using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;
using WebMap.Util;
using WebMap.World;

namespace WebMap.Tiles
{
    // Owns the tile pyramid: what exists on disk, what still needs rendering,
    // and the workers that render it.
    //
    // Scheduling, in priority order:
    //   * a tile a browser is asking for right now
    //   * the overview levels (zoom 0..prerender_zoom) for the whole world, once
    //   * re-renders of tiles whose ground changed (terraforming, felled trees)
    //   * close-zoom tiles over explored ground, nearest zoom-out first
    // Tiles over unexplored ground at close zoom are never rendered: nobody can
    // see them through the fog, and the world is big.
    //
    // Rendering runs on worker threads. The one thing that may not be safe off
    // the main thread is asking the WorldGenerator for heights: it is pure
    // arithmetic over immutable state and works on every server tried, but if
    // the engine ever objects the store notices the exception and moves just
    // that sampling step onto the main thread, sliced a few rows per frame, so
    // the game never stalls. `render_threads = 0` selects that mode outright.
    internal static class TileStore
    {
        private sealed class Job
        {
            public long key; public int zoom, x, y; public int priority; public long seq;
            public TileJob work;
            public ManualResetEventSlim sampled;    // main-thread sampling handshake
            public Exception sampleError;
        }

        private static string root;
        private static readonly object queueLock = new object();
        private static readonly List<Job> queue = new List<Job>();
        private static readonly Dictionary<long, Job> queued = new Dictionary<long, Job>();   // key -> waiting job (O(1) priority bumps)
        private static readonly HashSet<long> haveColor = new HashSet<long>();
        private static readonly HashSet<long> haveHeight = new HashSet<long>();
        private static readonly HashSet<long> haveVeg = new HashSet<long>();
        // bump when the tile look changes: the tiles it touches are dropped and re-rendered (DropStaleFormat)
        private const int TILE_FORMAT = 3;
        private static readonly List<long> redo = new List<long>();   // dropped close-zoom tiles, queued again on Start
        private static readonly ConcurrentDictionary<long, byte[]> memColor = new ConcurrentDictionary<long, byte[]>();   // small zooms only
        private static readonly ConcurrentQueue<Job> mainSampleQueue = new ConcurrentQueue<Job>();
        private static readonly List<string> notify = new List<string>();
        private static long seqCounter;
        private static Thread[] workers;
        private static volatile bool running;
        private static volatile bool mainThreadSampling;
        private static readonly AutoResetEvent wake = new AutoResetEvent(false);
        private static readonly object diskLock = new object();   // tile file writes (renderer vs recompress pass)
        // bump when the PNG encoding improves: tiles on disk are re-compressed once, same pixels
        private const int PNG_FORMAT = 1;

        public static int Rendered { get; private set; }
        public static double RenderMsTotal { get; private set; }
        public static int QueueLength { get { lock (queueLock) return queue.Count; } }
        public static bool MainThreadSampling => mainThreadSampling;
        public static int OnDisk { get { lock (queueLock) return haveColor.Count; } }

        public static void Init(string worldDataPath)
        {
            root = Path.Combine(worldDataPath, "tiles");
            // tiles of another grid size cover other squares under the same names: set them aside
            // (and render afresh) rather than show the world shifted
            try
            {
                string grid = Path.Combine(root, "grid.txt");
                bool any = Directory.Exists(Path.Combine(root, "map"));
                string have = File.Exists(grid) ? File.ReadAllText(grid).Trim() : (any ? "20480" : "");
                if (have != "" && have != TileMath.WORLD_SIZE.ToString())
                {
                    string old = root + "-grid" + have;
                    if (Directory.Exists(old)) Directory.Delete(old, true);
                    Directory.Move(root, old);
                    ZLog.Log($"MuninMap: map grid is now {TileMath.WORLD_SIZE} m (was {have}): old tiles moved to {Path.GetFileName(old)}, rendering afresh");
                }
                Directory.CreateDirectory(root);
                File.WriteAllText(grid, TileMath.WORLD_SIZE.ToString());
            }
            catch (Exception e) { ZLog.LogWarning("MuninMap: tile grid check: " + e.Message); }
            Directory.CreateDirectory(Path.Combine(root, "map"));
            Directory.CreateDirectory(Path.Combine(root, "height"));
            Directory.CreateDirectory(Path.Combine(root, "veg"));
            DropStaleFormat();
            lock (queueLock)
            {
                haveColor.Clear(); haveHeight.Clear(); haveVeg.Clear();
                Scan("map", haveColor);
                Scan("height", haveHeight);
                Scan("veg", haveVeg);
            }
            ZLog.Log($"MuninMap: tile store at {root}: {haveColor.Count} map tiles, {haveHeight.Count} height tiles on disk");
        }

        // Tiles rendered by an older look (format number in tiles/format.txt) are deleted where
        // the look changed, so they render again:
        //   2  close zooms (5+) of everything; overview tiles (zoom 0-4) are kept
        //   3  map tiles at every zoom that reach the Ashlands (its lava and ashen sea)
        private static void DropStaleFormat()
        {
            string marker = Path.Combine(root, "format.txt");
            int have = 0;
            try { if (File.Exists(marker)) int.TryParse(File.ReadAllText(marker).Trim(), out have); } catch { }
            if (have == TILE_FORMAT) return;
            int dropped = 0;
            for (int z = 0; z <= TileMath.MAX_ZOOM; z++)
                foreach (string layer in new[] { "map", "veg" })
                {
                    bool all = have < 2 && z >= 5;
                    if (!all && layer != "map") continue;
                    string dir = Path.Combine(root, layer, z.ToString());
                    if (!Directory.Exists(dir)) continue;
                    try
                    {
                        foreach (string f in TileFiles(dir))
                            if (all || TouchesAshlands(z, f))
                            {
                                File.Delete(f); dropped++;
                                // close zooms only render on demand: queue these again so nobody waits for them
                                if (layer == "map" && z > WebMapConfig.PRERENDER_ZOOM && TileName(f, out int tx, out int ty)) redo.Add(TileMath.Key(z, tx, ty));
                            }
                    }
                    catch { }
                }
            try { File.WriteAllText(marker, TILE_FORMAT.ToString()); } catch { }
            if (dropped > 0) ZLog.Log($"MuninMap: tile look changed, {dropped} tiles dropped for re-render");
        }

        // Whether tile file "x_y.ext" at zoom z reaches the Ashlands: its farthest corner is past the
        // Ashlands ring (WorldGenerator: 12000 m from (0, 4000), give or take 100 m of wobble)
        private static bool TouchesAshlands(int z, string file)
        {
            if (!TileName(file, out int x, out int y)) return false;
            TileMath.TileBounds(z, x, y, out float minX, out float minZ, out float maxX, out float maxZ);
            // farthest corner past the ring, nearest point still inside the world
            float fx = Math.Max(Math.Abs(minX), Math.Abs(maxX));
            float fz = Math.Max(Math.Abs(minZ - 4000f), Math.Abs(maxZ - 4000f));
            float nx = Math.Max(minX, Math.Min(0f, maxX)), nz = Math.Max(minZ, Math.Min(0f, maxZ));
            return fx * fx + fz * fz > 11850f * 11850f && nx * nx + nz * nz < 10500f * 10500f;
        }

        private static bool TileName(string file, out int x, out int y)
        {
            string n = Path.GetFileNameWithoutExtension(file);
            int us = n.IndexOf('_');
            x = y = 0;
            return us > 0 && int.TryParse(n.Substring(0, us), out x) && int.TryParse(n.Substring(us + 1), out y);
        }

        private static void Scan(string layer, HashSet<long> into)
        {
            string dir = Path.Combine(root, layer);
            if (!Directory.Exists(dir)) return;
            foreach (string zdir in Directory.GetDirectories(dir))
            {
                if (!int.TryParse(Path.GetFileName(zdir), out int z)) continue;
                foreach (string f in TileFiles(zdir))
                {
                    string n = Path.GetFileNameWithoutExtension(f);
                    int us = n.IndexOf('_');
                    if (us < 0) continue;
                    if (int.TryParse(n.Substring(0, us), out int x) && int.TryParse(n.Substring(us + 1), out int y))
                        into.Add(TileMath.Key(z, x, y));
                }
            }
        }

        public static void Start()
        {
            if (running) return;
            running = true;
            mainThreadSampling = WebMapConfig.RENDER_THREADS <= 0;
            try { TileJob.WaterLevel = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f; } catch { }
            int n = Math.Max(1, WebMapConfig.RENDER_THREADS);
            workers = new Thread[n];
            for (int i = 0; i < n; i++)
            {
                workers[i] = new Thread(WorkerLoop) { IsBackground = true, Name = "MuninMap tiles " + i, Priority = System.Threading.ThreadPriority.BelowNormal };
                workers[i].Start();
            }
            EnqueueOverview();
            // tiles a look change dropped, rendered again in the background after the overview
            foreach (long k in redo) { TileMath.Unkey(k, out int z, out int x, out int y); Enqueue(z, x, y, 40 + z, onlyIfMissing: true); }
            if (redo.Count > 0) ZLog.Log($"MuninMap: {redo.Count} close-zoom tiles queued to render again");
            redo.Clear();
            new Thread(BackgroundPasses) { IsBackground = true, Name = "MuninMap tile upkeep", Priority = System.Threading.ThreadPriority.Lowest }.Start();
            ZLog.Log($"MuninMap: tile workers started ({n} thread(s), main-thread sampling {(mainThreadSampling ? "on" : "off")})");
        }

        public static void Stop()
        {
            running = false;
            wake.Set();
        }

        // ---------------------------------------------------------------- scheduling

        private static void EnqueueOverview()
        {
            int top = Math.Min(WebMapConfig.PRERENDER_ZOOM, TileMath.MAX_ZOOM);
            int count = 0;
            for (int z = 0; z <= top; z++)
            {
                int n = TileMath.TilesPerSide(z);
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                        if (Enqueue(z, x, y, 10 + z, onlyIfMissing: true)) count++;
            }
            if (count > 0) ZLog.Log($"MuninMap: {count} overview tiles to render");
        }

        // A browser wants this tile and it does not exist yet.
        public static void RequestMissing(int zoom, int x, int y)
        {
            if (!TileMath.Valid(zoom, x, y)) return;
            if (zoom > WebMapConfig.PRERENDER_ZOOM && !TileTouchesExplored(zoom, x, y)) return;
            Enqueue(zoom, x, y, 5, onlyIfMissing: true);
        }

        // Fog revealed a pixel: make sure the close-zoom tiles over it exist.
        public static void OnExplored(float wx, float wz)
        {
            for (int z = WebMapConfig.PRERENDER_ZOOM + 1; z <= TileMath.MAX_ZOOM; z++)
            {
                if (z > WebMapConfig.MAX_RENDER_ZOOM) break;
                TileMath.WorldToTile(z, wx, wz, out int tx, out int ty);
                Enqueue(z, tx, ty, 25 + z, onlyIfMissing: true);
            }
        }

        // The ground in a zone changed: re-render the tiles that show it.
        public static void OnZoneChanged(int zx, int zz)
        {
            float cx = TileMath.ZoneCenter(zx), cz = TileMath.ZoneCenter(zz);
            for (int z = 4; z <= TileMath.MAX_ZOOM; z++)
            {
                // a zone can straddle up to four tiles at close zoom
                for (int dz = -1; dz <= 1; dz += 2)
                    for (int dx = -1; dx <= 1; dx += 2)
                    {
                        TileMath.WorldToTile(z, cx + dx * 31.9f, cz + dz * 31.9f, out int tx, out int ty);
                        bool exists; lock (queueLock) exists = haveColor.Contains(TileMath.Key(z, tx, ty));
                        if (exists) Enqueue(z, tx, ty, 18 + z, onlyIfMissing: false);
                    }
            }
        }

        // Re-render everything at and above a zoom (admin action, e.g. after a palette change).
        public static int Rerender(int fromZoom)
        {
            int n = 0;
            List<long> keys; lock (queueLock) keys = new List<long>(haveColor);
            foreach (long k in keys)
            {
                TileMath.Unkey(k, out int z, out int x, out int y);
                if (z >= fromZoom && Enqueue(z, x, y, 60 + z, onlyIfMissing: false)) n++;
            }
            return n;
        }

        private static bool Enqueue(int zoom, int x, int y, int priority, bool onlyIfMissing)
        {
            if (!TileMath.Valid(zoom, x, y)) return false;
            long key = TileMath.Key(zoom, x, y);
            lock (queueLock)
            {
                if (onlyIfMissing && haveColor.Contains(key) && (zoom > WebMapConfig.HeightMaxZoom || haveHeight.Contains(key))) return false;
                if (queued.TryGetValue(key, out var waiting))
                {
                    // already waiting (or rendering): maybe raise its priority
                    if (priority < waiting.priority) waiting.priority = priority;
                    return false;
                }
                var job = new Job { key = key, zoom = zoom, x = x, y = y, priority = priority, seq = ++seqCounter };
                queued[key] = job;
                queue.Add(job);
            }
            wake.Set();
            return true;
        }

        private static Job Take()
        {
            lock (queueLock)
            {
                if (queue.Count == 0) return null;
                int best = 0;
                for (int i = 1; i < queue.Count; i++)
                {
                    var a = queue[i]; var b = queue[best];
                    if (a.priority < b.priority || (a.priority == b.priority && a.seq < b.seq)) best = i;
                }
                var j = queue[best];
                queue.RemoveAt(best);
                return j;
            }
        }

        private static bool TileTouchesExplored(int zoom, int x, int y)
        {
            if (WebMapConfig.REVEAL_ALL) return true;
            TileMath.TileBounds(zoom, x, y, out float minX, out float minZ, out float maxX, out float maxZ);
            return Fog.AnyExplored(minX, minZ, maxX, maxZ);
        }

        // ---------------------------------------------------------------- rendering

        private static void WorkerLoop()
        {
            while (running)
            {
                Job job = Take();
                if (job == null) { wake.WaitOne(1000); continue; }
                try
                {
                    Render(job);
                }
                catch (Exception e)
                {
                    ZLog.LogWarning($"MuninMap: tile {job.zoom}/{job.x}/{job.y} failed: {e.Message}");
                }
                finally
                {
                    lock (queueLock) queued.Remove(job.key);
                }
            }
        }

        private static void Render(Job job)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool wantHeight = job.zoom <= WebMapConfig.HeightMaxZoom;
            job.work = new TileJob(job.zoom, job.x, job.y, wantHeight);

            if (!mainThreadSampling)
            {
                try
                {
                    job.work.SampleAll();
                }
                catch (Exception e) when (LooksLikeThreadRule(e))
                {
                    ZLog.LogWarning("MuninMap: the engine refused terrain sampling off the main thread; switching to sliced main-thread sampling. (" + e.Message + ")");
                    mainThreadSampling = true;
                    job.work = new TileJob(job.zoom, job.x, job.y, wantHeight);
                }
            }
            if (mainThreadSampling)
            {
                job.sampled = new ManualResetEventSlim(false);
                mainSampleQueue.Enqueue(job);
                job.sampled.Wait();
                if (job.sampleError != null) throw job.sampleError;
            }

            job.work.Compose();
            job.work.Encode();

            bool hasVeg = job.work.VegImage != null;
            string name = job.x + "_" + job.y;
            lock (diskLock)
            {
                WriteTile(Path.Combine(root, "map", job.zoom.ToString()), name, job.work.ColorImage);
                if (wantHeight) WriteAtomic(Path.Combine(root, "height", job.zoom.ToString()), name + ".png", job.work.HeightPng);
                if (hasVeg) WriteTile(Path.Combine(root, "veg", job.zoom.ToString()), name, job.work.VegImage);
            }
            if (WebP.Enabled)
            {
                toWebp.Enqueue(Path.Combine(root, "map", job.zoom.ToString(), name + ".png"));
                if (hasVeg) toWebp.Enqueue(Path.Combine(root, "veg", job.zoom.ToString(), name + ".png"));
                webpWake.Set();
            }

            lock (queueLock)
            {
                haveColor.Add(job.key);
                if (wantHeight) haveHeight.Add(job.key);
                if (hasVeg) haveVeg.Add(job.key);
            }
            if (job.zoom <= 4) memColor[job.key] = job.work.ColorImage;
            lock (notify) notify.Add(job.zoom + "/" + job.x + "/" + job.y);
            Rendered++;
            RenderMsTotal += sw.Elapsed.TotalMilliseconds;
            if (WebMapConfig.DEBUG) ZLog.Log($"MuninMap: tile {job.zoom}/{job.x}/{job.y} in {sw.ElapsedMilliseconds} ms");
        }

        private static bool LooksLikeThreadRule(Exception e)
        {
            string m = (e.Message ?? "") + " " + e.GetType().Name;
            return m.IndexOf("main thread", StringComparison.OrdinalIgnoreCase) >= 0
                || m.IndexOf("UnityException", StringComparison.Ordinal) >= 0;
        }

        private static void WriteAtomic(string dir, string name, byte[] data)
        {
            Directory.CreateDirectory(dir);
            string final = Path.Combine(dir, name);
            string tmp = final + ".tmp";
            File.WriteAllBytes(tmp, data);
            if (File.Exists(final)) File.Delete(final);
            File.Move(tmp, final);
        }

        // Close-zoom tiles from older versions were saved with fast PNG compression, 20-30% bigger
        // than they need to be. Re-compress them once in the background (same pixels, no re-render),
        // so existing worlds upload less too. Runs at the lowest priority with a pause per tile.
        private static void BackgroundPasses()
        {
            Thread.Sleep(30000);   // let startup and the overview render go first
            if (!WebP.Enabled) { RecompressPass(); return; }
            WebpPass();
            // then every freshly rendered tile, shortly after it was first served as PNG
            while (running && WebP.Enabled)
            {
                if (!toWebp.TryDequeue(out string f)) { webpWake.WaitOne(5000); continue; }
                try { ConvertToWebp(f); }
                catch (Exception e) { if (WebMapConfig.DEBUG) ZLog.LogWarning("MuninMap: WebP of " + f + " failed: " + e.Message); }
            }
        }

        private static void RecompressPass()
        {
            string marker = Path.Combine(root, "pngformat.txt");
            try { if (File.Exists(marker) && File.ReadAllText(marker).Trim() == PNG_FORMAT.ToString()) return; } catch { }
            int done = 0; long saved = 0;
            try
            {
                foreach (string layer in new[] { "map", "veg" })
                    for (int z = 5; z <= TileMath.MAX_ZOOM; z++)
                    {
                        string dir = Path.Combine(root, layer, z.ToString());
                        if (!Directory.Exists(dir)) continue;
                        foreach (string f in Directory.GetFiles(dir, "*.png"))
                        {
                            if (!running) return;
                            byte[] old, smaller;
                            try { old = File.ReadAllBytes(f); } catch (IOException) { continue; }
                            smaller = Png.Recompress(old, filter: layer == "map");
                            if (smaller != null)
                                lock (diskLock)
                                {
                                    // skip it if the renderer replaced the file meanwhile
                                    byte[] now;
                                    try { now = File.ReadAllBytes(f); } catch (IOException) { continue; }
                                    if (now.Length != old.Length || Fnv1a(now) != Fnv1a(old)) continue;
                                    WriteAtomic(dir, Path.GetFileName(f), smaller);
                                    done++; saved += old.Length - smaller.Length;
                                }
                            Thread.Sleep(15);
                        }
                    }
                File.WriteAllText(marker, PNG_FORMAT.ToString());
                if (done > 0) ZLog.Log($"MuninMap: re-compressed {done} tiles, {saved / 1048576.0:F1} MB smaller");
            }
            catch (Exception e) { ZLog.LogWarning("MuninMap: tile re-compress stopped: " + e.Message); }
        }

        // PNG map/overlay tiles still on disk (older versions, or rendered before a restart) become
        // WebP (same pixels) and the PNG goes. Lowest priority and paced.
        private static void WebpPass()
        {
            int made = 0; long png = 0, webp = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                foreach (string layer in new[] { "map", "veg" })
                    for (int z = 0; z <= TileMath.MAX_ZOOM; z++)
                    {
                        string dir = Path.Combine(root, layer, z.ToString());
                        if (!Directory.Exists(dir)) continue;
                        foreach (string f in Directory.GetFiles(dir, "*.png"))
                        {
                            if (!running || !WebP.Enabled) return;
                            if (!ConvertToWebp(f, out int src, out int w)) continue;
                            made++; png += src; webp += w;
                            Thread.Sleep(5);
                        }
                    }
                if (made > 0) ZLog.Log($"MuninMap: converted {made} PNG tiles to WebP in {sw.Elapsed.TotalSeconds:F0} s, {png / 1048576.0:F1} MB -> {webp / 1048576.0:F1} MB");
            }
            catch (Exception e) { ZLog.LogWarning("MuninMap: WebP conversion stopped: " + e.Message); }
        }

        private static readonly ConcurrentQueue<string> toWebp = new ConcurrentQueue<string>();
        private static readonly AutoResetEvent webpWake = new AutoResetEvent(false);

        private static bool ConvertToWebp(string f) => ConvertToWebp(f, out _, out _);

        // one PNG tile to WebP; false when it's gone, changed meanwhile, or WebP failed
        private static bool ConvertToWebp(string f, out int pngBytes, out int webpBytes)
        {
            pngBytes = webpBytes = 0;
            byte[] src;
            try { if (!File.Exists(f)) return false; src = File.ReadAllBytes(f); } catch (IOException) { return false; }
            byte[] w = WebP.FromPng(src);
            if (w == null) return false;
            lock (diskLock)
            {
                byte[] now;
                try { now = File.ReadAllBytes(f); } catch (IOException) { return false; }   // re-rendered meanwhile
                if (now.Length != src.Length || Fnv1a(now) != Fnv1a(src)) return false;
                WriteTile(Path.GetDirectoryName(f), Path.GetFileNameWithoutExtension(f), w);
            }
            string zdir = Path.GetFileName(Path.GetDirectoryName(f)), layer = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(f)));
            if (layer == "map" && int.TryParse(zdir, out int z) && z <= 4) memColor[TileKeyOf(z, f)] = w;
            pngBytes = src.Length; webpBytes = w.Length;
            return true;
        }

        private static long TileKeyOf(int z, string file)
        {
            string n = Path.GetFileNameWithoutExtension(file);
            int us = n.IndexOf('_');
            int.TryParse(n.Substring(0, us), out int x); int.TryParse(n.Substring(us + 1), out int y);
            return TileMath.Key(z, x, y);
        }

        // a map/overlay tile, saved under the extension its bytes call for; the other format's file goes
        private static void WriteTile(string dir, string name, byte[] data)
        {
            bool webp = WebP.IsWebp(data);
            WriteAtomic(dir, name + (webp ? ".webp" : ".png"), data);
            string other = Path.Combine(dir, name + (webp ? ".png" : ".webp"));
            try { if (File.Exists(other)) File.Delete(other); } catch { }
        }

        private static IEnumerable<string> TileFiles(string dir)
        {
            foreach (string f in Directory.GetFiles(dir, "*.png")) yield return f;
            foreach (string f in Directory.GetFiles(dir, "*.webp")) yield return f;
        }

        internal static uint Fnv1a(byte[] d)
        {
            uint h = 2166136261u;
            for (int i = 0; i < d.Length; i++) { h ^= d[i]; h *= 16777619u; }
            return h;
        }

        // Main thread. Samples a few rows of whichever tile is waiting, every frame.
        public static IEnumerator MainThreadPump()
        {
            int rowsPerFrame = Math.Max(4, WebMapConfig.MAIN_THREAD_ROWS_PER_FRAME);
            while (true)
            {
                if (!mainThreadSampling || !mainSampleQueue.TryPeek(out Job job))
                {
                    yield return new WaitForSeconds(0.25f);
                    continue;
                }
                try
                {
                    int from = job.work.SampledRows;
                    int to = Math.Min(TileJob.S, from + rowsPerFrame);
                    job.work.SampleRows(from, to);
                    if (to >= TileJob.S)
                    {
                        mainSampleQueue.TryDequeue(out _);
                        job.sampled.Set();
                    }
                }
                catch (Exception e)
                {
                    job.sampleError = e;
                    mainSampleQueue.TryDequeue(out _);
                    job.sampled.Set();
                }
                yield return null;
            }
        }

        // ---------------------------------------------------------------- serving

        // Returns the image for a tile (WebP or PNG, see WebP.IsWebp), or null if it does not exist (yet). A miss
        // for a tile that should exist queues it.
        public static byte[] Get(string layer, int zoom, int x, int y, out string etag)
        {
            etag = null;
            if (!TileMath.Valid(zoom, x, y)) return null;
            long key = TileMath.Key(zoom, x, y);
            bool height = layer == "height", vegL = layer == "veg";
            if (vegL && zoom < 5) return null;   // no overlay at overview zooms
            bool have; lock (queueLock) have = height ? haveHeight.Contains(key) : vegL ? haveVeg.Contains(key) : haveColor.Contains(key);
            if (!have)
            {
                if (!height || zoom <= WebMapConfig.HeightMaxZoom) RequestMissing(zoom, x, y);
                return null;
            }
            bool color = !height && !vegL;
            byte[] data;
            if (!(color && memColor.TryGetValue(key, out data)))
            {
                string path = Path.Combine(root, layer, zoom.ToString(), x + "_" + y);
                try { data = !height && File.Exists(path + ".webp") ? File.ReadAllBytes(path + ".webp") : File.ReadAllBytes(path + ".png"); }
                catch (IOException) { return null; }
                if (color && zoom <= 4) memColor[key] = data;
            }
            // from the bytes, so it survives restarts: a browser's copy stays valid (304, no body)
            // exactly as long as the tile is unchanged
            etag = "\"" + layer[0] + data.Length.ToString("x") + "-" + Fnv1a(data).ToString("x") + "\"";
            return data;
        }

        public static List<string> DrainNotifications()
        {
            lock (notify)
            {
                if (notify.Count == 0) return null;
                var l = new List<string>(notify);
                notify.Clear();
                return l;
            }
        }

        public static string StatusJson()
        {
            var j = new JsonWriter();
            j.BeginObject();
            j.Prop("onDisk", OnDisk);
            j.Prop("queued", QueueLength);
            j.Prop("rendered", Rendered);
            j.Prop("avgMs", Rendered > 0 ? RenderMsTotal / Rendered : 0.0, 1);
            j.Prop("mainThreadSampling", mainThreadSampling);
            j.Prop("threads", workers != null ? workers.Length : 0);
            j.Prop("maxZoom", TileMath.MAX_ZOOM);
            j.Prop("prerenderZoom", WebMapConfig.PRERENDER_ZOOM);
            j.Prop("maxRenderZoom", WebMapConfig.MAX_RENDER_ZOOM);
            j.Prop("heightMaxZoom", WebMapConfig.HeightMaxZoom);
            j.End();
            return j.ToString();
        }
    }
}
