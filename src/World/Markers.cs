using System.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using MuninMap.Util;

namespace MuninMap.World
{
    // Points of interest as marker sets: portals with their tags, tombstones,
    // player bases, boats and carts, boss altars on explored ground, and custom
    // markers from markers.json beside the world's map data. Boss altars and the traders show
    // once someone has explored the ground they stand on; other world locations (dungeons...)
    // are not published: the game's registry knows every location whether or not anyone found
    // it, and that is a spoiler.
    internal static class Markers
    {
        private struct Portal { public ZDOID id; public int prefab; public float x, y, z; public string tag; public long creator; }
        private struct Tomb { public float x, y, z; public string owner; public long when; }

        private static List<Portal> portals = new List<Portal>();
        private static List<Tomb> tombs = new List<Tomb>();
        private static List<Portal> buildingPortals;
        private static List<Tomb> buildingTombs;

        // wards (PrivateArea): who placed one, whether it's on, how many may build there
        private struct Ward { public float x, y, z, r; public string owner; public bool on; public List<string> permitted; }
        private static List<Ward> wards = new List<Ward>();
        private static List<Ward> buildingWards;
        private static List<Runestones.Stone> runes = new List<Runestones.Stone>();
        private static List<Runestones.Stone> buildingRunes;
        // signs players wrote on (Sign: the text, and who wrote it when the game kept that)
        private struct SignSpot { public float x, y, z; public string text, author; }
        private static List<SignSpot> signs = new List<SignSpot>();
        private static List<SignSpot> buildingSigns;
        private static readonly Dictionary<string, bool> isSign = new Dictionary<string, bool>();
        private static readonly System.Text.RegularExpressions.Regex richTags = new System.Text.RegularExpressions.Regex("<[^>]*>", System.Text.RegularExpressions.RegexOptions.Compiled);
        private struct BedSpot { public float x, y, z; public string owner; }
        private static List<BedSpot> beds = new List<BedSpot>();
        private static List<BedSpot> buildingBeds;
        private static readonly Dictionary<string, bool> isBed = new Dictionary<string, bool>();
        private static readonly Dictionary<string, float> wardRadius = new Dictionary<string, float>();   // prefab -> radius (0: not a ward)

        // pins people shared on cartography tables, per table (re-read only when its data changes)
        private struct TablePin { public float x, y, z; public string name; public int type; public bool check; }
        private sealed class Table { public int sig; public List<TablePin> pins = new List<TablePin>(); }
        private static readonly Dictionary<ZDOID, Table> tables = new Dictionary<ZDOID, Table>();
        private static HashSet<ZDOID> buildingTables;

        private static volatile string json = "{\"sets\":[]}";
        private static int rev;
        private static string customCache;
        private static DateTime customStamp;

        private static int hashPortalTag = "tag".GetStableHashCode();
        private static int hashOwnerName = "ownerName".GetStableHashCode();
        private static int hashTimeOfDeath = "timeOfDeath".GetStableHashCode();

        public static string Json => json;
        public static int Rev => rev;

        // ---- base overrides: people rename or hide the auto-detected bases from the web page.
        // Kept in bases.json beside the world's map data, matched to a detected base by distance
        // (a base's centroid drifts a little as pieces come and go).
        public sealed class BaseOverride { public float x, z; public string label; public bool hidden; }
        private static List<BaseOverride> overrides;
        private static string overridesPath;
        private const float MATCH_RADIUS = 60f;

        // The last published markers, saved after every sweep and served straight away on the next
        // start, so portals, tombstones, bases and boss altars are there before the first sweep is.
        // Only ever what was already explored when it was saved.
        private static string cachePath;
        public static void LoadCache(string worldDataPath)
        {
            cachePath = Path.Combine(worldDataPath, "markers-cache.json");
            try { if (File.Exists(cachePath)) { string c = File.ReadAllText(cachePath); if (c.StartsWith("{", StringComparison.Ordinal)) json = c; } }
            catch (Exception e) { ZLog.LogWarning("MuninMap: markers cache: " + e.Message); }
        }

        public static void LoadOverrides(string worldDataPath)
        {
            overridesPath = Path.Combine(worldDataPath, "bases.json");
            overrides = new List<BaseOverride>();
            try
            {
                if (!File.Exists(overridesPath)) return;
                var doc = JsonParser.ParseObject(File.ReadAllText(overridesPath));
                var arr = JsonParser.Arr(doc, "bases");
                if (arr == null) return;
                foreach (var o in arr)
                    if (o is Dictionary<string, object> d)
                        overrides.Add(new BaseOverride { x = (float)JsonParser.Num(d, "x"), z = (float)JsonParser.Num(d, "z"), label = JsonParser.Str(d, "label", null), hidden = JsonParser.Bool(d, "hidden") });
            }
            catch (Exception e) { ZLog.LogWarning("MuninMap: bases.json not readable: " + e.Message); }
        }

        private static void SaveOverrides()
        {
            if (overridesPath == null) return;
            try
            {
                var j = new JsonWriter(512);
                j.BeginObject().Key("bases").BeginArray();
                lock (overrides)
                    foreach (var o in overrides)
                    {
                        j.BeginObject().Prop("x", o.x, 1).Prop("z", o.z, 1);
                        if (o.label != null) j.Prop("label", o.label);
                        if (o.hidden) j.Prop("hidden", true);
                        j.End();
                    }
                j.End().End();
                File.WriteAllText(overridesPath, j.ToString());
            }
            catch (Exception e) { ZLog.LogWarning("MuninMap: could not save bases.json: " + e.Message); }
        }

        private static BaseOverride FindOverride(float x, float z)
        {
            if (overrides == null) return null;
            BaseOverride best = null; float bd = MATCH_RADIUS * MATCH_RADIUS;
            foreach (var o in overrides)
            {
                float d = (o.x - x) * (o.x - x) + (o.z - z) * (o.z - z);
                if (d < bd) { bd = d; best = o; }
            }
            return best;
        }

        // From the web page (any thread): rename (label != null), hide, or clear (label null, hidden false).
        // The markers are rebuilt at once so the page sees it without waiting for the next sweep.
        public static void SetBase(float x, float z, string label, bool hidden)
        {
            if (overrides == null) overrides = new List<BaseOverride>();
            lock (overrides)
            {
                var o = FindOverride(x, z);
                if (label == null && !hidden) { if (o != null) overrides.Remove(o); }
                else
                {
                    if (o == null) { o = new BaseOverride { x = x, z = z }; overrides.Add(o); }
                    o.x = x; o.z = z; o.label = label; o.hidden = hidden;
                }
            }
            SaveOverrides();
            try { json = Build(); rev++; } catch (Exception e) { ZLog.LogWarning("MuninMap: markers failed: " + e.Message); }
        }

        public static int ClearOverrides()
        {
            int n = overrides?.Count ?? 0;
            overrides = new List<BaseOverride>();
            SaveOverrides();
            try { json = Build(); rev++; } catch { }
            return n;
        }

        public static void Begin()
        {
            buildingPortals = new List<Portal>(portals.Count + 8);
            buildingTombs = new List<Tomb>(tombs.Count + 8);
            buildingWards = new List<Ward>(wards.Count + 8);
            buildingRunes = new List<Runestones.Stone>(runes.Count + 8);
            buildingBeds = new List<BedSpot>(beds.Count + 8);
            buildingSigns = new List<SignSpot>(signs.Count + 8);
            buildingTables = new HashSet<ZDOID>();
        }

        // Main thread: a cartography table. Its data (Minimap.GetSharedMapData, compressed) is the
        // explored grid (Fog reads that), then the pins: int count, then each: long owner, string name,
        // Vector3 pos, int type, bool checked, string author (version 3 on).
        // A table seen by the sweep; true when its pins changed and need reading (ReadTablePins)
        public static bool ObserveMapTable(ZDO zdo, byte[] raw)
        {
            if (buildingTables == null) return false;
            buildingTables.Add(zdo.m_uid);
            int sig = raw == null || raw.Length == 0 ? 0 : raw.Length * 31 + raw[raw.Length / 2] * 7 + raw[raw.Length - 1];
            if (tables.TryGetValue(zdo.m_uid, out Table t) && t.sig == sig) return false;
            tables[zdo.m_uid] = new Table { sig = sig };
            return sig != 0;
        }

        // the table's pins, from its recorded map (data: unpacked)
        public static void ReadTablePins(ZDO zdo, byte[] data)
        {
            if (!tables.TryGetValue(zdo.m_uid, out Table t)) return;
            try
            {
                var pkg = new ZPackage(data);
                int version = pkg.ReadInt();
                if (version < 2) return;   // before pins were shared
                int n = pkg.ReadInt();
                pkg.SetPos(8 + n);         // past the explored grid, one byte a cell
                int count = pkg.ReadInt();
                for (int i = 0; i < count; i++)
                {
                    pkg.ReadLong();
                    string name = pkg.ReadString();
                    Vector3 pos = pkg.ReadVector3();
                    int type = pkg.ReadInt();
                    bool check = pkg.ReadBool();
                    if (version >= 3) pkg.ReadString();
                    if (name != null && name.StartsWith("$", StringComparison.Ordinal)) name = Live.WorldEvents.Localize(name, name.Substring(1));
                    t.pins.Add(new TablePin { x = pos.x, y = pos.y, z = pos.z, name = name ?? "", type = type, check = check });
                }
            }
            catch (Exception e) { ZLog.LogWarning("MuninMap: map table pins not readable: " + e.Message); }
        }

        // our icon for one of the game's pin types (Minimap.PinType)
        private static string TablePinIcon(int type)
        {
            switch (type)
            {
                case 0: return "fire";
                case 1: return "house";
                case 2: return "mine";
                case 4: return "tombstone";
                case 5: return "bed";
                case 6: return "cave";
                case 9: return "boss";
                case 14: return "hildir1";
                case 15: return "hildir2";
                case 16: return "hildir3";
                default: return "dot";
            }
        }

        // Main thread. Returns true if the ZDO was a marker-worthy object.
        // The sweep's walk asks this of nearly every object in the world (3 million on a big one). Most
        // can't be a marker (trees, rocks, building pieces); which prefabs can is worked out once each and
        // kept by the prefab's number, so those skip the name and its text checks.
        private static readonly Dictionary<int, bool> canBeMarker = new Dictionary<int, bool>();
        public static bool Observe(ZDO zdo, int prefab, Vector3 pos)
        {
            if (buildingPortals == null) return false;
            string name = null;
            if (!canBeMarker.TryGetValue(prefab, out bool can))
            {
                name = WorldSweep.NameOf(prefab);
                can = name != null && (name.StartsWith("portal", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Player_tombstone", StringComparison.OrdinalIgnoreCase)
                    || IsBed(name) || IsSign(name) || WardRadius(name) > 0f);
                canBeMarker[prefab] = can;
            }
            return can && Observe(zdo, name ?? WorldSweep.NameOf(prefab), pos);
        }

        private static bool Observe(ZDO zdo, string prefabName, Vector3 pos)
        {
            if (buildingPortals == null || prefabName == null) return false;
            string n = prefabName;
            if (n.StartsWith("portal", StringComparison.OrdinalIgnoreCase))
            {
                string tag = "";
                try { tag = zdo.GetString(hashPortalTag, "") ?? ""; } catch { }
                long creator = 0L;
                try { creator = zdo.GetLong(ZDOVars.s_creator, 0L); } catch { }
                buildingPortals.Add(new Portal { id = zdo.m_uid, prefab = zdo.GetPrefab(), x = pos.x, y = pos.y, z = pos.z, tag = tag, creator = creator });
                return true;
            }
            if (n.Equals("Player_tombstone", StringComparison.OrdinalIgnoreCase))
            {
                string owner = "";
                long when = 0;
                try { owner = zdo.GetString(hashOwnerName, "") ?? ""; } catch { }
                try { when = zdo.GetLong(hashTimeOfDeath, 0L); } catch { }
                buildingTombs.Add(new Tomb { x = pos.x, y = pos.y, z = pos.z, owner = owner, when = when });
                return true;
            }
            if (IsBed(n))
            {
                // a claimed bed (Bed.GetOwner / GetOwnerName): the player's spawn point
                try
                {
                    long who = zdo.GetLong(ZDOVars.s_owner, 0L);
                    string name = zdo.GetString(ZDOVars.s_ownerName, "") ?? "";
                    if (who != 0L && name.Length > 0) buildingBeds.Add(new BedSpot { x = pos.x, y = pos.y, z = pos.z, owner = name });
                }
                catch { }
                return false;   // still a built piece
            }
            if (buildingSigns != null && IsSign(n))
            {
                try
                {
                    string text = richTags.Replace(zdo.GetString(ZDOVars.s_text, "") ?? "", "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
                    // (the writer's name is only kept on console platforms: s_authorDisplayName)
                    if (text.Length > 0)
                        buildingSigns.Add(new SignSpot { x = pos.x, y = pos.y, z = pos.z, text = text.Length > 80 ? text.Substring(0, 80) : text, author = zdo.GetString(ZDOVars.s_authorDisplayName, "") ?? "" });
                }
                catch { }
                return false;   // still a built piece
            }
            float r = WardRadius(n);
            if (r > 0f && zdo.GetLong(ZDOVars.s_creator, 0L) != 0L)   // (built by a player: the world's own camps and forts carry wards too)
            {
                var w = new Ward { x = pos.x, y = pos.y, z = pos.z, r = r, owner = "" };
                try
                {
                    w.owner = zdo.GetString(ZDOVars.s_creatorName, "") ?? ""; w.on = zdo.GetBool(ZDOVars.s_enabled, false);
                    // the players the owner let build there (PrivateArea.SetPermittedPlayers)
                    int n2 = Math.Min(20, zdo.GetInt(ZDOVars.s_permitted, 0));
                    for (int i = 0; i < n2; i++)
                    {
                        string who = zdo.GetString("pu_name" + i, "");
                        if (!string.IsNullOrEmpty(who)) (w.permitted ?? (w.permitted = new List<string>())).Add(who);
                    }
                }
                catch { }
                buildingWards.Add(w);
                return false;   // still a built piece (Structures draws it)
            }
            return false;
        }

        // Main thread: a location's proxy (its runestones, if it has any)
        public static void ObserveProxy(ZDO zdo, Vector3 pos)
        {
            if (buildingRunes != null) Runestones.Observe(zdo, pos, buildingRunes);
        }

        private static bool IsSign(string prefab)
        {
            if (isSign.TryGetValue(prefab, out bool b)) return b;
            b = false;
            try { var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab) : null; b = go != null && go.GetComponent<Sign>() != null; } catch { }
            isSign[prefab] = b;
            return b;
        }

        private static bool IsBed(string prefab)
        {
            if (isBed.TryGetValue(prefab, out bool b)) return b;
            b = false;
            try { var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab) : null; b = go != null && go.GetComponent<Bed>() != null; } catch { }
            isBed[prefab] = b;
            return b;
        }

        // the protected radius of a ward prefab (the game's guard stone, or a mod's): 0 if it isn't one
        private static float WardRadius(string prefab)
        {
            if (wardRadius.TryGetValue(prefab, out float r)) return r;
            r = 0f;
            try
            {
                var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab) : null;
                var pa = go != null ? go.GetComponent<PrivateArea>() : null;
                if (pa != null) r = pa.m_radius;
            }
            catch { }
            wardRadius[prefab] = r;
            return r;
        }

        // Main thread, end of sweep: publish everything.
        // Main thread, end of sweep, a slice per frame (WorldSweep.Due)
        public static IEnumerator Finish()
        {
            if (buildingPortals != null) { portals = buildingPortals; tombs = buildingTombs; wards = buildingWards; runes = buildingRunes; beds = buildingBeds; if (buildingSigns != null) signs = buildingSigns; }
            if (buildingTables != null)
            {
                var gone = new List<ZDOID>();
                foreach (var id in tables.Keys) if (!buildingTables.Contains(id)) gone.Add(id);
                foreach (var id in gone) tables.Remove(id);
            }
            buildingPortals = null; buildingTombs = null; buildingWards = null; buildingRunes = null; buildingBeds = null; buildingSigns = null; buildingTables = null;
            var j = new JsonWriter(8192);
            var parts = BuildParts(j, true).GetEnumerator();
            while (true)
            {
                try { if (!parts.MoveNext()) break; }
                catch (Exception e) { ZLog.LogWarning("MuninMap: markers failed: " + e.Message); yield break; }
                yield return null;
            }
            string built = j.ToString(); rev++;
            if (built != json && cachePath != null)
            {
                string path = cachePath;   // written off the game's thread
                System.Threading.ThreadPool.QueueUserWorkItem(_ => { try { File.WriteAllText(path, built); } catch { } });
            }
            json = built;
        }

        private static bool Visible(float x, float z) => MuninConfig.REVEAL_ALL || Fog.IsExplored(x, z);

        // the boss a location prefab is the altar of, or null
        private static string BossName(string prefab)
        {
            if (string.IsNullOrEmpty(prefab)) return null;
            switch (prefab)
            {
                case "Eikthyrnir": return "Eikthyr";
                case "GDKing": return "The Elder";
                case "Bonemass": return "Bonemass";
                case "Dragonqueen": return "Moder";
                case "GoblinKing": return "Yagluth";
                case "FaderLocation": return "Fader";
            }
            if (prefab.StartsWith("DN_Bossroom", StringComparison.Ordinal)) return "Kall Fimbulbringer";   // the game's pin: "Aesir Passage"
            return prefab.StartsWith("Mistlands_DvergrBossEntrance", StringComparison.Ordinal) ? "The Queen" : null;
        }

        // the traders' camps: name and our icon
        private static string TraderName(string prefab, out string icon)
        {
            icon = null;
            switch (prefab)
            {
                case "Vendor_BlackForest": icon = "trader"; return "Haldor";
                case "Hildir_camp": icon = "hildir"; return "Hildir";
                case "BogWitch_Camp": icon = "bogwitch"; return "Bog Witch";
            }
            return null;
        }

        // Dungeon entrances: name and an icon per kind (shown on explored ground, like everything here;
        // no events, a world has hundreds of them; the page starts with the set switched off)
        public static bool IsDungeon(string prefab) => DungeonName(prefab, out _) != null;
        private static string DungeonName(string prefab, out string icon)
        {
            icon = null;
            if (prefab == null) return null;
            if (prefab.StartsWith("SunkenCrypt", StringComparison.Ordinal)) { icon = "sunkencrypt"; return "Sunken crypt"; }
            if (prefab.StartsWith("Crypt", StringComparison.Ordinal) || prefab == "HalfBurried_ForestCrypt") { icon = "crypt"; return "Burial chamber"; }
            if (prefab.StartsWith("TrollCave", StringComparison.Ordinal)) { icon = "trollcave"; return "Troll cave"; }
            if (prefab.StartsWith("MountainCave", StringComparison.Ordinal)) { icon = "frostcave"; return "Frost cave"; }
            if (prefab.StartsWith("Mistlands_DvergrTownEntrance", StringComparison.Ordinal)) { icon = "infestedmine"; return "Infested mine"; }
            if (prefab == "CharredFortress") { icon = "fortress"; return "Charred fortress"; }
            if (prefab == "BearCave") { icon = "bearcave"; return "Bear cave"; }
            if (prefab.StartsWith("MorgenHole", StringComparison.Ordinal)) { icon = "hole"; return "Morgen hole"; }   // the Ashlands' Morgen lairs
            // the Deep North's, by the game's own names (location_thehole, location_darkesthole, location_morkhalla)
            if (prefab.StartsWith("TheHole", StringComparison.Ordinal)) { icon = "hole"; return "Winding tunnels"; }
            if (prefab == "TheDarkestHole") { icon = "hole"; return "The Hole"; }
            if (prefab == "MorkBorg") { icon = "morkhalla"; return "Mörkhalla"; }
            return null;
        }

        // camps and resource spots: places worth a trip that are not dungeons
        private static string CampName(string prefab, out string icon)
        {
            icon = null;
            if (prefab == null) return null;
            if (prefab.StartsWith("GoblinCamp", StringComparison.Ordinal)) { icon = "fulingcamp"; return "Fuling camp"; }
            if (prefab.StartsWith("TarPit", StringComparison.Ordinal)) { icon = "tarpit"; return "Tar pit"; }
            if (prefab.StartsWith("DrakeNest", StringComparison.Ordinal)) { icon = "drakenest"; return "Drake nest"; }
            if (prefab.StartsWith("InfestedTree", StringComparison.Ordinal)) { icon = "gucktree"; return "Guck tree"; }
            return null;
        }

        // one-off places with something to do there, by the game's names
        private static string LandmarkName(string prefab, out string icon)
        {
            icon = null;
            if (prefab == null) return null;
            if (prefab == "AncientUpgradeStation") { icon = "forge"; return "Forge of Potential"; }
            if (prefab == "NorthMemorialPlace") { icon = "memorial"; return "Ancestral Memorial"; }
            if (prefab.StartsWith("FimbulLocation", StringComparison.Ordinal)) { icon = "orb"; return "Orb of Fimbulvinter"; }
            if (prefab.StartsWith("HotSpring", StringComparison.Ordinal)) { icon = "hotspring"; return "Hot spring"; }
            return null;
        }

        // Hildir's quest dungeons, each with one of her sisters as a mini boss
        private static string MiniBossName(string prefab, out string icon)
        {
            icon = null;
            switch (prefab)
            {
                case "Hildir_crypt": icon = "hildir1"; return "Smouldering Tomb (Brenna)";
                case "Hildir_cave": icon = "hildir2"; return "Howling Cavern (Geirrhafa)";
                case "Hildir_plainsfortress": icon = "hildir3"; return "Sealed Tower (Zil & Thungr)";
            }
            return null;
        }

        // A location the game has actually put in the world. The registry also holds the spots it has
        // only planned (in zones nobody has been to yet), and `genloc` (or a world upgrade tool) clears
        // those and plans them again elsewhere, which used to drop phantom altars and traders onto
        // explored ground. Ground a player has walked is generated, so a real one there is built.
        public static bool Built(ZoneSystem.LocationInstance li) => li.m_placed;

        // Boss altars and traders, the locations people "find" (Live.WorldEvents reports each the
        // moment its ground is explored). Main thread.
        public struct Findable { public string key, label, kind; public Vector3 pos; }
        public static List<Findable> Findables()
        {
            var list = new List<Findable>();
            var zs = ZoneSystem.instance;
            if (zs == null || zs.m_locationInstances == null) return list;
            foreach (var li in zs.m_locationInstances.Values)
            {
                string name = li.m_location?.m_prefabName;
                string label = BossName(name), kind = "boss";
                if (label == null) { label = TraderName(name, out _); kind = "trader"; }
                if (label == null) { label = MiniBossName(name, out _); kind = "miniboss"; }
                if (label == null || !Built(li)) continue;
                list.Add(new Findable { key = name + "@" + Mathf.RoundToInt(li.m_position.x) + "," + Mathf.RoundToInt(li.m_position.z), label = label, kind = kind, pos = li.m_position });
            }
            return list;
        }

        // Main thread: publish again now (a boss altar or trader was just found), not at the next sweep
        public static void Refresh()
        {
            try
            {
                string built = Build(); rev++;
                if (built != json && cachePath != null) { try { File.WriteAllText(cachePath, built); } catch { } }
                json = built;
            }
            catch (Exception e) { ZLog.LogWarning("MuninMap: markers failed: " + e.Message); }
        }

        private static List<Structures.Base> bases;

        // Between sweeps (every 15 s, main thread): portals placed, retagged or taken down and boats
        // and carts that moved show without waiting for the next walk over the world. Portals come
        // from the game's own list of them (ZDOMan.m_portalObjects, a few hundred at most), boats and
        // carts are the ones the last sweep found looked up by id, and new ones as players' games send
        // them (Vehicles.ArrivePatch). The markers are rebuilt only when something changed.
        public static IEnumerator LiveLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(15f);
                try
                {
                    bool p = RefreshPortals(), v = Vehicles.Refresh();
                    if (p || v) { json = Build(); rev++; }
                }
                catch (Exception e) { if (MuninConfig.DEBUG) ZLog.LogWarning("MuninMap: live markers: " + e.Message); }
            }
        }

        private static HashSet<int> gamePortals;
        private static bool RefreshPortals()
        {
            if (buildingPortals != null || ZDOMan.instance == null || Game.instance == null) return false;   // mid-sweep: the sweep has them
            if (gamePortals == null) gamePortals = new HashSet<int>(Game.instance.PortalPrefabHash);
            var live = new Dictionary<ZDOID, ZDO>();
            foreach (var l in ZDOMan.instance.m_portalObjects.Values)
                foreach (var z in l) if (z != null && z.IsValid()) live[z.m_uid] = z;
            bool changed = false;
            var next = new List<Portal>(portals.Count + 4);
            foreach (var p in portals)
            {
                if (!live.TryGetValue(p.id, out var z))
                {
                    if (gamePortals.Contains(p.prefab)) { changed = true; continue; }   // taken down
                    next.Add(p);   // a portal the game doesn't list (a mod's): the sweep keeps it
                    continue;
                }
                live.Remove(p.id);
                var q = Read(z);
                if (q.tag != p.tag || Mathf.Abs(q.x - p.x) > 0.5f || Mathf.Abs(q.z - p.z) > 0.5f) changed = true;
                next.Add(q);
            }
            foreach (var z in live.Values) { next.Add(Read(z)); changed = true; }   // new since the sweep
            if (changed) portals = next;
            return changed;
        }
        private static Portal Read(ZDO z)
        {
            var pos = z.GetPosition();
            string tag = "";
            try { tag = z.GetString(hashPortalTag, "") ?? ""; } catch { }
            return new Portal { id = z.m_uid, prefab = z.GetPrefab(), x = pos.x, y = pos.y, z = pos.z, tag = tag };
        }

        private static string Build()
        {
            var j = new JsonWriter(8192);
            foreach (var _ in BuildParts(j, false)) { }
            return j.ToString();
        }

        // slice: pause (yield) when the sweep's frame is used up (WorldSweep.Due), from Finish
        // The game's locations by kind (0 boss altars, 1 mini-boss lairs, 2 dungeons, 3 traders), in one
        // pass with each prefab name's kind remembered: the four loops each used to read every location's
        // name, the world's thousands of them, in one go (about 28 ms a sweep)
        private static readonly Dictionary<string, int> locationKinds = new Dictionary<string, int>();
        private static List<ZoneSystem.LocationInstance>[] LocationsByKind()
        {
            var lists = new List<ZoneSystem.LocationInstance>[6];
            for (int i = 0; i < 6; i++) lists[i] = new List<ZoneSystem.LocationInstance>();
            var zs = ZoneSystem.instance;
            if (zs == null) return lists;
            foreach (var li in zs.m_locationInstances.Values)
            {
                string name = li.m_location?.m_prefabName;
                if (name == null) continue;
                if (!locationKinds.TryGetValue(name, out int kinds))
                {
                    kinds = (BossName(name) != null ? 1 : 0) | (MiniBossName(name, out _) != null ? 2 : 0)
                          | (DungeonName(name, out _) != null ? 4 : 0) | (TraderName(name, out _) != null ? 8 : 0)
                          | (CampName(name, out _) != null ? 16 : 0) | (LandmarkName(name, out _) != null ? 32 : 0);
                    locationKinds[name] = kinds;
                }
                for (int i = 0; i < 6; i++) if ((kinds & (1 << i)) != 0) lists[i].Add(li);
            }
            return lists;
        }


        // ---- a marker set's text, kept from the last time when what it's made of is the same: each
        // sweep rewrote all of it (700 KB on a big world, hundreds of ms and a memory clean-up in one
        // frame) though nearly every sweep changes nothing
        private static readonly Dictionary<string, KeyValuePair<long, string>> setText = new Dictionary<string, KeyValuePair<long, string>>();
        private static bool Reuse(JsonWriter j, string id, long sig)
        {
            if (!setText.TryGetValue(id, out var kept) || kept.Key != sig) return false;
            j.Raw(kept.Value);
            return true;
        }
        private static void Keep(JsonWriter j, string id, long sig, int mark) => setText[id] = new KeyValuePair<long, string>(sig, j.Since(mark));
        private static long Mix(long h, long v) { unchecked { return (h ^ v) * 1099511628211L; } }
        private static long Mix(long h, float x, float z) => Mix(Mix(h, Mathf.RoundToInt(x * 10)), Mathf.RoundToInt(z * 10));
        private static long Mix(long h, string s) => Mix(h, s == null ? 0 : s.GetHashCode());

        // ---- one marker set. Its text is kept and reused while what it shows is the same. What it
        // shows is worked out first, cheaply (which of its items stand on explored ground), so exploring
        // that uncovers nothing new no longer rebuilds every set: the signature used to include all the
        // explored ground, and with players about every set was rebuilt each sweep, a whole set in one
        // frame (224 ms seen with 5 players). Both passes are spread over frames when sliced.
        private static IEnumerable<object> Set<T>(JsonWriter j, bool slice, string id, string label, List<T> items, long sig,
            Func<T, bool> shown, Func<long, T, long> mix, Action<JsonWriter, T> write)
        {
            var timer = new PartTimer(id);
            for (int i = 0; i < items.Count; i++)
            {
                if (slice && (i & 127) == 127 && WorldSweep.Due) { timer.Pause(); yield return null; timer.Resume(); }
                T it = items[i];
                bool s; try { s = shown(it); } catch { s = false; }
                if (s) sig = mix(Mix(sig, i), it);
            }
            if (Reuse(j, id, sig)) { timer.Done(); yield break; }
            int mark = j.Mark();
            j.BeginObject().Prop("id", id).Prop("label", label).Key("markers").BeginArray();
            for (int i = 0; i < items.Count; i++)
            {
                if (slice && (i & 63) == 63 && WorldSweep.Due) { timer.Pause(); yield return null; timer.Resume(); }
                T it = items[i];
                try { if (shown(it)) write(j, it); }
                catch (Exception e) { if (MuninConfig.DEBUG) ZLog.LogWarning($"MuninMap: {id}: {e.Message}"); }
            }
            j.End().End();
            Keep(j, id, sig, mark);
            timer.Done();
        }

        // ---- how long each part of the markers took on the game's thread this sweep (its longest
        // single frame), for the sweep's log line: parts over 10 ms are named there
        private static readonly Dictionary<string, double> partLongest = new Dictionary<string, double>();
        public static string SlowParts()
        {
            var slow = partLongest.Where(kv => kv.Value >= 10.0).OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value:0.0}ms").ToList();
            return slow.Count == 0 ? "" : "markers slowest: " + string.Join(", ", slow);
        }
        private sealed class PartTimer
        {
            private readonly string id;
            private readonly System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            private double longest;
            public PartTimer(string id) { this.id = id; }
            public void Pause() { double ms = sw.Elapsed.TotalMilliseconds; if (ms > longest) longest = ms; sw.Reset(); }
            public void Resume() => sw.Start();
            public void Done() { Pause(); partLongest[id] = longest; }
        }

        private static void Location(JsonWriter j, ZoneSystem.LocationInstance li, string cat, string icon, string label)
        {
            j.BeginObject().Prop("x", li.m_position.x, 1).Prop("z", li.m_position.z, 1).Prop("y", li.m_position.y, 1)
             .Prop("cat", cat).Prop("icon", icon).Prop("label", label).Prop("prefab", li.m_location?.m_prefabName);
        }
        private static bool Shown(ZoneSystem.LocationInstance li) => Built(li) && Visible(li.m_position.x, li.m_position.z);
        private static long MixAt(long h, ZoneSystem.LocationInstance li) => Mix(h, li.m_position.x, li.m_position.z);

        private static IEnumerable<object> BuildParts(JsonWriter j, bool slice)
        {
            if (slice) partLongest.Clear();
            var timer = new PartTimer("locations");
            var locs = LocationsByKind();   // altars, lairs, dungeons, traders, camps, landmarks: each set below visits only its own
            timer.Done();
            j.BeginObject();
            j.Prop("rev", rev + 1);
            j.Key("sets").BeginArray();

            // --- boss altars, once someone has explored the ground they stand on (the same rule as
            // everything here; the rest of the game's location registry stays private: spoilers)
            foreach (var y in Set(j, slice, "bosses", "Boss altars", locs[0], 1,
                li => BossName(li.m_location?.m_prefabName) != null && Shown(li), MixAt,
                (w, li) => { Location(w, li, "boss", "boss", BossName(li.m_location.m_prefabName)); w.End(); })) yield return y;
            if (slice && WorldSweep.Due) yield return null;   // the rest next frame

            // --- mini-boss lairs (Hildir's sisters), on explored ground like the altars
            foreach (var y in Set(j, slice, "minibosses", "Mini-boss lairs", locs[1], 2,
                li => MiniBossName(li.m_location?.m_prefabName, out _) != null && Shown(li), MixAt,
                (w, li) => { string mb = MiniBossName(li.m_location.m_prefabName, out string icon); Location(w, li, "miniboss", icon, mb); w.End(); })) yield return y;
            if (slice && WorldSweep.Due) yield return null;

            // --- dungeon entrances, on explored ground, with what is known of their insides
            foreach (var y in Set(j, slice, "dungeons", "Dungeons", locs[2], Mix(3, Dungeons.Signature),
                li => DungeonName(li.m_location?.m_prefabName, out _) != null && Shown(li), MixAt,
                (w, li) =>
                {
                    string dg = DungeonName(li.m_location.m_prefabName, out string icon);
                    Location(w, li, "dungeon", icon, dg);
                    var d = Dungeons.Get(li.m_position);   // (none: never generated)
                    if (d != null)
                    {
                        w.Key("inside").BeginObject().Prop("rooms", d.rooms).Prop("chests", d.chests).Prop("emptied", d.emptied)
                         .Prop("picked", d.picked).Prop("gates", d.gates).Prop("opened", d.opened)
                         .Prop("monsters", d.monsters).Prop("graves", d.graves).Prop("visited", d.Visited);
                        w.Key("left").BeginArray();   // [[name, count]], most first
                        foreach (var kv in d.left.OrderByDescending(e => e.Value)) w.BeginArray().Value(kv.Key).Value(kv.Value).End();
                        w.End().End();
                    }
                    w.End();
                })) yield return y;
            if (slice && WorldSweep.Due) yield return null;

            // --- landmarks (the Forge of Potential, memorials, Fimbulvinter orbs, hot springs), on explored ground
            foreach (var y in Set(j, slice, "landmarks", "Landmarks", locs[5], 4,
                li => LandmarkName(li.m_location?.m_prefabName, out _) != null && Shown(li), MixAt,
                (w, li) => { string ln = LandmarkName(li.m_location.m_prefabName, out string icon); Location(w, li, "landmark", icon, ln); w.End(); })) yield return y;
            if (slice && WorldSweep.Due) yield return null;

            // --- Fuling camps, tar pits, drake nests and guck trees, on explored ground
            foreach (var y in Set(j, slice, "camps", "Camps & resources", locs[4], 5,
                li => CampName(li.m_location?.m_prefabName, out _) != null && Shown(li), MixAt,
                (w, li) => { string cn = CampName(li.m_location.m_prefabName, out string icon); Location(w, li, "camp", icon, cn); w.End(); })) yield return y;
            if (slice && WorldSweep.Due) yield return null;

            // --- traders (Haldor, Hildir, the Bog Witch), on explored ground like the altars
            foreach (var y in Set(j, slice, "traders", "Traders", locs[3], 6,
                li => TraderName(li.m_location?.m_prefabName, out _) != null && Shown(li), MixAt,
                (w, li) => { string tr = TraderName(li.m_location.m_prefabName, out string icon); Location(w, li, "trader", icon, tr); w.End(); })) yield return y;
            if (slice && WorldSweep.Due) yield return null;

            // --- portals (who built each: a player the map has seen)
            foreach (var y in Set(j, slice, "portals", "Portals", portals, Mix(7, Live.Stats.Keys().Count),
                p => Visible(p.x, p.z), (h, p) => Mix(Mix(Mix(h, p.x, p.z), p.tag), p.creator),
                (w, p) =>
                {
                    w.BeginObject().Prop("x", p.x, 1).Prop("z", p.z, 1).Prop("y", p.y, 1).Prop("cat", "portal").Prop("icon", "portal");
                    w.Prop("label", string.IsNullOrEmpty(p.tag) ? "(untagged)" : p.tag).Prop("tag", p.tag);
                    string by = Live.Stats.NameOfPlayerId(p.creator);
                    if (by != null) w.Prop("by", by);
                    w.End();
                })) yield return y;
            if (slice && WorldSweep.Due) yield return null;

            // --- pins shared on cartography tables (every table holds what was shared at it, so the
            // same pin is on many: keep one per spot)
            timer = new PartTimer("tablepins");
            j.BeginObject().Prop("id", "tablepins").Prop("label", "Cartography table pins").Key("markers").BeginArray();
            var pinSeen = new HashSet<long>();
            int n = 0;
            foreach (var t in new List<Table>(tables.Values))
                foreach (var p in t.pins)
                {
                    if (slice && (++n & 255) == 0 && WorldSweep.Due) { timer.Pause(); yield return null; timer.Resume(); }
                    long key = ((long)Mathf.RoundToInt(p.x) << 32) ^ (uint)Mathf.RoundToInt(p.z) ^ ((long)p.type << 58);
                    if (!pinSeen.Add(key) || !Visible(p.x, p.z)) continue;
                    j.BeginObject().Prop("x", p.x, 1).Prop("z", p.z, 1).Prop("y", p.y, 1).Prop("cat", "tablepin").Prop("icon", TablePinIcon(p.type))
                     .Prop("label", p.name);   // unnamed pins have no text, as in the game
                    if (p.check) j.Prop("checked", true);
                    j.End();
                }
            j.End().End();
            timer.Done();
            if (slice && WorldSweep.Due) yield return null;

            // --- tombstones
            foreach (var y in Set(j, slice, "tombstones", "Tombstones", tombs, 8,
                t => Visible(t.x, t.z), (h, t) => Mix(Mix(Mix(h, t.x, t.z), t.owner), t.when),
                (w, t) =>
                {
                    w.BeginObject().Prop("x", t.x, 1).Prop("z", t.z, 1).Prop("y", t.y, 1).Prop("cat", "tombstone").Prop("icon", "tombstone");
                    w.Prop("label", string.IsNullOrEmpty(t.owner) ? "Tombstone" : t.owner + "'s tombstone").Prop("owner", t.owner).Prop("when", t.when).End();
                })) yield return y;
            if (slice && WorldSweep.Due) yield return null;

            // --- wards and the ground they protect
            foreach (var y in Set(j, slice, "wards", "Wards", wards, 9,
                wd => Visible(wd.x, wd.z),
                (h, wd) => Mix(Mix(Mix(Mix(Mix(h, wd.x, wd.z), wd.owner), wd.on ? 1 : 0), Mathf.RoundToInt(wd.r)), wd.permitted == null ? 0 : string.Join(",", wd.permitted).GetHashCode()),
                (w, wd) =>
                {
                    w.BeginObject().Prop("x", wd.x, 1).Prop("z", wd.z, 1).Prop("y", wd.y, 1).Prop("cat", "ward").Prop("icon", "ward");
                    w.Prop("label", string.IsNullOrEmpty(wd.owner) ? "Ward" : wd.owner + "'s ward").Prop("owner", wd.owner);
                    w.Prop("r", wd.r, 1).Prop("on", wd.on);
                    if (wd.permitted != null) { w.Key("permitted").BeginArray(); foreach (var who in wd.permitted) w.Value(who); w.End(); }
                    w.End();
                })) yield return y;
            if (slice && WorldSweep.Due) yield return null;

            // --- runestones, with what they say
            foreach (var y in Set(j, slice, "runestones", "Runestones", runes, 10,
                r => !r.vegvisir && Visible(r.x, r.z), (h, r) => Mix(Mix(Mix(h, r.x, r.z), r.text), r.topic),
                (w, r) =>
                {
                    w.BeginObject().Prop("x", r.x, 1).Prop("z", r.z, 1).Prop("y", r.y, 1).Prop("cat", "runestone").Prop("icon", "runestone");
                    w.Prop("label", !string.IsNullOrEmpty(r.topic) ? r.topic : r.name).Prop("text", r.text);
                    w.End();
                })) yield return y;
            if (slice && WorldSweep.Due) yield return null;

            // --- mountain tops on explored ground; the highest of those shown is named so
            bool firstPeak = true;
            foreach (var y in Set(j, slice, "peaks", "Peaks", Peaks.All, 11,
                pk => Visible(pk.x, pk.z), (h, pk) => Mix(Mix(h, pk.x, pk.z), Mathf.RoundToInt(pk.height)),
                (w, pk) =>
                {
                    w.BeginObject().Prop("x", pk.x, 1).Prop("z", pk.z, 1).Prop("cat", "peak").Prop("icon", "peak")
                     .Prop("label", (firstPeak ? "Highest peak · " : "Peak · ") + Mathf.RoundToInt(pk.height) + " m").Prop("height", pk.height, 0).End();
                    firstPeak = false;   // (highest first)
                })) yield return y;
            if (slice && WorldSweep.Due) yield return null;

            // --- signs with something written on them, on explored ground
            foreach (var y in Set(j, slice, "signs", "Signs", signs, 12,
                s => Visible(s.x, s.z), (h, s) => Mix(Mix(Mix(h, s.x, s.z), s.text), s.author),
                (w, s) => w.BeginObject().Prop("x", s.x, 1).Prop("z", s.z, 1).Prop("y", s.y, 1).Prop("cat", "sign").Prop("icon", "sign")
                           .Prop("label", s.text).Prop("author", s.author).End())) yield return y;
            if (slice && WorldSweep.Due) yield return null;

            // --- Vegvisirs: their own set, named as the game's hover names them (never where they point)
            foreach (var y in Set(j, slice, "vegvisirs", "Vegvisirs", runes, 13,
                r => r.vegvisir && Visible(r.x, r.z), (h, r) => Mix(Mix(Mix(h, r.x, r.z), r.name), r.topic),
                (w, r) =>
                {
                    w.BeginObject().Prop("x", r.x, 1).Prop("z", r.z, 1).Prop("y", r.y, 1).Prop("cat", "vegvisir").Prop("icon", "vegvisir");
                    w.Prop("label", string.IsNullOrEmpty(r.topic) ? r.name : r.name + ": " + r.topic).End();
                })) yield return y;
            if (slice && WorldSweep.Due) yield return null;

            // --- player beds (where each one respawns)
            foreach (var y in Set(j, slice, "beds", "Beds", beds, 14,
                b => Visible(b.x, b.z), (h, b) => Mix(Mix(h, b.x, b.z), b.owner),
                (w, b) =>
                {
                    w.BeginObject().Prop("x", b.x, 1).Prop("z", b.z, 1).Prop("y", b.y, 1).Prop("cat", "bed").Prop("icon", "bed");
                    w.Prop("label", b.owner + "'s bed").Prop("owner", b.owner).End();
                })) yield return y;
            if (slice && WorldSweep.Due) yield return null;

            // --- player bases (clusters of built pieces), named after a portal inside them when there is one
            timer = new PartTimer("bases");
            j.BeginObject().Prop("id", "bases").Prop("label", "Player bases").Key("markers").BeginArray();
            try
            {
                // worked out from the sweep's buildings, so once a sweep: a rebuild between sweeps (a
                // base renamed, a portal or boat moved) reuses them
                if (slice || bases == null) bases = Structures.ComputeBases(MuninConfig.BASE_MIN_PER_CELL, MuninConfig.BASE_MIN_PIECES);
                foreach (var b in bases)
                {
                    if (!Visible(b.x, b.z)) continue;
                    var ov = FindOverride(b.x, b.z);
                    if (ov != null && ov.hidden) continue;
                    string label = ov?.label; float best = 60f * 60f;
                    if (label == null)
                        foreach (var p in portals)
                        {
                            float d = (p.x - b.x) * (p.x - b.x) + (p.z - b.z) * (p.z - b.z);
                            if (d < best && !string.IsNullOrEmpty(p.tag)) { best = d; label = p.tag; }
                        }
                    j.BeginObject().Prop("x", b.x, 1).Prop("z", b.z, 1).Prop("y", b.y, 1).Prop("cat", "base").Prop("icon", "house");
                    j.Prop("label", label != null ? label : "Base").Prop("pieces", b.pieces);
                    string builder = Live.Stats.NameOfPlayerId(b.builder);
                    if (builder != null) j.Prop("by", builder);
                    if (ov != null && ov.label != null) j.Prop("renamed", true);
                    j.End();
                }
            }
            catch (Exception e) { if (MuninConfig.DEBUG) ZLog.LogWarning("MuninMap: bases: " + e.Message); }
            j.End().End();
            timer.Done();
            if (slice && WorldSweep.Due) yield return null;

            // --- vehicles (from the same sweep)
            timer = new PartTimer("vehicles");
            j.BeginObject().Prop("id", "vehicles").Prop("label", "Boats & carts").PropRaw("markers", Vehicles.GetMarkersJson()).End();
            timer.Done();
            if (slice && WorldSweep.Due) yield return null;

            // --- custom markers.json
            timer = new PartTimer("custom");
            string custom = LoadCustom();
            if (custom != null) j.Raw(custom);   // one or more set objects, comma-joined
            timer.Done();

            j.End();   // sets
            j.End();
        }

        // markers.json beside map_data/<world>/: {"sets":[{"id":"...","label":"...","markers":[{"x":0,"z":0,"label":"...","icon":"pin"}]}]}
        private static string LoadCustom()
        {
            try
            {
                string path = Path.Combine(MuninMap.worldDataPath ?? "", "markers.json");
                if (!File.Exists(path)) { customCache = null; return null; }
                var stamp = File.GetLastWriteTimeUtc(path);
                if (customCache != null && stamp == customStamp) return customCache;
                var doc = JsonParser.ParseObject(File.ReadAllText(path));
                var sets = JsonParser.Arr(doc, "sets");
                if (sets == null) { customCache = null; return null; }
                bool first = true;
                var sb = new System.Text.StringBuilder();
                foreach (var so in sets)
                {
                    var set = so as Dictionary<string, object>;
                    if (set == null) continue;
                    var w = new JsonWriter(512);
                    w.BeginObject();
                    w.Prop("id", JsonParser.Str(set, "id", "custom")).Prop("label", JsonParser.Str(set, "label", "Custom"));
                    w.Key("markers").BeginArray();
                    var ms = JsonParser.Arr(set, "markers");
                    if (ms != null)
                        foreach (var mo in ms)
                        {
                            var m = mo as Dictionary<string, object>;
                            if (m == null) continue;
                            w.BeginObject();
                            w.Prop("x", JsonParser.Num(m, "x"), 1).Prop("z", JsonParser.Num(m, "z"), 1);
                            w.Prop("cat", "custom").Prop("label", JsonParser.Str(m, "label", "")).Prop("icon", JsonParser.Str(m, "icon", "pin"));
                            string desc = JsonParser.Str(m, "description", null);
                            if (desc != null) w.Prop("description", desc);
                            w.End();
                        }
                    w.End().End();
                    if (!first) sb.Append(',');
                    sb.Append(w.ToString());
                    first = false;
                }
                customCache = first ? null : sb.ToString();
                customStamp = stamp;
                return customCache;
            }
            catch (Exception e)
            {
                ZLog.LogWarning("MuninMap: markers.json not understood: " + e.Message);
                customCache = null;
                return null;
            }
        }
    }
}
