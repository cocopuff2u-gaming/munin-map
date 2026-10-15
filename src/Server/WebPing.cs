using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MuninMap
{
    // A ping from the web page (middle click on the map), shown in game the way a player's map ping
    // is: the marker on everyone's map and the name over the spot.
    //
    // A ping is the ChatMessage RPC with type Ping, and a player's game only shows it once
    // RelationsManager.CheckPermissionAsync grants the sender's platform id (see Announce.cs: a
    // server has none of its own). So each player is sent it on their own, as from their own id,
    // under the web visitor's name ("Vaskyy (web)"): nobody's game turns down their own id.
    //
    // Anyone who can open the map may ping (web_pings), once every PER_VISITOR seconds each (by
    // address: the page's own client id is up to the page) and once every ANY seconds in all.
    internal static class WebPing
    {
        public const float PER_VISITOR = 10f, ANY = 2f;
        private static readonly Dictionary<string, DateTime> last = new Dictionary<string, DateTime>();
        private static DateTime lastAny = DateTime.MinValue;
        private static readonly Queue<KeyValuePair<Vector3, string>> queue = new Queue<KeyValuePair<Vector3, string>>();
        internal static bool sending;   // our own pings going out: MuninMap's chat watcher leaves them alone

        // any thread: the seconds to wait before this visitor may ping again (0: queued)
        public static int Try(string who, float x, float z, string name)
        {
            var now = DateTime.UtcNow;
            lock (queue)
            {
                double wait = 0;
                if (last.TryGetValue(who, out var t)) wait = PER_VISITOR - (now - t).TotalSeconds;
                wait = Math.Max(wait, ANY - (now - lastAny).TotalSeconds);
                if (wait > 0) return (int)Math.Ceiling(wait);
                last[who] = now; lastAny = now;
                if (last.Count > 512) last.Clear();
                if (queue.Count < 8) queue.Enqueue(new KeyValuePair<Vector3, string>(new Vector3(x, 0f, z), name));
            }
            return 0;
        }

        // main thread
        public static IEnumerator Pump()
        {
            while (true)
            {
                yield return new WaitForSeconds(0.2f);
                KeyValuePair<Vector3, string> p;
                lock (queue) { if (queue.Count == 0) continue; p = queue.Dequeue(); }
                try { Send(p.Key, p.Value); } catch (Exception e) { ZLog.LogWarning("MuninMap: web ping: " + e.Message); }
            }
        }

        private static void Send(Vector3 pos, string name)
        {
            var znet = ZNet.instance;
            if (znet == null || ZRoutedRpc.instance == null) return;
            try { if (WorldGenerator.instance != null) pos.y = Mathf.Max(WorldGenerator.instance.GetHeight(pos.x, pos.z), ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f); } catch { }
            string label = name + " (web)";
            int sent = 0;
            var peers = znet.GetPeers();
            sending = true;
            try
            {
                foreach (var pi in znet.GetPlayerList())
                {
                    if (pi.m_characterID.IsNone()) continue;
                    ZNetPeer peer = null;
                    foreach (var q in peers) if (q.m_characterID == pi.m_characterID) { peer = q; break; }
                    if (peer == null || !peer.IsReady()) continue;
                    var from = new UserInfo { Name = label, UserId = pi.m_userInfo.m_id };
                    ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "ChatMessage", pos, (int)Talker.Type.Ping, from, "");
                    sent++;
                }
            }
            finally { sending = false; }
            MuninMap.mapDataServer?.BroadcastPing(0L, label, pos);   // the web pages show it too
            ZLog.Log($"MuninMap: web ping from \"{name}\" at {pos.x:F0}, {pos.z:F0} sent to {sent} player(s)");
        }
    }
}
