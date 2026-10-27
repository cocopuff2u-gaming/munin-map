using MuninMap.Util;

namespace MuninMap.World
{
    // Bosses the game keeps no world key for. Lord Reto (the Charred Fortress, Ashlands) comes from a
    // spawner that never spawns again (respawn 0 minutes). The spawner keeps a "spawned" link to him,
    // and the game keeps that link through saves even once he's gone (its "done" spawners), which is
    // how it knows not to bring him back. So, by the game's own rule: spawned and gone = beaten.
    internal static class SpawnerBosses
    {
        private static readonly int retoSpawner = "Spawner_Charred_Dyrnwyn".GetStableHashCode();
        private static int found, fought, beaten;          // the last sweep's
        private static int bFound, bFought, bBeaten;       // this sweep's, so far

        public static bool Is(int prefab) => prefab == retoSpawner;

        public static void Begin() { bFound = bFought = bBeaten = 0; }

        // main thread, the sweep's walk
        public static void Observe(ZDO zdo)
        {
            bFound++;
            var c = zdo.GetConnection();
            if (c == null || c.m_type != ZDOExtraData.ConnectionType.Spawned) return;   // nobody has come near yet
            bFought++;
            var him = zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Spawned);
            if (him.IsNone() || ZDOMan.instance.GetZDO(him) == null) bBeaten++;
        }

        public static void Finish() { found = bFound; fought = bFought; beaten = bBeaten; }

        // for the stats: per Lord Reto, how many fortresses the world has made so far, how many of
        // them he has come out in, and how many of those he's been beaten in
        public static void Write(JsonWriter j)
        {
            j.Key("lordReto").BeginObject().Prop("fortresses", found).Prop("fought", fought).Prop("beaten", beaten).End();
        }
    }
}
