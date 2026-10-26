using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using UnityEngine;
using MuninMap.Models;
using MuninMap.Util;

namespace MuninMap.World
{
    // The game's own item icons (the ones in your inventory), for what a player wears on their card.
    // Like the map icons (MapIcons): a dedicated server knows each item's sprite and its rectangle in
    // a UI atlas but can't read the pixels through Unity, so each atlas is found in the game's files
    // and decoded once, on a low-priority thread, and every item's icon cut out to
    // map_data/items/<prefab>.png at 64 px. items.txt records the atlases they came from: the same
    // list next start (no game update) means nothing to do.
    internal static class ItemIcons
    {
        private static readonly BepInEx.Logging.ManualLogSource log = BepInEx.Logging.Logger.CreateLogSource("MuninMap.items");
        private sealed class Entry { public string prefab; public Rect rect; }
        private const int Size = 64;

        public static string Dir { get; private set; }
        public static volatile string NamesJson = "{}";   // prefab -> the game's name for it (data/items.json)

        public static void Start(string mapDataPath)
        {
            try
            {
                Dir = Path.Combine(mapDataPath, "items");
                Directory.CreateDirectory(Dir);
                var db = ObjectDB.instance;
                if (db == null || db.m_items == null) return;
                var byAtlas = new Dictionary<string, List<Entry>>();
                var names = new JsonWriter(16384);
                names.BeginObject();
                foreach (var go in db.m_items)
                {
                    try
                    {
                        var drop = go != null ? go.GetComponent<ItemDrop>() : null;
                        string token = drop?.m_itemData?.m_shared?.m_name;
                        if (!string.IsNullOrEmpty(token))
                        {
                            string shown = token;
                            try { shown = Live.WorldEvents.LocalizeFn(token); } catch { }   // (the game's Localization, by reflection)
                            if (!string.IsNullOrEmpty(shown) && !shown.StartsWith("$", StringComparison.Ordinal)) names.Prop(go.name, shown);
                        }
                        var icons = drop?.m_itemData?.m_shared?.m_icons;
                        if (icons == null || icons.Length == 0 || icons[0] == null || icons[0].texture == null) continue;
                        string name = Safe(go.name);
                        if (name.Length == 0) continue;
                        string atlas = icons[0].texture.name;
                        if (!byAtlas.TryGetValue(atlas, out var list)) byAtlas[atlas] = list = new List<Entry>();
                        list.Add(new Entry { prefab = name, rect = icons[0].textureRect });
                    }
                    catch { }   // (a sprite packed tightly has no rectangle: that item goes without)
                }
                names.End();
                NamesJson = names.ToString();
                int count = byAtlas.Values.Sum(l => l.Count);
                if (count == 0) return;
                var sb = new StringBuilder();
                foreach (var kv in byAtlas.OrderBy(k => k.Key, StringComparer.Ordinal)) sb.Append(kv.Key).Append(' ').Append(kv.Value.Count).Append('\n');
                string manifest = sb.ToString(), mf = Path.Combine(Dir, "items.txt");
                if (File.Exists(mf) && File.ReadAllText(mf) == manifest) { log.LogInfo($"{count} item icons ready"); return; }
                string dataDir = Application.dataPath;
                new Thread(() => Extract(dataDir, byAtlas, manifest, mf)) { IsBackground = true, Name = "MuninMap item icons", Priority = System.Threading.ThreadPriority.Lowest }.Start();
            }
            catch (Exception e) { log.LogWarning("item icons: " + e.Message); }
        }

        // a prefab name as a file name (and as the page asks for it)
        public static string Safe(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "") if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c);
            return sb.ToString();
        }

        private static void Extract(string dataDir, Dictionary<string, List<Entry>> byAtlas, string manifest, string mf)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int done = 0;
            foreach (var kv in byAtlas)
            {
                try
                {
                    byte[] rgba = TextureExtractor.FindTexture(dataDir, kv.Key, out int w, out int h);
                    if (rgba == null) { log.LogInfo($"item icons: atlas {kv.Key} not found ({kv.Value.Count} items without an icon)"); continue; }
                    foreach (var e in kv.Value)
                    {
                        int x0 = Mathf.Clamp(Mathf.RoundToInt(e.rect.x), 0, w), y0 = Mathf.Clamp(Mathf.RoundToInt(e.rect.y), 0, h);
                        int cw = Mathf.Clamp(Mathf.RoundToInt(e.rect.width), 1, w - x0), ch = Mathf.Clamp(Mathf.RoundToInt(e.rect.height), 1, h - y0);
                        // cut (atlas rows run bottom-up) and box-filter into Size x Size
                        var px = new byte[Size * Size * 4];
                        for (int y = 0; y < Size; y++)
                            for (int x = 0; x < Size; x++)
                            {
                                int sx0 = x * cw / Size, sx1 = Math.Max(sx0 + 1, (x + 1) * cw / Size), sy0 = y * ch / Size, sy1 = Math.Max(sy0 + 1, (y + 1) * ch / Size);
                                int r = 0, g = 0, b = 0, a = 0, n = 0;
                                for (int sy = sy0; sy < sy1; sy++)
                                    for (int sx = sx0; sx < sx1; sx++)
                                    {
                                        int o = ((y0 + ch - 1 - sy) * w + x0 + sx) * 4, al = rgba[o + 3];
                                        r += rgba[o] * al; g += rgba[o + 1] * al; b += rgba[o + 2] * al; a += al; n++;
                                    }
                                int q = (y * Size + x) * 4;
                                if (a > 0) { px[q] = (byte)(r / a); px[q + 1] = (byte)(g / a); px[q + 2] = (byte)(b / a); }
                                px[q + 3] = (byte)(a / n);
                            }
                        File.WriteAllBytes(Path.Combine(Dir, e.prefab + ".png"), Png.Encode(px, Size, Size, Png.Format.RGBA));
                        done++;
                    }
                }
                catch (Exception ex) { log.LogWarning($"item icons: atlas {kv.Key}: {ex.Message}"); }
            }
            try { File.WriteAllText(mf, manifest); } catch { }
            log.LogInfo($"{done} item icons extracted from {byAtlas.Count} atlases in {sw.Elapsed.TotalSeconds:F0} s");
        }
    }
}
