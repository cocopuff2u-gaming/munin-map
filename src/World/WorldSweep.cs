using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MuninMap.Tiles;

namespace MuninMap.World
{
    // The one walk over every object in the world.
    //
    // Everything the map knows about the world's contents -- buildings,
    // trees, terraforming, portals, tombstones, boats -- comes from this
    // sweep, so the ZDO table is only ever walked once per cycle. It runs on
    // the main thread (ZDOs are not safe anywhere else) a few milliseconds of
    // each frame (Due), and the collectors it feeds publish their results at
    // the end, chunk by chunk, also a few milliseconds a frame: each chunk is
    // whole, and a chunk's rev always comes with its own content.
    //
    // Zones whose ground changed (terraforming, felled or planted trees) are
    // handed to the tile store for re-rendering.
    internal static class WorldSweep
    {
        private static readonly int terrainCompilerHash = "_TerrainCompiler".GetStableHashCode();
        private static readonly int mapTableHash = "piece_cartographytable".GetStableHashCode();
        private static readonly int tombstoneHash = "Player_tombstone".GetStableHashCode();
        private static readonly Dictionary<int, string> nameCache = new Dictionary<int, string>();
        private static bool sweeping;

        public static volatile bool RefreshRequested;
        public static int LastScanned { get; private set; }
        public static double LastSweepSeconds { get; private set; }
        public static DateTime LastSweepUtc { get; private set; }
        public static int Sweeps { get; private set; }
        public static double LastLongestMs { get; private set; }   // the longest single frame the last sweep took

        // The sweep's share of a server frame. Players' movement runs on the same thread, so a frame
        // that takes 100 ms is a 100 ms hitch for everyone near the server: the walk and the work after
        // it (sorting a million trees, building chunk JSON) stop for the next frame when their time is
        // up.
        private static readonly System.Diagnostics.Stopwatch slice = new System.Diagnostics.Stopwatch();
        private static double budgetMs = 4.0, longestMs, stepMs;
        private static string part = "", longestPart = "";   // what the sweep was doing in its longest frame
        // garbage collections: the runtime stops everything to collect, which lands in whatever frame is running
        private static int gcAtResume, gcFrames;
        private static bool longestGc;
        private static int gcAtEnd = -1;
        private static float endTime;
        public static bool Due { get { NewFrame(); return slice.Elapsed.TotalMilliseconds >= budgetMs; } }
        // A slice is this frame's share. Unity may resume the sweep a frame later than it paused (after a
        // step finishes), so a new frame always starts a new slice: the frame in between isn't the sweep's.
        private static int sliceFrame;
        private static void NewFrame()
        {
            if (Time.frameCount == sliceFrame) return;
            slice.Restart(); sliceFrame = Time.frameCount; gcAtResume = GC.CollectionCount(0);
        }
        // after a yield: the next frame starts a fresh slice
        public static void Resume() { slice.Restart(); sliceFrame = Time.frameCount; gcAtResume = GC.CollectionCount(0); }
        private static void EndSlice()
        {
            NewFrame();
            double ms = slice.Elapsed.TotalMilliseconds;
            if (ms > stepMs) stepMs = ms;
            bool gc = GC.CollectionCount(0) != gcAtResume;
            if (gc) gcFrames++;
            if (ms > longestMs) { longestMs = ms; longestPart = part; longestGc = gc; }
        }
        // a yield for the steps that run between: `if (WorldSweep.Due) yield return WorldSweep.Pause();`
        public static object Pause() { EndSlice(); return null; }

        // A fingerprint of a zone's or chunk's contents that doesn't depend on their order: each
        // item's own hash, mixed, then summed. Comparing it says whether anything changed without
        // sorting the list first; only what changed gets sorted and copied.
        public static long Mix(int v)
        {
            uint x = unchecked((uint)v);
            x ^= x >> 16; x = unchecked(x * 0x7feb352d); x ^= x >> 15; x = unchecked(x * 0x846ca68b); x ^= x >> 16;
            return x;
        }
        public static int Fold(long sum, int count) => unchecked((int)(sum ^ (sum >> 32)) * 31 + count);

        public static IEnumerator Loop()
        {
            float loopStart = Time.time;   // game time, like WaitForSeconds: it barely advances while the world is still loading
            yield return new WaitForSeconds(MuninConfig.FIRST_SWEEP_DELAY);
            // one extra sweep shortly after start: the first one runs early so the map fills in quickly,
            // and this catches anything that was still loading when it did
            bool early = MuninConfig.SECOND_SWEEP_DELAY > MuninConfig.FIRST_SWEEP_DELAY;
            while (true)
            {
                yield return Sweep();
                if (early)
                {
                    early = false;
                    float wait = MuninConfig.SECOND_SWEEP_DELAY - (Time.time - loopStart);
                    if (wait > 0f) yield return new WaitForSeconds(wait);
                    continue;
                }
                float waited = 0f;
                while (waited < MuninConfig.SWEEP_INTERVAL && !RefreshRequested && !StructureMap.RefreshRequested)
                {
                    yield return new WaitForSeconds(1f);
                    waited += 1f;
                }
                RefreshRequested = false;
                StructureMap.RefreshRequested = false;
            }
        }

        public static string NameOf(int prefabHash)
        {
            if (nameCache.TryGetValue(prefabHash, out string n)) return n;
            n = null;
            try
            {
                var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabHash) : null;
                if (go != null) n = go.name;
            }
            catch { }
            nameCache[prefabHash] = n;
            return n;
        }

        private static IEnumerator Sweep()
        {
            if (sweeping) yield break;
            sweeping = true;
            var started = DateTime.UtcNow;
            float t0 = Time.realtimeSinceStartup;
            longestMs = 0; part = "list"; gcFrames = 0; longestGc = false;
            int gcStart = GC.CollectionCount(0), frameStart = Time.frameCount;
            string between = gcAtEnd >= 0 ? $"; {gcStart - gcAtEnd} garbage collections in the {Time.realtimeSinceStartup - endTime:0}s since the last sweep" : "";
            slice.Restart();

            // The game keeps every object in a list per 64 m sector (portals in a list of their own):
            // walked a sector at a time, copying only that sector's list, where a copy of the whole
            // table (3 million objects on a big world) held the game up ~150 ms. Something that moves
            // to a sector not walked yet may be seen twice (Vehicles counts each boat once).
            List<ZDO>[] bySector = null;
            Dictionary<ZoneSystem.SectorIndex, List<ZDO>> portalLists = null;
            try { bySector = ZDOMan.instance.m_objectsBySector; portalLists = ZDOMan.instance.m_portalObjects; }
            catch (Exception e) { ZLog.LogWarning("MuninMap: could not list ZDOs: " + e.Message); }
            if (bySector == null) { sweeping = false; yield break; }
            var batch = new List<ZDO>(1024);

            int size = MuninConfig.TEXTURE_SIZE, half = size / 2, pixel = MuninConfig.PIXEL_SIZE;
            int perFrame = Math.Max(500, MuninConfig.SWEEP_ZDOS_PER_FRAME);
            budgetMs = Math.Max(1.0, MuninConfig.SWEEP_FRAME_MS);
            yield return Pause(); Resume();

            part = "start";
            Structures.Begin();
            Ruins.Begin();
            Vegetation.Begin();
            Vehicles.Begin();
            Markers.Begin();
            Dungeons.Begin(Markers.IsDungeon);
            StructureMap.Begin();
            ForestMap.Begin();
            WorldObjects.Begin();
            Demisters.Begin();
            Hotspots.Begin();
            var changedZones = new HashSet<long>();
            var mapTables = new List<ZDO>();

            yield return Pause(); Resume();

            int seen = 0;
            part = "walk";
            for (int sector = 0; sector <= bySector.Length; sector++)
            {
                batch.Clear();
                if (sector < bySector.Length) { var l = bySector[sector]; if (l == null || l.Count == 0) continue; batch.AddRange(l); }
                else if (portalLists != null) foreach (var l in portalLists.Values) batch.AddRange(l);
                foreach (var zdo in batch)
                {
                    seen++;
                    if (zdo != null)
                    {
                        try
                        {
                            Vector3 p = zdo.GetPosition();
                            int pref = zdo.GetPrefab();
                            if (pref == mapTableHash) mapTables.Add(zdo);   // read after the walk, a slice at a time
                            if (Runestones.IsProxy(pref)) { try { Markers.ObserveProxy(zdo, p); } catch { } }
                            if (pref == tombstoneHash) Fog.AddTrace(p);   // where a player died   // recorded maps: exact explored areas (and still a building piece below)
                            if (pref == terrainCompilerHash)
                            {
                                if (TerrainPatches.Observe(zdo, p))
                                    changedZones.Add(TileMath.ZoneKey(TileMath.ZoneCoord(p.x), TileMath.ZoneCoord(p.z)));
                            }
                            else
                            {
                                long creator = 0L;
                                try { creator = zdo.GetLong(ZDOVars.s_creator, 0L); } catch { }
                                Dungeons.Observe(zdo, pref, p, creator);
                                Hotspots.Observe(pref, creator, p);   // what weighs on players' frame rates, per zone
                                Demisters.Observe(pref, p);   // Dvergr lanterns: clearings in the mist   // inside a dungeon (and the gates at their doors)
                                // the 3D object data (and the model export it requests) only matters with 3D on
                                if (MuninConfig.ENABLE_3D) WorldObjects.Observe(zdo, pref, p, creator);
                                int lx = Mathf.RoundToInt(p.x / pixel + half);
                                int ly = Mathf.RoundToInt(p.z / pixel + half);
                                bool inLegacy = lx >= 0 && ly >= 0 && lx < size && ly < size;
                                int legacyIdx = ly * size + lx;

                                if (creator != 0L)
                                {
                                    Fog.AddTrace(p);   // built or moved by a player: someone stood there
                                    var veh = Vehicles.Classify(pref);
                                    if (veh != Vehicles.Kind.None) Vehicles.Observe(zdo.m_uid, pref, veh, p);
                                    else if (!Markers.Observe(zdo, NameOf(pref), p))
                                    {
                                        Structures.Observe(zdo, pref, p, creator);
                                        if (inLegacy) StructureMap.Observe(pref, legacyIdx);
                                    }
                                }
                                else if (!Markers.Observe(zdo, NameOf(pref), p))
                                {
                                    if (Vegetation.Observe(pref, p)) { if (inLegacy) ForestMap.Observe(pref, legacyIdx); }
                                    else Ruins.Observe(zdo, pref, p);
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            if (MuninConfig.DEBUG) ZLog.LogWarning("MuninMap: sweep skipped an object: " + e.Message);
                        }
                    }
                    if ((seen & 63) == 0 && (Due || seen % perFrame == 0)) { yield return Pause(); Resume(); }
                }
            }

            // the collectors publish their results, each a slice at a time
            var steps = new System.Diagnostics.Stopwatch();
            var took = new List<string>();
            IEnumerator Step(string name, IEnumerator work)
            {
                if (Due) { yield return Pause(); Resume(); }
                steps.Restart(); part = name; stepMs = 0;
                while (work.MoveNext()) { yield return Pause(); Resume(); }
                EndSlice();
                if (steps.Elapsed.TotalMilliseconds >= 20) took.Add($"{name} {steps.Elapsed.TotalMilliseconds:0}/{stepMs:0.0}ms");
            }
            IEnumerator Once(Action a) { a(); yield break; }
            // cartography tables: their explored map and pins, each table unpacked once and only when it changed
            IEnumerator MapTables(List<ZDO> tables)
            {
                foreach (var zdo in tables)
                {
                    if (Due) yield return null;
                    byte[] raw;
                    try { raw = zdo.GetByteArray(ZDOVars.s_data); } catch { continue; }
                    bool fog = Fog.TableChanged(zdo, raw), pins = Markers.ObserveMapTable(zdo, raw);
                    if (!fog && !pins) continue;
                    // unpacking 4 MB took most of a frame: done on a worker thread, the game waits for nothing
                    var unpack = System.Threading.Tasks.Task.Run(() => Utils.Decompress(raw));
                    while (!unpack.IsCompleted) yield return null;
                    byte[] data;
                    try { data = unpack.Result; } catch (Exception e) { ZLog.LogWarning("MuninMap: map table not readable: " + (e.InnerException ?? e).Message); continue; }
                    if (pins) Markers.ReadTablePins(zdo, data);
                    if (fog)
                    {
                        var merge = Fog.MergeMapTable(zdo, data);
                        while (true)
                        {
                            try { if (!merge.MoveNext()) break; } catch (Exception e) { ZLog.LogWarning("MuninMap: map table: " + e.Message); break; }
                            yield return null;
                        }
                    }
                }
            }
            yield return Step("map tables", MapTables(mapTables));
            yield return Step("traces", Once(() => Fog.RevealTraces()));
            yield return Step("trees", Vegetation.Finish(changedZones));
            yield return Step("buildings", Structures.Finish());
            int changedChunks = Structures.LastChanged;
            yield return Step("ruins", Ruins.Finish());
            Vehicles.Finish(); Dungeons.Finish(); Demisters.Finish();
            yield return Step("hotspots", Once(Hotspots.Finish));
            yield return Step("markers", Markers.Finish());
            yield return Step("structure map", Once(StructureMap.Finish));
            StructureMap.LastScanned = seen;
            yield return Step("forest map", Once(ForestMap.Finish));
            yield return Step("3D objects", WorldObjects.Finish());
            int changedObjectChunks = WorldObjects.LastChanged;
            EndSlice();

            // The first sweep after a start only establishes the baseline: the
            // collectors' change hashes are empty, so every wood and moat would
            // otherwise count as "changed" and every close-zoom tile on disk would
            // be re-rendered at each restart.
            int rerendered = 0;
            if (Sweeps > 0)
                foreach (long key in changedZones)
                {
                    TileMath.UnzoneKey(key, out int zx, out int zz);
                    TileStore.OnZoneChanged(zx, zz);
                    rerendered++;
                }

            LastScanned = seen;
            LastSweepSeconds = Time.realtimeSinceStartup - t0;
            LastLongestMs = longestMs;
            LastSweepUtc = started;
            Sweeps++;
            sweeping = false;
            ZLog.Log($"MuninMap: world sweep #{Sweeps}: {seen} objects in {LastSweepSeconds:0.0}s -> {Structures.Total} pieces ({changedChunks} chunks changed), "
                   + $"{Vegetation.LastTrees} trees, {Vegetation.LastRocks} rocks, {TerrainPatches.Count} terraformed zones, {rerendered} zones re-rendered, "
                   + $"{WorldObjects.Total} 3D objects ({changedObjectChunks} chunks changed), {Models.ModelStore.QueueLength} models to export; "
                   + $"longest frame {longestMs:0.0}ms in {longestPart}{(longestGc ? " (with a garbage collection)" : "")}, {GC.CollectionCount(0) - gcStart} garbage collections ({gcFrames} in sweep frames), server at {(Time.frameCount - frameStart) / Math.Max(0.1, LastSweepSeconds):0} fps" + (took.Count > 0 ? " (" + string.Join(", ", took) + ")" : "")
                   + (Markers.SlowParts() is string slow && slow.Length > 0 ? "; " + slow : ""));
            if (between.Length > 0) ZLog.Log("MuninMap: sweep #" + Sweeps + between);
            gcAtEnd = GC.CollectionCount(0); endTime = Time.realtimeSinceStartup;
            Live.Stats.OnSweep();
            MapDataServer.getInstance()?.BroadcastWorldRevision();
        }
    }
}
