using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace MuninMap
{
    // Boats and carts.
    //
    // Both are player-crafted, so they carry a creator on their ZDO and the
    // structures sweep was already finding them -- painting a karve as an
    // anonymous brown dot in the middle of the ocean. They are not buildings:
    // they move, and what anyone wants from them is "where did I leave it",
    // which wants a marker and a name rather than a pixel.
    //
    // Classified by component rather than prefab name, so a boat added in a
    // later patch is a boat without this needing to learn its name.
    internal static class Vehicles
    {
        internal enum Kind { None, Boat, Cart }

        private struct Entry { public Kind kind; public string name; public float x, z; }

        private static readonly Dictionary<int, Kind> kindCache = new Dictionary<int, Kind>();
        private static readonly Dictionary<int, string> nameCache = new Dictionary<int, string>();
        private static readonly List<Entry> found = new List<Entry>();
        private static string json = "{\"boats\":0,\"carts\":0,\"vehicles\":[]}";
        private static volatile string markersJson = "[]";

        public static Kind Classify(int prefabHash)
        {
            if (kindCache.TryGetValue(prefabHash, out var cached)) return cached;
            Kind k = Kind.None;
            string label = null;
            try
            {
                var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabHash) : null;
                if (go != null)
                {
                    if (go.GetComponent<Ship>() != null) k = Kind.Boat;
                    else if (go.GetComponent<Vagon>() != null) k = Kind.Cart;
                    if (k != Kind.None) label = go.name;
                }
            }
            catch { }
            kindCache[prefabHash] = k;
            nameCache[prefabHash] = label ?? "";
            return k;
        }

        private static readonly HashSet<ZDOID> seen = new HashSet<ZDOID>();   // a boat can be met twice in a sweep (WorldSweep)

        public static void Begin() { found.Clear(); seen.Clear(); }

        public static void Observe(ZDOID id, int prefabHash, Kind kind, Vector3 pos)
        {
            if (!seen.Add(id)) return;
            nameCache.TryGetValue(prefabHash, out string name);
            found.Add(new Entry { kind = kind, name = name ?? "", x = pos.x, z = pos.z });
        }

        // The structures layer is drawn under the fog mask by the page, so builds in
        // unexplored land are hidden for free. Markers cannot be masked that way, so
        // the filtering happens here instead -- a boat somewhere nobody has been is
        // not reported at all, rather than merely not drawn.
        private static bool Explored(float x, float z) => MuninConfig.REVEAL_ALL || World.Fog.IsExplored(x, z);

        public static void Finish()
        {
            int boats = 0, carts = 0;
            var sb = new StringBuilder();
            var mk = new StringBuilder("[");
            sb.Append("{\"vehicles\":[");
            bool first = true;
            for (int i = 0; i < found.Count; i++)
            {
                var e = found[i];
                if (!MuninConfig.SHOW_VEHICLES || !Explored(e.x, e.z)) continue;
                if (e.kind == Kind.Boat) boats++; else carts++;
                if (!first) sb.Append(",");
                first = false;
                string kind = e.kind == Kind.Boat ? "boat" : "cart";
                string name = e.name.Replace("\"", "");
                sb.Append(System.FormattableString.Invariant(
                    $"{{\"kind\":\"{kind}\",\"name\":\"{name}\",\"x\":{e.x:0.#},\"z\":{e.z:0.#}}}"));
                if (mk.Length > 1) mk.Append(",");
                mk.Append(System.FormattableString.Invariant(
                    $"{{\"x\":{e.x:0.#},\"z\":{e.z:0.#},\"cat\":\"{kind}\",\"icon\":\"{kind}\",\"label\":\"{PrettyName(name)}\"}}"));
            }
            mk.Append("]");
            markersJson = mk.ToString();
            sb.Append("],\"boats\":").Append(boats).Append(",\"carts\":").Append(carts).Append("}");
            json = sb.ToString();
        }

        public static string GetJson() => json;
        public static string GetMarkersJson() => markersJson;

        private static string PrettyName(string prefab)
        {
            switch (prefab.ToLowerInvariant())
            {
                case "raft": return "Raft";
                case "karve": return "Karve";
                case "vikingship": return "Longship";
                case "vikingship_ashlands": return "Drakkar";
                case "trailership": return "Trailer ship";
                case "cart": return "Cart";
                default: return prefab.Replace("_", " ");
            }
        }
    }
}
