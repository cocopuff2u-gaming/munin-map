using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;
using MuninMap.Live;
using MuninMap.Patches;
using MuninMap.Util;
using MuninMap.Tiles;
using MuninMap.World;
using static ZRoutedRpc;
using Random = UnityEngine.Random;

namespace MuninMap
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class MuninMap : BaseUnityPlugin
    {
        public const string GUID = "com.cocopuff2u.muninmap";
        public const string NAME = "Munin Map";
        public const string VERSION = "3.2.3";
        private const string OLD_GUID = "com.valheimwebmap.server";   // the mod this one was, Valheim WebMap

        private static readonly string[] ALLOWED_PINS = { "dot", "fire", "mine", "house", "cave" };

        public DiscordWebHook discordWebHook;
        public static MapDataServer mapDataServer;
        public static string worldDataPath;
        public static string mapDataPath;
        public static string pluginPath;

        public static int sayMethodHash = 0;
        private static readonly int SayHash = "Say".GetStableHashCode(), ChatHash = "ChatMessage".GetStableHashCode();
        public static int chatMessageMethodHash = 0;

        public static string currentWorldName;
        public static Dictionary<string, object> serverInfo;

        private static Harmony harmony;
        public static MuninMap instance;

        public void Awake()
        {
            instance = this;
            pluginPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            TakeOldSettings();
            Live.ServerLog.Listen();   // the game's console lines from the start (the admin "Server log" view)
            MuninConfig.ReadConfigFile(Config);   // (before the patches: some are only made with debug on)
            harmony = new Harmony(GUID);
            harmony.PatchAll(Assembly.GetExecutingAssembly());

            mapDataPath = Path.Combine(pluginPath ?? string.Empty, "map_data");
            Directory.CreateDirectory(mapDataPath);
            discordWebHook = new DiscordWebHook(MuninConfig.DISCORD_WEBHOOK);
            ZLog.Log($"Munin Map {VERSION} loaded");
        }

        // Valheim WebMap, the mod this one grew out of: its settings come along once, from the
        // config folder. Its own folder is never touched; if its map is still there, the log says
        // how to bring it over.
        private void TakeOldSettings()
        {
            try
            {
                string cfg = Path.Combine(Paths.ConfigPath, GUID + ".cfg"), oldCfg = Path.Combine(Paths.ConfigPath, OLD_GUID + ".cfg");
                if (!File.Exists(cfg) && File.Exists(oldCfg))
                {
                    File.Copy(oldCfg, cfg);
                    Config.Reload();
                    ZLog.Log("MuninMap: settings taken over from " + oldCfg);
                }
                // the old mod's name for the map's address setting
                if (File.Exists(cfg))
                {
                    string text = File.ReadAllText(cfg);
                    if (text.Contains("\nwebmap_url =") && !text.Contains("\nmap_url ="))
                    {
                        File.WriteAllText(cfg, text.Replace("\nwebmap_url =", "\nmap_url ="));
                        Config.Reload();
                    }
                }
            }
            catch (Exception e) { ZLog.LogWarning("MuninMap: settings from Valheim WebMap: " + e.Message); }
        }

        // every plugin has loaded by now
        public void Start()
        {
            if (Chainloader.PluginInfos.ContainsKey(OLD_GUID))
                ZLog.LogWarning("MuninMap: Valheim WebMap is installed too. Both serve a map on the same port and do the same work: switch one of them off.");
            try
            {
                string data = Path.Combine(pluginPath ?? "", "map_data");
                if (Directory.Exists(data) && Directory.GetFileSystemEntries(data).Length > 0) return;   // already has a map
                foreach (string d in Directory.GetDirectories(Paths.PluginPath))
                    if (!string.Equals(d, pluginPath, StringComparison.OrdinalIgnoreCase)
                        && Path.GetFileName(d).IndexOf("webmap", StringComparison.OrdinalIgnoreCase) >= 0
                        && Directory.Exists(Path.Combine(d, "map_data")))
                    {
                        ZLog.Log("MuninMap: a map from Valheim WebMap is in " + Path.Combine(d, "map_data") + ". To keep it, stop the server and move that folder next to MuninMap.dll.");
                        break;
                    }
            }
            catch { }
        }

        // No Config.Save() here: BepInEx writes the file when the entries are bound,
        // and saving on shutdown overwrote edits made while the server was running.
        public void OnDestroy() { }

        // ---------------------------------------------------------------- world lifecycle

        public void SetServerInfo(bool openServer, bool publicServer, string serverName, string password, string worldName)
        {
            serverInfo = new Dictionary<string, object>
            {
                ["openServer"] = openServer, ["publicServer"] = publicServer, ["serverName"] = serverName,
                ["password"] = password, ["worldName"] = worldName
                // the world seed is deliberately not kept anywhere: it must never reach the web
            };
        }

        public void NewWorld()
        {
            string worldName = MuninConfig.GetWorldName();
            bool forceReload = currentWorldName != null && currentWorldName != worldName;

            worldDataPath = Path.Combine(mapDataPath, worldName);
            Directory.CreateDirectory(worldDataPath);

            if (mapDataServer == null)
            {
                ZLog.Log($"MuninMap: loading world '{worldName}'");
                mapDataServer = new MapDataServer();
            }
            else if (forceReload)
            {
                ZLog.Log($"MuninMap: switching world from '{currentWorldName}' to '{worldName}'");
            }
            currentWorldName = worldName;

            // single-image world render (served at /map for simple clients). It shows the whole
            // world with no fog, so only load it when legacy_map is on: turning the setting off
            // must also stop serving a map.png left over from an earlier run.
            try
            {
                string mapImagePath = Path.Combine(worldDataPath, "map.png");
                if (MuninConfig.LEGACY_MAP && File.Exists(mapImagePath))
                {
                    mapDataServer.mapImageData = File.ReadAllBytes(mapImagePath);
                    mapDataServer.BuildMapJpg();
                }
            }
            catch (Exception e) { ZLog.LogWarning("MuninMap: legacy map.png not readable: " + e.Message); }

            Fog.Init(MuninConfig.TEXTURE_SIZE, MuninConfig.PIXEL_SIZE);
            string fogPath = Path.Combine(worldDataPath, "fog.png");
            if (!Fog.Load(fogPath) || Fog.Dirty)   // dirty: converted from another grid
            {
                if (!Fog.Dirty) ZLog.Log("MuninMap: starting a fresh fog of war");
                Fog.Save(fogPath);
            }

            try
            {
                string pinsFile = Path.Combine(worldDataPath, "pins.csv");
                if (File.Exists(pinsFile)) mapDataServer.pins = new List<string>(File.ReadAllLines(pinsFile));
            }
            catch (Exception e) { ZLog.LogWarning("MuninMap: pins.csv not readable: " + e.Message); }

            Stats.Load();
            Activity.Load(worldDataPath);
            BuildHistory.Load(worldDataPath);
            Hotspots.Load(worldDataPath);
            Peaks.Start(worldDataPath);
            Events.LoadTail();
            Live.ServerLog.Open(worldDataPath);
            Markers.LoadOverrides(worldDataPath);
            Markers.LoadCache(worldDataPath);
            // what the last sweep published, so a restarted server shows it before its first sweep
            Structures.LoadCache(worldDataPath);
            Ruins.LoadCache(worldDataPath);
            Vegetation.LoadCache(worldDataPath);
            TileStore.Init(worldDataPath);
            Models.ModelStore.Init(mapDataPath);

            if (forceReload) mapDataServer.Reload();
        }

        public void Online()
        {
            StaticCoroutine.Start(Perf.Wrap("snapshot", PlayerSnapshotLoop()));
            StaticCoroutine.Start(Perf.Wrap("raids", Live.WorldEvents.RaidLoop()));
            UpdateCheck.Start();
            StaticCoroutine.Start(Perf.Wrap("discoveries", Live.WorldEvents.DiscoveryLoop()));
            StaticCoroutine.Start(Perf.Wrap("fog", UpdateFogLoop()));
            StaticCoroutine.Start(Perf.Wrap("saves", SaveLoop()));
            StaticCoroutine.Start(Perf.Wrap("runestones", World.Runestones.PrepareRoutine(mapDataPath)));
            StaticCoroutine.Start(Perf.Wrap("sweep", WorldSweep.Loop()));
            StaticCoroutine.Start(Perf.Wrap("markers live", World.Markers.LiveLoop()));
            StaticCoroutine.Start(Perf.Wrap("announce", Announce.Pump()));
            StaticCoroutine.Start(Perf.Wrap("web pings", WebPing.Pump()));
            StaticCoroutine.Start(Perf.Wrap("tile sampling", TileStore.MainThreadPump()));
            StaticCoroutine.Start(Perf.Wrap("model export", Models.ModelStore.Pump()));
            StaticCoroutine.Start(Perf.Wrap("portraits", Models.Portraits.Pump()));
            TileStore.Start();
            World.MapIcons.Start(mapDataPath);
            World.ItemIcons.Start(mapDataPath);
            World.BiomeMap.Start(worldDataPath);
            // close-zoom tiles for everything already explored (cheap: only queues what is missing)
            int stride = Math.Max(1, (int)(TileMath.TileSpanMeters(MuninConfig.PRERENDER_ZOOM + 1) / MuninConfig.PIXEL_SIZE / 2));
            Fog.ForEachExplored(TileStore.OnExplored, stride);
            Events.Add("server", "Server", "online");
            NotifyOnline();
        }

        public void NotifyOnline()
        {
            try
            {
                string ip = AccessTools.Method(typeof(ZNet), "GetServerIP")?.Invoke(ZNet.instance, new object[] { })?.ToString() ?? "";
                discordWebHook.SendMessage($"🎮 **{serverInfo["serverName"]}** is *online* 🟢\n💻 {ip}:{ZNet.instance.GetHostPort()}\n🔑 {serverInfo["password"]}\n🗺 {MuninConfig.URL}");
            }
            catch (Exception e) { if (MuninConfig.DEBUG) ZLog.LogWarning("MuninMap: online notice failed: " + e.Message); }
        }

        public void NotifyOffline()
        {
            try { discordWebHook.SendMessage($"🎮 **{serverInfo["serverName"]}** is *offline* 🔴"); } catch { }
        }

        // a world event (raid, invasion) for the Discord webhook, with a link to it on the map when the
        // map's address is set
        public static void PostWorldEvent(string message, float? x = null, float? z = null)
        {
            if (!MuninConfig.DISCORD_WORLD_EVENTS || string.IsNullOrEmpty(MuninConfig.DISCORD_WEBHOOK)) return;
            try
            {
                var w = instance;
                if (w == null || w.discordWebHook == null) return;
                string link = "";
                if (x.HasValue && z.HasValue && !string.IsNullOrEmpty(MuninConfig.URL))
                    link = $" ([map]({MuninConfig.URL.TrimEnd('#')}#{Mathf.RoundToInt(x.Value)},{Mathf.RoundToInt(z.Value)},6))";
                w.discordWebHook.SendMessage($"🎮 **{serverInfo["serverName"]}** {message}{link}");
            }
            catch { }
        }

        public void NotifyJoin(ZNetPeer peer)
        {
            string message = $"player _{peer.m_playerName}_ joined";
            discordWebHook.SendMessage($"🎮 **{serverInfo["serverName"]}** {message}");
            Events.Add("join", peer.m_playerName, "joined the server");
            Stats.OnJoin(Players.KeyOf(peer), peer.m_playerName);
        }

        public void NotifyLeave(ZNetPeer peer)
        {
            string message = $"player _{peer.m_playerName}_ left";
            discordWebHook.SendMessage($"🎮 **{serverInfo["serverName"]}** {message}");
            Announce.Enqueue(message, feed: false);   // in game only: the feed has the leave event below
            Events.Add("leave", peer.m_playerName, "left the server");
            Stats.OnLeave(Players.KeyOf(peer), peer.m_playerName);
            Players.Forget(peer);
            Patches.PeerInfo_Patch.Forget(peer.m_rpc);
        }

        // ---------------------------------------------------------------- loops (main thread)

        public IEnumerator PlayerSnapshotLoop()
        {
            while (true)
            {
                try
                {
                    Players.Refresh(mapDataServer.players);
                    mapDataServer.BroadcastPlayers();
                    Stats.OnTick(Players.Current);
                    Stats.Serve();   // the stats text, remade only when a browser is waiting for it
                    Activity.Sample(Players.Current);
                }
                catch (Exception e) { if (MuninConfig.DEBUG) ZLog.LogWarning("MuninMap: player snapshot failed: " + e.Message); }
                yield return new WaitForSeconds(MuninConfig.PLAYER_UPDATE_INTERVAL);
            }
        }

        public IEnumerator UpdateFogLoop()
        {
            float visitedT = 0f;
            while (true)
            {
                yield return new WaitForSeconds(MuninConfig.UPDATE_FOG_TEXTURE_INTERVAL);
                try
                {
                    foreach (var p in Players.Current)
                    {
                        if (!p.tracked || p.dead) continue;
                        int n = Fog.Reveal(p.x, p.z, MuninConfig.EXPLORE_RADIUS);
                        if (n > 0) Stats.OnRevealed(p.key, p.name, n);
                    }
                    // zones generated since the last look (ships, hidden players): once a minute is plenty
                    visitedT += MuninConfig.UPDATE_FOG_TEXTURE_INTERVAL;
                    if (visitedT >= 60f || Fog.VisitedPending) { visitedT = 0f; Fog.RevealVisitedZones(); }   // (a worked-out answer is applied as soon as it's in)
                }
                catch (Exception e) { if (MuninConfig.DEBUG) ZLog.LogWarning("MuninMap: fog update failed: " + e.Message); }
            }
        }

        public IEnumerator SaveLoop()
        {
            float fogT = 0f, statsT = 0f;
            while (true)
            {
                yield return new WaitForSeconds(5f);
                fogT += 5f; statsT += 5f;
                Perf.Report();   // (perf_report: every 5 minutes)
                if (fogT >= MuninConfig.SAVE_FOG_TEXTURE_INTERVAL)
                {
                    fogT = 0f;
                    if (Fog.Dirty) Fog.Save(Path.Combine(worldDataPath, "fog.png"));
                }
                if (statsT >= MuninConfig.STATS_SAVE_INTERVAL)
                {
                    statsT = 0f;
                    Stats.Save();
                    Activity.Save();
                }
            }
        }

        public static void SavePins()
        {
            try { lock (mapDataServer.pins) File.WriteAllLines(Path.Combine(worldDataPath, "pins.csv"), mapDataServer.pins); }
            catch (Exception e) { ZLog.Log("MuninMap: FAILED TO WRITE PINS FILE! " + e.Message); }
        }

        // ---------------------------------------------------------------- pins
        //
        // Pins come from chat (!pin, !undopin, !deletepin) and from the web page (POST /api/pin).
        // On Valheim 1.0 chat is sent player to player, so the server only sees it while it is
        // forwarding between two or more players: a lone player's !pin never reaches us. The web
        // page is the way that always works.

        public static readonly Regex PinTextFilter = new Regex("[^a-zA-Z0-9 ]", RegexOptions.Compiled);

        public static string CleanPinText(string text, int max = 20)
        {
            text = PinTextFilter.Replace((text ?? "").Trim(), "");
            return text.Length > max ? text.Substring(0, max) : text;
        }

        public static string CleanPinType(string type) => Array.Exists(ALLOWED_PINS, e => e == (type ?? "").ToLower()) ? type.ToLower() : "dot";

        // owner: who may undo it (steam id for chat, "web:<client>" for the page)
        public static string PlacePin(string owner, string type, string name, Vector3 pos, string text)
        {
            long timestamp = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds();
            string pinId = $"{timestamp}-{Random.Range(1000, 9999)}";
            mapDataServer.AddPin(owner, pinId, CleanPinType(type), name, pos, CleanPinText(text));

            int overflow;
            lock (mapDataServer.pins) overflow = mapDataServer.pins.FindAll(pin => pin.StartsWith(owner + ",")).Count - MuninConfig.MAX_PINS_PER_USER;
            for (int t = overflow; t > 0; t--)
            {
                int pinIdx;
                lock (mapDataServer.pins) pinIdx = mapDataServer.pins.FindIndex(pin => pin.StartsWith(owner + ","));
                if (pinIdx > -1) mapDataServer.RemovePin(pinIdx);
            }
            SavePins();
            return pinId;
        }

        public static bool UndoPin(string owner)
        {
            int pinIdx;
            lock (mapDataServer.pins) pinIdx = mapDataServer.pins.FindLastIndex(pin => pin.StartsWith(owner + ","));
            if (pinIdx < 0) return false;
            mapDataServer.RemovePin(pinIdx); SavePins();
            return true;
        }

        public static bool DeletePinByText(string owner, string text)
        {
            int pinIdx;
            lock (mapDataServer.pins) pinIdx = mapDataServer.pins.FindLastIndex(pin =>
            {
                string[] pinParts = pin.Split(',');
                return pinParts[0] == owner && pinParts[pinParts.Length - 1] == text;
            });
            if (pinIdx < 0) return false;
            mapDataServer.RemovePin(pinIdx); SavePins();
            return true;
        }

        // remove one pin by id; owner "" (token holder) may remove any
        public static bool DeletePinById(string owner, string pinId)
        {
            int pinIdx;
            lock (mapDataServer.pins) pinIdx = mapDataServer.pins.FindIndex(pin =>
            {
                string[] pinParts = pin.Split(',');
                return pinParts.Length > 1 && pinParts[1] == pinId && (owner.Length == 0 || pinParts[0] == owner);
            });
            if (pinIdx < 0) return false;
            mapDataServer.RemovePin(pinIdx); SavePins();
            return true;
        }

        // true when the message was a command (and so not ordinary chat)
        private static bool HandleChatCommand(string owner, string name, Vector3 pos, string message)
        {
            string upper = message.ToUpper();
            if (upper.StartsWith("!PIN", StringComparison.Ordinal))
            {
                string[] parts = message.Split(' ');
                string type = "dot"; int startIdx = 1;
                if (parts.Length > 1 && Array.Exists(ALLOWED_PINS, e => e == parts[1].ToLower())) { type = parts[1].ToLower(); startIdx = 2; }
                string text = startIdx < parts.Length ? string.Join(" ", parts, startIdx, parts.Length - startIdx) : "";
                PlacePin(owner, type, name, pos, text);
                return true;
            }
            if (upper.StartsWith("!UNDOPIN", StringComparison.Ordinal)) { UndoPin(owner); return true; }
            if (upper.StartsWith("!DELETEPIN", StringComparison.Ordinal))
            {
                string[] parts = message.Split(' ');
                DeletePinByText(owner, parts.Length > 1 ? string.Join(" ", parts, 1, parts.Length - 1) : "");
                return true;
            }
            return false;
        }

        // ---------------------------------------------------------------- patches

        // Single-image world render, served at /map and used as the page's fallback base layer.
        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.Start))]
        private class ZoneSystemPatch
        {
            private static readonly Color DeepWaterColor = new Color(0.36105883f, 0.36105883f, 0.43137255f);
            private static readonly Color ShallowWaterColor = new Color(0.574f, 0.50709206f, 0.47892025f);
            private static readonly Color ShoreColor = new Color(0.1981132f, 0.12241901f, 0.1503943f);

            private static Color GetPixelColor(Heightmap.Biome biome)
            {
                switch (biome)
                {
                    case Heightmap.Biome.Meadows: return new Color(0.573f, 0.655f, 0.361f);
                    case Heightmap.Biome.Swamp: return new Color(0.639f, 0.447f, 0.345f);
                    case Heightmap.Biome.Mountain: return Color.white;
                    case Heightmap.Biome.BlackForest: return new Color(0.420f, 0.455f, 0.247f);
                    case Heightmap.Biome.Plains: return new Color(0.906f, 0.671f, 0.470f);
                    case Heightmap.Biome.AshLands: return new Color(0.690f, 0.192f, 0.192f);
                    case Heightmap.Biome.DeepNorth: return Color.white;
                    case Heightmap.Biome.Mistlands: return new Color(0.36f, 0.22f, 0.4f);
                    default: return Color.white;
                }
            }

            private static void Postfix(ZoneSystem __instance)
            {
                MuninMap.instance.NewWorld();
                if (mapDataServer.mapImageData != null || !MuninConfig.LEGACY_MAP) return;

                ZLog.Log("MuninMap: building legacy world render (once)");
                int size = MuninConfig.TEXTURE_SIZE, num = size / 2;
                float num2 = MuninConfig.PIXEL_SIZE / 2f;
                Color32[] colorArray = new Color32[size * size];
                float[] heightArray = new float[size * size];
                for (int i = 0; i < size; i++)
                    for (int j = 0; j < size; j++)
                    {
                        float wx = (j - num) * MuninConfig.PIXEL_SIZE + num2;
                        float wy = (i - num) * MuninConfig.PIXEL_SIZE + num2;
                        Heightmap.Biome biome = WorldGenerator.instance.GetBiome(wx, wy);
                        float biomeHeight = WorldGenerator.instance.GetBiomeHeight(biome, wx, wy, out Color _);
                        colorArray[i * size + j] = GetPixelColor(biome);
                        heightArray[i * size + j] = biomeHeight;
                    }

                float waterLevel = ZoneSystem.instance.m_waterLevel;
                Vector3 sunDir = new Vector3(-0.57735f, 0.57735f, 0.57735f);
                Color[] newColors = new Color[colorArray.Length];
                for (int t = 0; t < colorArray.Length; t++)
                {
                    float h = heightArray[t];
                    int tUp = t - size; if (tUp < 0) tUp = t;
                    int tDown = t + size; if (tDown > colorArray.Length - 1) tDown = t;
                    int tRight = t + 1; if (tRight > colorArray.Length - 1) tRight = t;
                    int tLeft = t - 1; if (tLeft < 0) tLeft = t;
                    Vector3 va = new Vector3(2f, 0f, heightArray[tRight] - heightArray[tLeft]).normalized;
                    Vector3 vb = new Vector3(0f, 2f, heightArray[tUp] - heightArray[tDown]).normalized;
                    float surfaceLight = Vector3.Dot(Vector3.Cross(va, vb), sunDir) * 0.25f + 0.75f;
                    float shoreMask = Mathf.Clamp(h - waterLevel, 0, 1);
                    float shallowRamp = Mathf.Clamp((h - waterLevel + 0.2f * 12.5f) * 0.5f, 0, 1);
                    float deepRamp = Mathf.Clamp((h - waterLevel + 1f * 12.5f) * 0.1f, 0, 1);
                    Color ans = Color.Lerp(ShoreColor, colorArray[t], shoreMask);
                    ans = Color.Lerp(ShallowWaterColor, ans, shallowRamp);
                    ans = Color.Lerp(DeepWaterColor, ans, deepRamp);
                    newColors[t] = new Color(ans.r * surfaceLight, ans.g * surfaceLight, ans.b * surfaceLight, ans.a);
                }

                Texture2D newTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
                newTexture.SetPixels(newColors);
                byte[] pngBytes = ImageConv.EncodeToPNG(newTexture);
                UnityEngine.Object.Destroy(newTexture);
                mapDataServer.mapImageData = pngBytes;
                mapDataServer.BuildMapJpg();
                try { File.WriteAllBytes(Path.Combine(worldDataPath, "map.png"), pngBytes); }
                catch (Exception e) { ZLog.LogError("MuninMap: FAILED TO WRITE MAP FILE! " + e.Message); }
            }
        }

        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.Load))]
        private class ZoneSystemLoadPatch
        {
            private static void Postfix()
            {
                if (ZoneSystem.instance.FindClosestLocation("StartTemple", Vector3.zero, out ZoneSystem.LocationInstance startLocation))
                {
                    MuninConfig.WORLD_START_POS = startLocation.m_position;
                    ZLog.Log("MuninMap: starting point " + MuninConfig.WORLD_START_POS);
                }
                else ZLog.LogWarning("MuninMap: failed to find starting point");

                MuninMap.instance.Online();
                mapDataServer.ListenAsync();
                try { Fog.RevealVisitedZones(wait: true); } catch (Exception e) { ZLog.LogWarning("MuninMap: visited-zone reveal failed: " + e.Message); }
            }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Start))]
        private class ZNetPatchStart
        {
            private static void Postfix(List<ZNetPeer> ___m_peers) { mapDataServer.players = ___m_peers; }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
        private class ZNetPatchShutdown
        {
            private static void Postfix()
            {
                try { Stats.EndSessions(); Stats.Save(force: true); } catch { }
                try { Activity.Save(); } catch { }
                try { if (Fog.Dirty) Fog.Save(Path.Combine(worldDataPath, "fog.png")); } catch { }
                TileStore.Stop();
                mapDataServer.Stop();
                MuninMap.instance.NotifyOffline();
            }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.SetServer))]
        private class ZNetPatchSetServer
        {
            // global:: because the mod's MuninMap.World namespace shadows the game's World class here
            private static void Postfix(bool server, bool openServer, bool publicServer, string serverName, string password, global::World world)
            {
                MuninMap.instance.SetServerInfo(openServer, publicServer, serverName, password, world.m_name);
            }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
        private class ZNetPatchDisconnect
        {
            private static void Prefix(ref ZNetPeer peer)
            {
                if (!peer.m_server && !string.IsNullOrEmpty(peer.m_playerName)) MuninMap.instance.NotifyLeave(peer);
            }
        }

        [HarmonyPatch(typeof(ZRoutedRpc), nameof(ZRoutedRpc.AddPeer))]
        private class ZRoutedRpcAddPeerPatch
        {
            private static void Postfix(ZNetPeer peer)
            {
                if (!peer.m_server && !string.IsNullOrEmpty(peer.m_playerName)) MuninMap.instance.NotifyJoin(peer);
            }
        }

        // Chat on 1.0 is addressed per recipient and passes through RouteRPC on the
        // server while it forwards; HandleRoutedRPC only sees Everybody-targeted
        // traffic (pings). See README "How chat reaches the server on 1.0".
        [HarmonyPatch(typeof(ZRoutedRpc), "RouteRPC")]
        private class ZRoutedRpcRoutePatch
        {
            private static readonly Dictionary<string, float> recent = new Dictionary<string, float>();

            private static void Prefix(ref ZRoutedRpc __instance, RoutedRPCData rpcData)
            {
                if (rpcData == null || rpcData.m_targetPeerID == 0L || WebPing.sending) return;
                try
                {
                    if (IsDuplicate(rpcData)) return;
                    RoutedRPCData data = rpcData;
                    ZRoutedRpcPatch.Observe(ref __instance, ref data);
                }
                catch (Exception ex) { ZLog.LogWarning("MuninMap: failed observing a routed rpc: " + ex); }
            }

            private static bool IsDuplicate(RoutedRPCData d)
            {
                byte[] body = d.m_parameters != null ? d.m_parameters.GetArray() : null;
                uint h = 2166136261u;
                if (body != null) foreach (byte b in body) { h ^= b; h *= 16777619u; }
                string key = d.m_senderPeerID + ":" + d.m_methodHash + ":" + h;
                float now = Time.realtimeSinceStartup;
                if (recent.TryGetValue(key, out float seen) && now - seen < 2f) return true;
                recent[key] = now;
                if (recent.Count > 256)
                {
                    var stale = new List<string>();
                    foreach (var kv in recent) if (now - kv.Value > 10f) stale.Add(kv.Key);
                    foreach (var k in stale) recent.Remove(k);
                }
                return false;
            }
        }

        [HarmonyPatch(typeof(ZRoutedRpc), nameof(ZRoutedRpc.HandleRoutedRPC))]
        private class ZRoutedRpcPatch
        {
            private static readonly string[] ignoreRpc = { "DestroyZDO", "SetEvent", "OnTargeted", "Step" };

            private static void Postfix(ref ZRoutedRpc __instance, ref RoutedRPCData data) => Observe(ref __instance, ref data);

            internal static void Observe(ref ZRoutedRpc __instance, ref RoutedRPCData data)
            {
                long perf = Perf.Start();
                try { ObserveTimed(ref __instance, ref data); } finally { Perf.Stop("message hook", perf); }
            }

            private static void ObserveTimed(ref ZRoutedRpc __instance, ref RoutedRPCData data)
            {
                int hash = data?.m_methodHash ?? 0;
                if (hash == 0) return;
                if (hash == Live.WorldEvents.SetGlobalKeyHash) { Live.WorldEvents.OnGlobalKey(data); return; }   // boss kills
                // (every routed message comes through here: the two hashes once, not twice a message)
                bool isSay = hash == sayMethodHash || hash == SayHash;
                bool isChat = hash == chatMessageMethodHash || hash == ChatHash;
                if (!isSay && !isChat)
                {
                    if (MuninConfig.DEBUG)
                    {
                        string other = StringExtensionMethods_Patch.GetStableHashName(hash);
                        if (!Array.Exists(ignoreRpc, x => x == other)) ZLog.Log("RoutedRPC: " + other);
                    }
                    return;
                }

                ZNetPeer peer = ZNet.instance.GetPeer(data.m_senderPeerID);
                string steamid = "";
                if (peer != null) { try { steamid = peer.m_rpc.GetSocket().GetHostName(); } catch { } }

                if (isSay)
                {
                    sayMethodHash = data.m_methodHash;
                    try
                    {
                        // the talker's own ZDO is the rpc target on 1.0; fall back to the peer
                        ZDO zdoData = !data.m_targetZDO.IsNone() ? ZDOMan.instance.GetZDO(data.m_targetZDO) : null;
                        if (zdoData == null && peer != null) zdoData = ZDOMan.instance.GetZDO(peer.m_characterID);
                        Vector3 pos = zdoData != null ? zdoData.GetPosition() : (peer != null ? peer.m_refPos : Vector3.zero);
                        ZPackage package = new ZPackage(data.m_parameters.GetArray());   // a copy: the original must still be forwarded
                        var messageType = package.ReadInt();
                        var userInfo = new UserInfo();
                        userInfo.Deserialize(ref package);
                        string message = (package.ReadString() ?? "").Trim();

                        if (!HandleChatCommand(steamid, userInfo.Name, pos, message) && messageType != (int)Talker.Type.Whisper)
                        {
                            mapDataServer.AddMessage(data.m_senderPeerID, messageType, userInfo.Name, message, pos);
                        }
                    }
                    catch (Exception ex) { ZLog.LogWarning("MuninMap: failed handling a chat message: " + ex); }
                }
                else
                {
                    chatMessageMethodHash = data.m_methodHash;
                    try
                    {
                        ZPackage package = new ZPackage(data.m_parameters.GetArray());
                        Vector3 pos = package.ReadVector3();
                        var messageType = package.ReadInt();
                        var userInfo = new UserInfo();
                        userInfo.Deserialize(ref package);
                        if (messageType == (int)Talker.Type.Ping)
                        {
                            mapDataServer.BroadcastPing(data.m_senderPeerID, userInfo.Name, pos);
                        }
                        else
                        {
                            var message = (package.ReadString() ?? "").Trim();
                            if (!HandleChatCommand(steamid, userInfo.Name, pos, message))
                                mapDataServer.AddMessage(data.m_senderPeerID, messageType, userInfo.Name, message, pos);
                        }
                    }
                    catch (Exception ex) { if (MuninConfig.DEBUG) ZLog.LogError(ex.ToString()); }
                }
            }
        }
    }

    public class StaticCoroutine
    {
        private static StaticCoroutineRunner runner;

        public static Coroutine Start(IEnumerator coroutine)
        {
            if (runner == null)
            {
                runner = new GameObject("[MuninMap Coroutines]").AddComponent<StaticCoroutineRunner>();
                UnityEngine.Object.DontDestroyOnLoad(runner.gameObject);
            }
            return runner.StartCoroutine(coroutine);
        }

        private class StaticCoroutineRunner : MonoBehaviour { }
    }
}
