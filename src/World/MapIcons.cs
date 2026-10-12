using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;
using MuninMap.Models;
using MuninMap.Models.Unity;
using MuninMap.Util;

namespace MuninMap.World
{
    // The game's own map icons (the spawn stones, traders, the pin types, death, boss...) for the web
    // map. The game keeps them as sprites in one UI atlas; a dedicated server has the Minimap and
    // knows each sprite's rectangle, but with no graphics device the atlas pixels can't be read
    // through Unity. So the atlas is found in the game's own files and decoded (TextureExtractor,
    // BC7) once, each icon cut out to map_data/icons/<sprite>.png, with icons.json listing them by
    // location or pin type. Nothing of the game's art is shipped; a game update (new atlas name)
    // extracts again.
    internal static class MapIcons
    {
        private static readonly BepInEx.Logging.ManualLogSource log = BepInEx.Logging.Logger.CreateLogSource("MuninMap.icons");
        private sealed class Entry { public string key, sprite; public Rect rect; }

        public static string Dir { get; private set; }
        public static volatile string ManifestJson = "{\"icons\":{}}";

        public static void Start(string mapDataPath)
        {
            try
            {
                Dir = Path.Combine(mapDataPath, "icons");
                Directory.CreateDirectory(Dir);
                var mm = Minimap.instance ?? Resources.FindObjectsOfTypeAll<Minimap>().FirstOrDefault();
                if (mm == null) { log.LogInfo("no Minimap on this server: using the map's own icons"); return; }
                var entries = new List<Entry>();
                string atlas = null;
                void Add(string key, Sprite s)
                {
                    if (s == null || s.texture == null) return;
                    if (atlas == null) atlas = s.texture.name;
                    if (s.texture.name != atlas) return;   // all of them live in the one atlas today
                    entries.Add(new Entry { key = key, sprite = s.name, rect = s.textureRect });
                }
                foreach (var l in mm.m_locationIcons) Add(l.m_name, l.m_icon);
                foreach (var i in mm.m_icons) Add("pin:" + i.m_name, i.m_icon);
                Add("pin:Checked", ChildSprite(mm.m_pinPrefab, "Checked"));   // the cross over a pin ticked off on the map
                StartBackground(mm, Application.dataPath);
                if (atlas == null || entries.Count == 0) return;
                string manifest = BuildManifest(atlas, entries);
                string mf = Path.Combine(Dir, "icons.json");
                if (File.Exists(mf) && File.ReadAllText(mf) == manifest && entries.All(e => File.Exists(Path.Combine(Dir, e.sprite + ".png"))))
                {
                    ManifestJson = manifest;
                    log.LogInfo($"{entries.Count} game map icons ready");
                    return;
                }
                string dataDir = Application.dataPath;
                new Thread(() => Extract(dataDir, atlas, entries, manifest, mf)) { IsBackground = true, Name = "MuninMap icons", Priority = System.Threading.ThreadPriority.BelowNormal }.Start();
            }
            catch (Exception e) { log.LogWarning("map icons: " + e.Message); }
        }

        // the sprite of a UI Image on a child of a prefab (UnityEngine.UI isn't referenced: by reflection)
        private static Sprite ChildSprite(GameObject prefab, string child)
        {
            try
            {
                var t = prefab != null ? prefab.transform.Find(child) : null;
                var img = t != null ? t.GetComponent("Image") : null;
                return img?.GetType().GetProperty("sprite")?.GetValue(img, null) as Sprite;
            }
            catch { return null; }
        }

        // The in-game map's own textures (what it draws past the explored world, its paper and
        // clouds): the large map's material names them; each one found in the game files is saved
        // as map_data/icons/mapbg_<property>.png, listed in mapbg.json for the page.
        public static volatile string BackgroundJson = "{}";
        private static void StartBackground(Minimap mm, string dataDir)
        {
            try
            {
                // the large map is a UI RawImage (UnityEngine.UI, not referenced here): its material by reflection
                object img = typeof(Minimap).GetField("m_mapImageLarge")?.GetValue(mm);
                Material mat = img?.GetType().GetProperty("material")?.GetValue(img, null) as Material;
                if (mat == null) { log.LogInfo("map background: no large map material"); return; }
                var want = new List<KeyValuePair<string, Texture>>();
                foreach (string prop in mat.GetTexturePropertyNames())
                {
                    Texture t = null;
                    try { t = mat.GetTexture(prop); } catch { }
                    log.LogInfo($"map background: {mat.shader?.name} {prop} = {(t == null ? "-" : t.name + " " + t.width + "x" + t.height)}");
                    if (t != null && !string.IsNullOrEmpty(t.name)) want.Add(new KeyValuePair<string, Texture>(prop, t));
                }
                foreach (string f in new[] { "_Color", "_WaterColor", "_FogColor", "_BackgroundColor" })
                    if (mat.HasProperty(f)) log.LogInfo($"map background: {f} = {mat.GetColor(f)}");
                var names = want.Select((kv) => new KeyValuePair<string, string>(kv.Key, kv.Value.name)).ToList();
                string mf = Path.Combine(Dir, "mapbg.json");
                new Thread(() =>
                {
                    var j = new JsonWriter(512); j.BeginObject();
                    foreach (var kv in names)
                    {
                        string file = "mapbg" + kv.Key.ToLowerInvariant() + ".png", path = Path.Combine(Dir, file);
                        try
                        {
                            if (!File.Exists(path))
                            {
                                byte[] rgba = TextureExtractor.FindTexture(dataDir, kv.Value, out int w, out int h);
                                if (rgba == null) { log.LogInfo($"map background: {kv.Value} not in the game files (made at run time)"); continue; }
                                var flipped = new byte[rgba.Length];   // texture rows run bottom-up
                                for (int r = 0; r < h; r++) Buffer.BlockCopy(rgba, (h - 1 - r) * w * 4, flipped, r * w * 4, w * 4);
                                File.WriteAllBytes(path, Png.Encode(flipped, w, h, Png.Format.RGBA));
                                log.LogInfo($"map background: {kv.Key} ({kv.Value}, {w}x{h}) saved");
                            }
                            j.Prop(kv.Key, file);
                        }
                        catch (Exception e) { log.LogWarning($"map background {kv.Key}: {e.Message}"); }
                    }
                    j.End();
                    BackgroundJson = j.ToString();
                    try { File.WriteAllText(mf, BackgroundJson); } catch { }
                }) { IsBackground = true, Name = "MuninMap map background", Priority = System.Threading.ThreadPriority.BelowNormal }.Start();
            }
            catch (Exception e) { log.LogWarning("map background: " + e.Message); }
        }

        private static string BuildManifest(string atlas, List<Entry> entries)
        {
            var j = new JsonWriter(2048);
            j.BeginObject().Prop("atlas", atlas).Key("icons").BeginObject();
            foreach (var e in entries) j.Prop(e.key, e.sprite);
            j.End().End();
            return j.ToString();
        }

        private static void Extract(string dataDir, string atlas, List<Entry> entries, string manifest, string mf)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                byte[] rgba = TextureExtractor.FindTexture(dataDir, atlas, out int w, out int h);
                if (rgba == null) { log.LogWarning($"map icons: atlas {atlas} not found in the game files; using the map's own icons"); return; }
                foreach (var e in entries)
                {
                    int x0 = Mathf.Clamp(Mathf.RoundToInt(e.rect.x), 0, w), y0 = Mathf.Clamp(Mathf.RoundToInt(e.rect.y), 0, h);
                    int cw = Mathf.Clamp(Mathf.RoundToInt(e.rect.width), 1, w - x0), ch = Mathf.Clamp(Mathf.RoundToInt(e.rect.height), 1, h - y0);
                    var px = new byte[cw * ch * 4];
                    for (int r = 0; r < ch; r++)   // atlas rows run bottom-up, like the rect's y
                        Buffer.BlockCopy(rgba, ((y0 + ch - 1 - r) * w + x0) * 4, px, r * cw * 4, cw * 4);
                    File.WriteAllBytes(Path.Combine(Dir, e.sprite + ".png"), Png.Encode(px, cw, ch, Png.Format.RGBA));
                }
                File.WriteAllText(mf, manifest);
                ManifestJson = manifest;
                log.LogInfo($"{entries.Count} game map icons extracted in {sw.Elapsed.TotalSeconds:F0} s");
            }
            catch (Exception e) { log.LogWarning("map icons: extraction failed: " + e.Message); }
        }
    }
}
