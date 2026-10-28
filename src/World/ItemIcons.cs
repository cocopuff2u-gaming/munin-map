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
        public static volatile string GearJson = "{}";    // prefab -> what a piece of gear does, for the player cards (data/gear.json)

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
                var gear = new JsonWriter(65536);
                gear.BeginObject();
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
                        if (drop?.m_itemData?.m_shared != null) WriteGear(gear, go.name, drop.m_itemData.m_shared);
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
                gear.End();
                GearJson = gear.ToString();
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

        private static readonly HashSet<ItemDrop.ItemData.ItemType> Worn = new HashSet<ItemDrop.ItemData.ItemType>
        {
            ItemDrop.ItemData.ItemType.OneHandedWeapon, ItemDrop.ItemData.ItemType.Bow, ItemDrop.ItemData.ItemType.Shield, ItemDrop.ItemData.ItemType.Helmet,
            ItemDrop.ItemData.ItemType.Chest, ItemDrop.ItemData.ItemType.Legs, ItemDrop.ItemData.ItemType.TwoHandedWeapon, ItemDrop.ItemData.ItemType.Torch,
            ItemDrop.ItemData.ItemType.Shoulder, ItemDrop.ItemData.ItemType.Utility, ItemDrop.ItemData.ItemType.Tool, ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft,
            ItemDrop.ItemData.ItemType.Trinket,
        };

        private static readonly HashSet<ItemDrop.ItemData.ItemType> Wearable = new HashSet<ItemDrop.ItemData.ItemType>
        {
            ItemDrop.ItemData.ItemType.Helmet, ItemDrop.ItemData.ItemType.Chest, ItemDrop.ItemData.ItemType.Legs, ItemDrop.ItemData.ItemType.Shoulder,
            ItemDrop.ItemData.ItemType.Hands, ItemDrop.ItemData.ItemType.Utility, ItemDrop.ItemData.ItemType.Trinket,
        };
        // the kinds whose armour counts (the game's tooltip shows it for these only: a belt's or a trinket's
        // armour is the default 10, never used)
        private static readonly HashSet<ItemDrop.ItemData.ItemType> Armoured = new HashSet<ItemDrop.ItemData.ItemType>
        {
            ItemDrop.ItemData.ItemType.Helmet, ItemDrop.ItemData.ItemType.Chest, ItemDrop.ItemData.ItemType.Legs, ItemDrop.ItemData.ItemType.Shoulder,
        };

        private static string L(string token) { if (string.IsNullOrEmpty(token)) return ""; try { string t = Live.WorldEvents.LocalizeFn(token); return t != null && !t.StartsWith("$", StringComparison.Ordinal) ? t : ""; } catch { return ""; } }

        // What a piece of gear does at quality 1 and what each level adds (the game's own numbers: the
        // tooltip's), so the card can show it at the level it's worn at when the game says (weapons,
        // shields and capes), or as a range up to the most it can be upgraded to.
        // {"type":"Chest","q":4,"armor":[8,2],"block":[..],"dmg":{"slash":[35,6]},"dur":[1000,200],
        //  "weight":15,"move":-0.05,"mods":[["Frost","Resistant"]],"effect":"..","set":["Troll","..",3]}
        private static void WriteGear(JsonWriter j, string prefab, ItemDrop.ItemData.SharedData sh)
        {
            if (!Worn.Contains(sh.m_itemType)) return;
            j.Key(prefab).BeginObject().Prop("type", sh.m_itemType.ToString()).Prop("q", sh.m_maxQuality);
            // (every item has armour, block and damage numbers; only the ones that count for its kind)
            bool worn = Wearable.Contains(sh.m_itemType);
            if (Armoured.Contains(sh.m_itemType) && (sh.m_armor > 0f || sh.m_armorPerLevel > 0f)) j.Key("armor").BeginArray().Value(sh.m_armor, 1).Value(sh.m_armorPerLevel, 1).End();
            if (!worn && sh.m_blockPower > 0f) j.Key("block").BeginArray().Value(sh.m_blockPower, 1).Value(sh.m_blockPowerPerLevel, 1).End();
            var d = worn ? default : sh.m_damages; var dl = sh.m_damagesPerLevel;
            var types = new[] { ("damage", d.m_damage, dl.m_damage), ("blunt", d.m_blunt, dl.m_blunt), ("slash", d.m_slash, dl.m_slash), ("pierce", d.m_pierce, dl.m_pierce),
                ("fire", d.m_fire, dl.m_fire), ("frost", d.m_frost, dl.m_frost), ("lightning", d.m_lightning, dl.m_lightning), ("poison", d.m_poison, dl.m_poison), ("spirit", d.m_spirit, dl.m_spirit),
                ("chop", d.m_chop, dl.m_chop), ("pickaxe", d.m_pickaxe, dl.m_pickaxe) };
            bool any = false;
            foreach (var t in types) if (t.Item2 > 0f) { if (!any) { j.Key("dmg").BeginObject(); any = true; } j.Key(t.Item1).BeginArray().Value(t.Item2, 1).Value(t.Item3, 1).End(); }
            if (any) j.End();
            if (sh.m_useDurability) j.Key("dur").BeginArray().Value(sh.m_maxDurability, 0).Value(sh.m_durabilityPerLevel, 0).End();
            j.Prop("weight", sh.m_weight, 1);
            if (sh.m_movementModifier != 0f) j.Prop("move", sh.m_movementModifier, 2);
            if (sh.m_eitrRegenModifier != 0f) j.Prop("eitrRegen", sh.m_eitrRegenModifier, 2);
            if (sh.m_damageModifiers != null && sh.m_damageModifiers.Count > 0)
            {
                j.Key("mods").BeginArray();
                foreach (var m in sh.m_damageModifiers) if (m.m_modifier != HitData.DamageModifier.Normal) j.BeginArray().Value(m.m_type.ToString()).Value(m.m_modifier.ToString()).End();
                j.End();
            }
            string effect = sh.m_equipStatusEffect != null ? L(sh.m_equipStatusEffect.m_name) : "";
            if (effect.Length > 0) j.Prop("effect", effect);
            if (!string.IsNullOrEmpty(sh.m_setName) && sh.m_setSize > 1)
                j.Key("set").BeginArray().Value(sh.m_setName).Value(sh.m_setStatusEffect != null ? L(sh.m_setStatusEffect.m_name) : "").Value(sh.m_setSize).End();
            j.End();
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
