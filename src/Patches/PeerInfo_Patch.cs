using System;
using System.Collections.Generic;
using HarmonyLib;

namespace MuninMap.Patches
{
    // The game version and network version each player's game sends when it joins (the server checks
    // them, then forgets them): read from the join message before the game does, kept for the player
    // card, by connection.
    [HarmonyPatch]
    internal static class PeerInfo_Patch
    {
        public struct Info { public string version; public uint network; }
        private static readonly Dictionary<ZRpc, Info> byRpc = new Dictionary<ZRpc, Info>();

        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
        [HarmonyPrefix]
        private static void Peek(ZRpc rpc, ZPackage pkg)
        {
            if (rpc == null || pkg == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            int pos = pkg.GetPos();
            try
            {
                pkg.ReadLong();
                string v = pkg.ReadString();
                uint n = 0;
                if (GameVersion.TryParseGameVersion(v, out var gv) && gv >= Version.FirstVersionWithNetworkVersion) n = pkg.ReadUInt();
                lock (byRpc) { byRpc[rpc] = new Info { version = v, network = n }; if (byRpc.Count > 256) byRpc.Clear(); }
            }
            catch { }
            finally { pkg.SetPos(pos); }
        }

        public static bool TryGet(ZRpc rpc, out Info info)
        {
            info = default;
            if (rpc == null) return false;
            lock (byRpc) return byRpc.TryGetValue(rpc, out info);
        }

        public static void Forget(ZRpc rpc) { if (rpc != null) lock (byRpc) byRpc.Remove(rpc); }
    }
}
