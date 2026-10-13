using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using WebSocketSharp;
using WebSocketSharp.Net;
using WebSocketSharp.Server;
using MuninMap.Live;
using MuninMap.Tiles;
using MuninMap.Util;
using MuninMap.World;
using static MuninMap.MuninConfig;

namespace MuninMap
{
    // The HTTP + websocket front door.
    //
    // Everything served here is a string or byte array that some other part
    // of the mod built on the thread it was safe to build on; the request
    // handlers never touch the game. Tiles come from disk through TileStore,
    // vector data from the sweep's published chunks, live state from the
    // snapshots.
    //
    // Routes (all GET unless noted):
    //   /                          the web app (static files under web/, subfolders allowed)
    //   /config                    client configuration
    //   /tiles/map/{z}/{x}/{y}.png rendered map tile      (404 + X-Munin-Tile: pending while it renders)
    //   /tiles/height/{z}/{x}/{y}.png  Terrarium-encoded height tile
    //   /tiles/veg/{z}/{x}/{y}.png    transparent overlay of tree crowns and rocks (zoom 5+, 2D only)
    //   /data/structures/index.json, /data/structures/{cx}_{cz}.json
    //   /data/ruins/index.json, /data/ruins/{cx}_{cz}.json   world-generated structures (explored only)
    //   /data/structures/r/{rx}_{rz}.json, /data/ruins/r/{rx}_{rz}.json   8x8 chunks in one go (see Regions)
    //   /data/veg/{cx}_{cz}.bin    vegetation points for a chunk
    //   /data/veg/index.json, /data/veg/r3/{rx}_{rz}.bin  4x4 chunks of vegetation in one go (see Vegetation)
    //   /data/markers.json         marker sets (locations, portals, tombstones, vehicles, custom)
    //   /data/players.json, /data/stats.json, /data/events.json, /data/pins.json, /data/fog.png
    //   /api/status                renderer and sweep status
    //   /api/rerender?zoom=N (POST, token)   re-render tiles from zoom N up
    //   /api/reexport (POST, token)  re-export all prefab models (after extracting textures)
    //   /api/reload (POST, token)    drop cached web files and tell open browsers to refresh (no restart)
    //   /api/sweep (POST)          run a world sweep now
    //   /api/pin (POST) place a pin from the page, /api/unpin?id= (POST) remove one of your own
    //   /api/base (POST) rename or hide an auto-detected base, /api/bases/reset (POST, token)
    //   simple endpoints: /map /map.jpg /fog /players /pins /messages /structures /structures/stats
    //               /structures/refresh /forest /forest/stats /vehicles /announce (POST)
    //   websocket: /ws (and / for old clients), JSON frames, see Broadcast()
    public class WebSocketHandler : WebSocketBehavior
    {
        protected override void OnOpen()
        {
            string endpoint = Context.Headers.Get("X-Forwarded-For");
            if (endpoint.IsNullOrEmpty()) endpoint = Context.UserEndPoint.ToString();
            if (MuninConfig.DEBUG) ZLog.Log("MuninMap: new visitor connected from " + endpoint);
            var s = MapDataServer.getInstance();
            if (s != null)
            {
                Send(s.HelloFrame());
                Send("{\"t\":\"players\",\"data\":" + Players.Json + "}");
                Send("{\"t\":\"events\",\"data\":" + Events.RecentJson + ",\"initial\":true}");
            }
            base.OnOpen();
        }

        protected override void OnMessage(MessageEventArgs e)
        {
            if (e.Data == "players") Send("{\"t\":\"players\",\"data\":" + Players.Json + "}");
            base.OnMessage(e);
        }
    }

    public class MapDataServer
    {
        private static readonly Dictionary<string, string> contentTypes = new Dictionary<string, string> {
            {"html", "text/html; charset=utf-8"}, {"js", "text/javascript; charset=utf-8"}, {"mjs", "text/javascript; charset=utf-8"},
            {"css", "text/css; charset=utf-8"}, {"json", "application/json"}, {"png", "image/png"}, {"jpg", "image/jpeg"},
            {"webp", "image/webp"}, {"svg", "image/svg+xml"}, {"ico", "image/x-icon"}, {"woff", "font/woff"}, {"woff2", "font/woff2"},
            {"bin", "application/octet-stream"}, {"wasm", "application/wasm"}, {"map", "application/json"}, {"txt", "text/plain; charset=utf-8"},
            {"webmanifest", "application/manifest+json"}
        };

        private readonly System.Threading.Timer broadcastTimer;
        private readonly ConcurrentDictionary<string, byte[]> fileCache = new ConcurrentDictionary<string, byte[]>();
        private readonly HttpServer httpServer;
        private readonly string publicRoot;
        private static Dictionary<string, string> embeddedWeb;   // "js/app.js" -> resource name
        private readonly WebSocketServiceHost wsHost, wsLegacyHost;
        private static MapDataServer __instance;

        // single-image world render
        public byte[] mapImageData;
        private byte[] mapJpgCache;

        public List<string> pins = new List<string>();
        public List<ZNetPeer> players = new List<ZNetPeer>();

        private string lastPlayersJson = "";
        private volatile bool forceReload;
        private volatile int worldRev;
        private volatile bool worldChanged;

        public MapDataServer()
        {
            __instance = this;
            httpServer = new HttpServer(SERVER_PORT);
            // permessage-deflate is off unless asked for: IIS ARR and some other proxies accept the
            // handshake with it and then stall every frame
            httpServer.AddWebSocketService<WebSocketHandler>("/ws", ws => ws.IgnoreExtensions = !WEBSOCKET_COMPRESSION);
            httpServer.AddWebSocketService<WebSocketHandler>("/", ws => ws.IgnoreExtensions = !WEBSOCKET_COMPRESSION);
            httpServer.KeepClean = true;
            wsHost = httpServer.WebSocketServices["/ws"];
            wsLegacyHost = httpServer.WebSocketServices["/"];

            publicRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty, "web"));

            broadcastTimer = new System.Threading.Timer(e => { try { Broadcast(); } catch (Exception ex) { if (DEBUG) ZLog.LogWarning("MuninMap: broadcast failed: " + ex.Message); } },
                null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(PLAYER_UPDATE_INTERVAL));

            httpServer.OnGet += (sender, e) => { try { if (!Route(e, false)) ServeStatic(e); } catch (Exception ex) { Fail(e, ex); } };
            httpServer.OnPost += (sender, e) => { try { if (!Route(e, true)) NotFound(e.Response); } catch (Exception ex) { Fail(e, ex); } };
            httpServer.OnHead += (sender, e) => { try { if (!Route(e, false)) ServeStatic(e); } catch (Exception ex) { Fail(e, ex); } };
        }

        public static MapDataServer getInstance() => __instance;

        private static void Fail(HttpRequestEventArgs e, Exception ex)
        {
            ZLog.LogWarning("MuninMap: request " + e.Request.RawUrl + " failed: " + ex);
            try { e.Response.StatusCode = 500; e.Response.Close(); } catch { }
        }

        // ---------------------------------------------------------------- websocket

        public string HelloFrame()
        {
            return "{\"t\":\"hello\",\"version\":\"" + MuninMap.VERSION + "\",\"worldRev\":" + worldRev + ",\"raid\":" + WorldEvents.RaidJson + ",\"invasions\":" + WorldEvents.InvasionsJson + ",\"config\":" + MakeClientConfigJson() + "}";
        }

        private void Send(string frame)
        {
            try { wsHost.Sessions.Broadcast(frame); } catch { }
            try { wsLegacyHost.Sessions.Broadcast(frame); } catch { }
        }

        private string lastRaidJson = "null", lastInvasionKey = "";
        private static string InvasionKey(string json) => System.Text.RegularExpressions.Regex.Replace(json, ",\"left\":[0-9.]+", "");
        private void Broadcast()
        {
            if (forceReload)
            {
                forceReload = false;
                Send("{\"t\":\"reload\"}");
                return;
            }
            string pj = Players.Json;
            if (pj != lastPlayersJson)
            {
                lastPlayersJson = pj;
                Send("{\"t\":\"players\",\"data\":" + pj + "}");
            }
            string raid = WorldEvents.RaidJson;
            if (raid != lastRaidJson) { lastRaidJson = raid; Send("{\"t\":\"raid\",\"data\":" + raid + "}"); }
            string inv = WorldEvents.InvasionsJson;   // ("left" counts down: sent when the set changes, the page counts itself)
            string invKey = InvasionKey(inv);
            if (invKey != lastInvasionKey) { lastInvasionKey = invKey; Send("{\"t\":\"invasions\",\"data\":" + inv + "}"); }
            string ev = Events.DrainPendingJson();
            if (ev != null) Send("{\"t\":\"events\",\"data\":" + ev + "}");
            var tiles = TileStore.DrainNotifications();
            if (tiles != null)
            {
                var j = new JsonWriter(tiles.Count * 12 + 32);
                j.BeginObject().Prop("t", "tiles").Key("keys").BeginArray();
                foreach (var k in tiles) j.Value(k);
                j.End().Prop("status", TileStore.OnDisk + "/" + TileStore.QueueLength).End();
                Send(j.ToString());
            }
            if (worldChanged)
            {
                worldChanged = false;
                Send("{\"t\":\"world\",\"rev\":" + worldRev + ",\"stats\":" + Stats.Json + "}");
            }
        }

        public void BroadcastWorldRevision() { worldRev++; worldChanged = true; }
        public void Reload() { forceReload = true; }

        public void BroadcastPing(long id, string name, Vector3 position)
        {
            var j = new JsonWriter(128);
            j.BeginObject().Prop("t", "ping").Prop("id", id).Prop("name", name).Prop("x", position.x, 1).Prop("z", position.z, 1).End();
            Send(j.ToString());
            Events.Add("ping", name, "pinged the map", position.x, position.z);
        }

        // ---------------------------------------------------------------- pins (chat commands)

        public void AddPin(string id, string pinId, string type, string name, Vector3 position, string pinText)
        {
            lock (pins) pins.Add($"{id},{pinId},{type},{name},{Fixed(position.x)},{Fixed(position.z)},{pinText}");
            var j = new JsonWriter(160);
            j.BeginObject().Prop("t", "pin").Prop("owner", id).Prop("id", pinId).Prop("type", type).Prop("name", name)
             .Prop("x", position.x, 1).Prop("z", position.z, 1).Prop("text", pinText).End();
            Send(j.ToString());
            Events.Add("pin", name, "placed a pin" + (pinText.Length > 0 ? ": " + pinText : ""), position.x, position.z);
        }

        public void RemovePin(int idx)
        {
            string[] parts;
            lock (pins) { parts = pins[idx].Split(','); pins.RemoveAt(idx); }
            Send("{\"t\":\"rmpin\",\"id\":\"" + parts[1] + "\"}");
        }

        public string PinsJson()
        {
            var j = new JsonWriter(1024);
            j.BeginArray();
            lock (pins)
                foreach (var line in pins)
                {
                    var p = line.Split(',');
                    if (p.Length < 7) continue;
                    j.BeginObject().Prop("owner", p[0]).Prop("id", p[1]).Prop("type", p[2]).Prop("name", p[3]);
                    float.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float x);
                    float.TryParse(p[5], NumberStyles.Float, CultureInfo.InvariantCulture, out float z);
                    j.Prop("x", x, 1).Prop("z", z, 1).Prop("text", string.Join(",", p, 6, p.Length - 6)).End();
                }
            j.End();
            return j.ToString();
        }

        // pos: where it was said (shouts come with it). A shout shows on the map where it was shouted,
        // like the game's map does, for players who share their position.
        public void AddMessage(long id, int type, string name, string message, Vector3? pos = null)
        {
            string kind = type == (int)Talker.Type.Shout ? "shout" : type == (int)Talker.Type.Whisper ? "whisper" : name == "Server" ? "server" : "chat";
            // the game's own shout on every spawn: the join is already in the events
            if (kind == "shout" && (message == "I have arrived!" || message == "$text_player_arrived")) return;
            if (kind == "shout" && pos.HasValue && pos.Value != Vector3.zero && Shares(id))
            {
                var j = new JsonWriter(160);
                j.BeginObject().Prop("t", "shout").Prop("id", id).Prop("name", name).Prop("text", message)
                 .Prop("x", pos.Value.x, 1).Prop("z", pos.Value.z, 1).End();
                Send(j.ToString());
                Events.Add(kind, name, message, pos.Value.x, pos.Value.z);
                return;
            }
            Events.Add(kind, name, message);
        }

        private static bool Shares(long peerId)
        {
            if (MuninConfig.ALWAYS_VISIBLE) return true;
            try { var peer = ZNet.instance != null ? ZNet.instance.GetPeer(peerId) : null; return peer != null && peer.m_publicRefPos; }
            catch { return false; }
        }

        private static string Fixed(float f) => f.ToString("F2", CultureInfo.InvariantCulture);

        // ---------------------------------------------------------------- legacy map.png

        public void BuildMapJpg()
        {
            if (mapJpgCache != null || mapImageData == null || mapImageData.Length == 0) return;
            try
            {
                var tex = new Texture2D(TEXTURE_SIZE, TEXTURE_SIZE, TextureFormat.RGBA32, false);
                if (!ImageConv.LoadImage(tex, mapImageData)) return;
                mapJpgCache = ImageConv.EncodeToJPG(tex, 85);
                UnityEngine.Object.Destroy(tex);
            }
            catch (Exception ex) { ZLog.LogWarning("MuninMap: jpeg encode failed: " + ex.Message); }
        }

        // ---------------------------------------------------------------- lifecycle

        public void ListenAsync()
        {
            httpServer.Start();
            if (httpServer.IsListening) ZLog.Log($"MuninMap: HTTP server listening on port {SERVER_PORT}");
            else ZLog.LogError("MuninMap: HTTP server failed to start");
        }

        public void Stop()
        {
            broadcastTimer.Dispose();
            try { httpServer.Stop(); } catch { }
        }

        // ---------------------------------------------------------------- routing

        private bool Route(HttpRequestEventArgs e, bool post)
        {
            var req = e.Request; var res = e.Response;
            string path = req.Url.AbsolutePath;

            if (path.StartsWith("/tiles/", StringComparison.Ordinal)) return post ? false : ServeTile(e, path);
            if (path.StartsWith("/data/", StringComparison.Ordinal)) return post ? false : ServeData(e, path);
            if (path.StartsWith("/models/", StringComparison.Ordinal)) return post ? false : ServeModel(e, path);
            if (path.StartsWith("/icons/game/", StringComparison.Ordinal)) return post ? false : ServeGameIcon(e, path);

            switch (path)
            {
                case "/config": return Text(e, MakeClientConfigJson(), "application/json", nocache: true);
                case "/api/status":
                {
                    var j = new JsonWriter(512);
                    j.BeginObject().PropRaw("tiles", TileStore.StatusJson()).Prop("sweeps", WorldSweep.Sweeps)
                     .Prop("lastSweepSeconds", WorldSweep.LastSweepSeconds, 1).Prop("objects", WorldSweep.LastScanned)
                     .Prop("structures", Structures.Total).Prop("worldRev", worldRev).Prop("version", MuninMap.VERSION).End();
                    return Text(e, j.ToString(), "application/json", nocache: true);
                }
                case "/api/sweep":
                    if (!post) return false;
                    WorldSweep.RefreshRequested = true;
                    return Text(e, "{\"queued\":true}", "application/json", nocache: true, status: 202);
                case "/api/reexport":
                {
                    // re-export every prefab model, e.g. after textures were added
                    if (!post) return false;
                    if (!Authorized(req)) return Text(e, "{\"error\":\"forbidden\"}", "application/json", nocache: true, status: 403);
                    int n = Models.ModelStore.ReexportAll();
                    return Text(e, "{\"queued\":" + n + "}", "application/json", nocache: true, status: 202);
                }
                case "/api/rerender":
                {
                    if (!post) return false;
                    if (!Authorized(req)) return Text(e, "{\"error\":\"forbidden\"}", "application/json", nocache: true, status: 403);
                    int.TryParse(req.QueryString["zoom"] ?? "0", out int z);
                    int n = TileStore.Rerender(z);
                    return Text(e, "{\"queued\":" + n + "}", "application/json", nocache: true, status: 202);
                }
                case "/api/serverlog":
                {
                    // the game's console lines (Live/ServerLog): admins only, they name accounts and addresses
                    if (!Authorized(req)) return Text(e, "{\"error\":\"forbidden\"}", "application/json", nocache: true, status: 403);
                    long.TryParse(req.QueryString["after"] ?? "-1", out long after);
                    int.TryParse(req.QueryString["limit"] ?? "500", out int limit);
                    return Text(e, ServerLog.Json(after, Math.Max(1, Math.Min(2000, limit))), "application/json", nocache: true);
                }
                case "/api/reload":
                {
                    // pick up new files in web/ without restarting the game: forget the cached copies
                    // and tell every open browser to refresh. The DLL itself still needs a restart.
                    if (!post) return false;
                    if (!Authorized(req)) return Text(e, "{\"error\":\"forbidden\"}", "application/json", nocache: true, status: 403);
                    int n = fileCache.Count;
                    fileCache.Clear(); stampedIndex = null; stampedFrom = null;
                    Reload();
                    ZLog.Log("MuninMap: web files reloaded (" + n + " cached files dropped), browsers told to refresh");
                    return Text(e, "{\"dropped\":" + n + ",\"browsers\":" + (wsHost.Sessions.Count + wsLegacyHost.Sessions.Count) + "}", "application/json", nocache: true);
                }

                // ---- simple endpoints (single-image map, plain lists)
                case "/map":
                    if (!MuninConfig.LEGACY_MAP) return Text(e, "disabled (legacy_map = false)", "text/plain", status: 404);
                    if (mapImageData == null) return Text(e, "not built", "text/plain", status: 503);
                    return Bytes(e, mapImageData, "application/octet-stream", "public, max-age=604800, immutable");
                case "/map.jpg":
                    if (!MuninConfig.LEGACY_MAP) return Text(e, "disabled (legacy_map = false)", "text/plain", status: 404);
                    if (mapJpgCache == null) return Text(e, "not built", "text/plain", status: 503);
                    return Bytes(e, mapJpgCache, "image/jpeg", "public, max-age=604800, immutable");
                case "/fog": return Bytes(e, Fog.Png(), "image/png", "no-cache");
                case "/players": return Text(e, Players.Json, "application/json", nocache: true);
                case "/messages": return Text(e, Events.RecentJson, "application/json", nocache: true);
                case "/pins":
                {
                    string text; lock (pins) text = string.Join("\n", pins);
                    return Text(e, text, "text/csv", nocache: true);
                }
                case "/structures": return Bytes(e, StructureMap.GetPng(), "image/png", "no-cache");
                case "/structures/stats": return Text(e, Structures.StatsJson, "application/json", nocache: true);
                case "/structures/refresh":
                    WorldSweep.RefreshRequested = true;
                    return Text(e, "{\"queued\":true}", "application/json", nocache: true, status: 202);
                case "/forest": return Bytes(e, ForestMap.GetPng(), "image/png", "no-cache");
                case "/forest/stats": return Text(e, ForestMap.GetStats(), "application/json", nocache: true);
                case "/vehicles": return Text(e, Vehicles.GetJson(), "application/json", nocache: true);
                case "/api/pin":
                {
                    // place a pin from the web page. Body: JSON {x,z,type,text,name,client}
                    if (!post) return false;
                    if (!WEB_PINS && !Authorized(req)) return Text(e, "{\"error\":\"web pins are off\"}", "application/json", nocache: true, status: 403);
                    string body;
                    using (var sr = new StreamReader(req.InputStream, Encoding.UTF8)) body = sr.ReadToEnd();
                    Dictionary<string, object> f;
                    try { f = JsonParser.Parse(body) as Dictionary<string, object>; } catch { f = null; }
                    if (f == null || !f.TryGetValue("x", out object xo) || !f.TryGetValue("z", out object zo)) return Text(e, "{\"error\":\"need x and z\"}", "application/json", nocache: true, status: 400);
                    float x = Convert.ToSingle(xo, CultureInfo.InvariantCulture), z = Convert.ToSingle(zo, CultureInfo.InvariantCulture);
                    float half = Tiles.TileMath.WORLD_SIZE / 2f;
                    if (float.IsNaN(x) || float.IsNaN(z) || Mathf.Abs(x) > half || Mathf.Abs(z) > half) return Text(e, "{\"error\":\"off the map\"}", "application/json", nocache: true, status: 400);
                    string owner = WebOwner(req, f);
                    if (!PinRateOk(owner)) return Text(e, "{\"error\":\"slow down\"}", "application/json", nocache: true, status: 429);
                    string name = MuninMap.CleanPinText(f.TryGetValue("name", out object no) ? no as string : null, 16);
                    if (name.Length == 0) name = "web";
                    string id = MuninMap.PlacePin(owner, f.TryGetValue("type", out object to) ? to as string : "dot", name, new Vector3(x, 0, z), f.TryGetValue("text", out object txo) ? txo as string : "");
                    return Text(e, "{\"id\":\"" + id + "\",\"owner\":\"" + owner + "\"}", "application/json", nocache: true);
                }
                case "/api/base":
                {
                    // rename or hide an auto-detected base. Body: JSON {x,z,label} or {x,z,hidden:true};
                    // {x,z} alone puts it back the way it was. Token holder always may; visitors when web_edit_bases is on
                    if (!post) return false;
                    if (!WEB_EDIT_BASES && !Authorized(req)) return Text(e, "{\"error\":\"base editing is off\"}", "application/json", nocache: true, status: 403);
                    string body;
                    using (var sr = new StreamReader(req.InputStream, Encoding.UTF8)) body = sr.ReadToEnd();
                    Dictionary<string, object> f;
                    try { f = JsonParser.Parse(body) as Dictionary<string, object>; } catch { f = null; }
                    if (f == null || !f.TryGetValue("x", out object xo) || !f.TryGetValue("z", out object zo)) return Text(e, "{\"error\":\"need x and z\"}", "application/json", nocache: true, status: 400);
                    float x = Convert.ToSingle(xo, CultureInfo.InvariantCulture), z = Convert.ToSingle(zo, CultureInfo.InvariantCulture);
                    string label = f.TryGetValue("label", out object lo) ? MuninMap.CleanPinText(lo as string, 24) : null;
                    if (label != null && label.Length == 0) label = null;
                    bool hidden = f.TryGetValue("hidden", out object ho) && ho is bool hb && hb;
                    if (!PinRateOk(WebOwner(req, f))) return Text(e, "{\"error\":\"slow down\"}", "application/json", nocache: true, status: 429);
                    Markers.SetBase(x, z, label, hidden);
                    BroadcastWorldRevision();
                    return Text(e, "{\"ok\":true}", "application/json", nocache: true);
                }
                case "/api/bases/reset":
                {
                    // forget every rename and hide (token)
                    if (!post) return false;
                    if (!Authorized(req)) return Text(e, "{\"error\":\"forbidden\"}", "application/json", nocache: true, status: 403);
                    int n = Markers.ClearOverrides();
                    BroadcastWorldRevision();
                    return Text(e, "{\"cleared\":" + n + "}", "application/json", nocache: true);
                }
                case "/api/unpin":
                {
                    // remove one pin: ?id=<pin id>. Only its owner (same browser) or the token holder
                    if (!post) return false;
                    string id = req.QueryString["id"] ?? "";
                    if (id.Length == 0 || id.Contains(",")) return Text(e, "{\"error\":\"need id\"}", "application/json", nocache: true, status: 400);
                    string owner = Authorized(req) ? "" : WebOwner(req, null);
                    if (!WEB_PINS && owner.Length > 0) return Text(e, "{\"error\":\"web pins are off\"}", "application/json", nocache: true, status: 403);
                    bool ok = MuninMap.DeletePinById(owner, id);
                    return Text(e, ok ? "{\"removed\":true}" : "{\"error\":\"not yours\"}", "application/json", nocache: true, status: ok ? 200 : 404);
                }
                case "/announce":
                {
                    if (!post) return false;
                    if (!Authorized(req)) return Text(e, "{\"error\":\"forbidden\"}", "application/json", nocache: true, status: 403);
                    string body;
                    using (var sr = new StreamReader(req.InputStream, Encoding.UTF8)) body = sr.ReadToEnd();
                    body = (body ?? "").Trim();
                    if (body.Length == 0) return Text(e, "{\"error\":\"empty\"}", "application/json", nocache: true, status: 400);
                    Announce.Enqueue(body);      // Announce.Send posts it to the event feed once delivered
                    return Text(e, "{\"queued\":true}", "application/json", nocache: true, status: 202);
                }
            }
            return false;
        }

        // a browser identifies itself with a random id it made up and keeps (X-Munin-Client header
        // or "client" in the body); pins it placed can be removed from that browser only
        private static readonly Regex clientIdFilter = new Regex("[^A-Za-z0-9_-]", RegexOptions.Compiled);
        private static readonly Dictionary<string, float> pinLast = new Dictionary<string, float>();

        private static string WebOwner(HttpListenerRequest req, Dictionary<string, object> body)
        {
            string c = req.Headers["X-Munin-Client"];
            if (string.IsNullOrEmpty(c) && body != null && body.TryGetValue("client", out object co)) c = co as string;
            c = clientIdFilter.Replace(c ?? "", "");
            if (c.Length > 40) c = c.Substring(0, 40);
            if (c.Length == 0) c = "anon";
            return "web:" + c;
        }

        private static bool PinRateOk(string owner)
        {
            float now = (float)(DateTime.UtcNow - new DateTime(2020, 1, 1)).TotalSeconds;
            lock (pinLast)
            {
                if (pinLast.TryGetValue(owner, out float last) && now - last < 2f) return false;
                pinLast[owner] = now;
                if (pinLast.Count > 512) pinLast.Clear();
            }
            return true;
        }

        private static bool Authorized(HttpListenerRequest req)
        {
            string want = Announce.Token;
            string got = req.Headers["X-Announce-Token"] ?? req.Headers["X-Munin-Token"] ?? "";
            return want != null && got == want;
        }

        // /tiles/{layer}/{z}/{x}/{y}.png
        private bool ServeTile(HttpRequestEventArgs e, string path)
        {
            var res = e.Response;
            string[] p = path.Split('/');
            if (p.Length != 6 || !p[5].EndsWith(".png", StringComparison.Ordinal)) { NotFound(res); return true; }
            string layer = p[2];
            if (layer != "map" && layer != "height" && layer != "veg") { NotFound(res); return true; }
            if (!int.TryParse(p[3], out int z) || !int.TryParse(p[4], out int x) || !int.TryParse(p[5].Substring(0, p[5].Length - 4), out int y))
            { NotFound(res); return true; }

            byte[] data = TileStore.Get(layer, z, x, y, out string etag);
            // Tiles hardly ever change, so the browser may reuse one for 10 minutes without asking:
            // zooming back over seen ground is instant. After that it still shows its copy at once
            // and checks for a newer one in the background (stale-while-revalidate), so a return
            // visit days later paints from cache too; a service worker can't do this here, since
            // browsers only run them over HTTPS. A re-render reaches open pages over the websocket
            // and they reload that tile with a fresh ?r=.
            if (data == null)
            {
                res.Headers.Add("X-Munin-Tile", "pending");
                res.Headers.Add(HttpResponseHeader.CacheControl, "no-store");
                res.StatusCode = 404;
                res.Close();
                return true;
            }
            const string tileCache = "public, max-age=600, stale-while-revalidate=2592000";
            if (!WebP.IsWebp(data)) return Bytes(e, data, "image/png", tileCache, etag: etag);
            // stored as lossless WebP; the URL still says .png, so a browser that doesn't take WebP
            // (it says so in Accept) gets the same pixels as a PNG made on the spot
            res.Headers.Add(HttpResponseHeader.Vary, "Accept");
            if ((e.Request.Headers["Accept"] ?? "").Contains("image/webp")) return Bytes(e, data, "image/webp", tileCache, etag: etag);
            byte[] png = WebP.ToPng(data);
            if (png == null) { res.StatusCode = 500; res.Close(); return true; }
            return Bytes(e, png, "image/png", tileCache, etag: "\"p" + etag.Substring(1));
        }

        // /data/...
        private bool ServeData(HttpRequestEventArgs e, string path)
        {
            var res = e.Response;
            string rest = path.Substring("/data/".Length);
            switch (rest)
            {
                case "players.json": return Text(e, Players.Json, "application/json", nocache: true);
                case "stats.json": return Text(e, Stats.Json, "application/json", nocache: true);
                case "events.json": return Text(e, Events.RecentJson, "application/json", nocache: true);
                case "events/older.json":
                {
                    string before = e.Request.QueryString["before"] ?? "9999";
                    int.TryParse(e.Request.QueryString["limit"] ?? "100", out int limit);
                    return Text(e, Events.OlderJson(before, Math.Max(1, Math.Min(500, limit))), "application/json", nocache: true);
                }
                case "pins.json": return Text(e, PinsJson(), "application/json", nocache: true);
                case "markers.json": return Text(e, Markers.Json, "application/json", nocache: true);
                case "fog.png": return Bytes(e, Fog.Png(), "image/png", "no-cache");
                case "structures/index.json": return Text(e, Structures.IndexJson, "application/json", nocache: true);
                case "ruins/index.json": return Text(e, Ruins.IndexJson, "application/json", nocache: true);
                case "objects/index.json": return Text(e, WorldObjects.IndexJson, "application/json", nocache: true);
                case "prefabs.json": return Text(e, Models.ModelStore.PrefabsJson, "application/json", nocache: true);
                case "icons.json": return Text(e, MapIcons.ManifestJson, "application/json", nocache: true);
                case "biomes.json": return Text(e, BiomeMap.LabelsJson(), "application/json", nocache: true);
                case "biomes.grid":
                {
                    var g = BiomeMap.GridFor();   // explored ground only (unless reveal_all)
                    if (g == null) { NotFound(res); return true; }
                    return Bytes(e, g, "application/octet-stream", "no-cache", compressible: true);
                }
                case "mist.png":
                {
                    var png = BiomeMap.MistPngFor();   // explored ground only (unless reveal_all)
                    if (png == null) { NotFound(res); return true; }
                    return Bytes(e, png, "image/png", "no-cache", etag: ETagOf(png));
                }
                case "mistclear.json": return Text(e, Demisters.Json(), "application/json", nocache: true);
                case "biomes.png":
                {
                    var png = BiomeMap.PngFor();   // explored ground only (unless reveal_all)
                    if (png == null) { NotFound(res); return true; }
                    return Bytes(e, png, "image/png", "no-cache", etag: ETagOf(png));
                }
            }
            if (rest.StartsWith("objects/", StringComparison.Ordinal) && rest.EndsWith(".bin", StringComparison.Ordinal))
            {
                if (!ParseChunk(rest.Substring(8, rest.Length - 12), out int cx, out int cz)) { NotFound(res); return true; }
                float minX = TileMath.ChunkMin(cx), minZ = TileMath.ChunkMin(cz);
                if (!REVEAL_ALL && !Fog.AnyExplored(minX, minZ, minX + TileMath.CHUNK_SIZE, minZ + TileMath.CHUNK_SIZE)) { NotFound(res); return true; }
                byte[] data = WorldObjects.ChunkBytes(cx, cz);
                if (data == null) { NotFound(res); return true; }
                return Bytes(e, data, "application/octet-stream", "no-cache", compressible: true);
            }
            if ((rest.StartsWith("structures/r/", StringComparison.Ordinal) || rest.StartsWith("ruins/r/", StringComparison.Ordinal)) && rest.EndsWith(".json", StringComparison.Ordinal))
            {
                bool st = rest.StartsWith("structures/", StringComparison.Ordinal);
                string id = rest.Substring(st ? "structures/r/".Length : "ruins/r/".Length);
                if (!Regions.Parse(id.Substring(0, id.Length - 5), out int rx, out int rz)) { NotFound(res); return true; }
                string json = st ? Structures.RegionJson(rx, rz, out int rev) : Ruins.RegionJson(rx, rz, out rev);
                if (json == null) { NotFound(res); return true; }
                return ChunkText(e, json, rev);
            }
            if (rest.StartsWith("structures/", StringComparison.Ordinal) && rest.EndsWith(".json", StringComparison.Ordinal))
            {
                if (!ParseChunk(rest.Substring("structures/".Length, rest.Length - "structures/".Length - 5), out int cx, out int cz)) { NotFound(res); return true; }
                string json = Structures.ChunkJson(cx, cz, out int rev);
                if (json == null) json = "{\"cx\":" + cx + ",\"cz\":" + cz + ",\"rev\":0,\"count\":0,\"pieces\":[],\"prefabs\":[]}";
                return ChunkText(e, json, rev);
            }
            if (rest.StartsWith("ruins/", StringComparison.Ordinal) && rest.EndsWith(".json", StringComparison.Ordinal))
            {
                if (!ParseChunk(rest.Substring("ruins/".Length, rest.Length - "ruins/".Length - 5), out int cx, out int cz)) { NotFound(res); return true; }
                string json = Ruins.ChunkJson(cx, cz, out int rev);
                if (json == null) { NotFound(res); return true; }
                return ChunkText(e, json, rev);
            }
            if (rest == "veg/index.json") return Text(e, Vegetation.IndexJson(), "application/json", nocache: true);
            // r3: the VGR3 format. Region URLs are cached for good, so a new format needs a new path,
            // or browsers keep handing the new code the old bytes.
            if (rest.StartsWith("veg/r3/", StringComparison.Ordinal) && rest.EndsWith(".bin", StringComparison.Ordinal))
            {
                string id = rest.Substring("veg/r3/".Length);
                if (!Regions.Parse(id.Substring(0, id.Length - 4), out int rx, out int rz, Vegetation.REGION)) { NotFound(res); return true; }
                byte[] bin = Vegetation.RegionBin(rx, rz, out int rev);
                if (bin == null) { NotFound(res); return true; }
                bool exact = e.Request.QueryString["h"] == rev.ToString(CultureInfo.InvariantCulture);
                return Bytes(e, bin, "application/octet-stream", exact ? "public, max-age=31536000, immutable" : "no-cache", compressible: true);
            }
            if (rest.StartsWith("veg/", StringComparison.Ordinal) && rest.EndsWith(".bin", StringComparison.Ordinal))
            {
                if (!ParseChunk(rest.Substring(4, rest.Length - 8), out int cx, out int cz)) { NotFound(res); return true; }
                float minX = TileMath.ChunkMin(cx), minZ = TileMath.ChunkMin(cz);
                if (!REVEAL_ALL && !Fog.AnyExplored(minX, minZ, minX + TileMath.CHUNK_SIZE, minZ + TileMath.CHUNK_SIZE)) { NotFound(res); return true; }
                return Bytes(e, Vegetation.Chunk(cx, cz), "application/octet-stream", "public, max-age=600", compressible: true);   // like tiles: reused without asking for 10 min
            }
            NotFound(res);
            return true;
        }

        // /models/{file}.glb | .png  from map_data/models (shared by all worlds)
        private bool ServeModel(HttpRequestEventArgs e, string path)
        {
            var res = e.Response;
            string name = path.Substring("/models/".Length);
            if (name.Length == 0 || name.Contains("/") || name.Contains("..") || name.Contains("\\")) { NotFound(res); return true; }
            string root = Models.ModelStore.Root;
            if (root == null) { NotFound(res); return true; }
            string full = Path.Combine(root, name);
            if (!File.Exists(full)) { NotFound(res); return true; }
            byte[] data;
            try { data = File.ReadAllBytes(full); } catch { NotFound(res); return true; }
            bool glb = name.EndsWith(".glb", StringComparison.Ordinal);
            return Bytes(e, data, glb ? "model/gltf-binary" : "image/png", "no-cache", compressible: glb);
        }

        // /icons/game/{sprite}.png: the game's own map icons, cut from its UI atlas (World/MapIcons)
        private bool ServeGameIcon(HttpRequestEventArgs e, string path)
        {
            string name = path.Substring("/icons/game/".Length);
            if (MapIcons.Dir == null || !name.EndsWith(".png", StringComparison.Ordinal) || name.Contains("/") || name.Contains("..") || name.Contains("\\")) { NotFound(e.Response); return true; }
            string full = Path.Combine(MapIcons.Dir, name);
            if (!File.Exists(full)) { NotFound(e.Response); return true; }
            byte[] data;
            try { data = File.ReadAllBytes(full); } catch { NotFound(e.Response); return true; }
            return Bytes(e, data, "image/png", "public, max-age=86400", etag: ETagOf(data));
        }

        private static bool ParseChunk(string s, out int cx, out int cz)
        {
            cx = cz = 0;
            int us = s.IndexOf('_');
            if (us < 0) return false;
            return int.TryParse(s.Substring(0, us), out cx) && int.TryParse(s.Substring(us + 1), out cz)
                && cx >= 0 && cz >= 0 && cx < TileMath.ChunksPerSide && cz < TileMath.ChunksPerSide;
        }

        // ---------------------------------------------------------------- static files

        private void ServeStatic(HttpRequestEventArgs e)
        {
            var req = e.Request; var res = e.Response;
            string path = req.Url.AbsolutePath;
            if (path == "/") path = "/index.html";
            string rel = path.TrimStart('/');
            if (rel.Length == 0 || rel.Contains("..") || rel.Contains("\\") || rel.Contains(":")) { NotFound(res); return; }
            string ext = Path.GetExtension(rel).TrimStart('.').ToLowerInvariant();
            if (!contentTypes.TryGetValue(ext, out string ctype)) { NotFound(res); return; }

            byte[] data = ReadWebFile(rel);
            if (data == null) { NotFound(res); return; }
            if (rel == "index.html") data = StampIndex(data);
            // vendored libraries never change between mod versions. Everything else carries a content
            // hash in its URL (see StampIndex), so it can be cached hard too: a new file is a new URL,
            // and no proxy in between (Cloudflare, a browser) can hand out a stale one.
            string cache = rel.StartsWith("vendor/", StringComparison.Ordinal) || req.QueryString["v"] != null ? "public, max-age=2592000, immutable" : "no-cache";
            Bytes(e, data, ctype, cache, etag: ETagOf(data), compressible: ext == "html" || ext == "js" || ext == "mjs" || ext == "css" || ext == "json" || ext == "svg");
        }

        // a web file's bytes: from disk next to the DLL, else from the copy inside the DLL; cached
        private byte[] ReadWebFile(string rel)
        {
            if (fileCache.TryGetValue(rel, out byte[] data)) return data;
            string full = Path.GetFullPath(Path.Combine(publicRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (full.StartsWith(publicRoot, StringComparison.Ordinal) && File.Exists(full))
            {
                try { data = File.ReadAllBytes(full); }
                catch (Exception ex) { ZLog.LogError("MuninMap: failed to read " + rel + ": " + ex.Message); return null; }
            }
            else
            {
                data = EmbeddedWebFile(rel);     // no web folder on disk: the copy built into the DLL
                if (data == null) return null;
            }
            if (CACHE_SERVER_FILES) fileCache[rel] = data;
            return data;
        }

        // Every script and stylesheet the page loads gets ?v=<hash of its bytes> in its URL, through
        // the import map for module imports and directly for the script/link tags. A changed file is
        // a new URL, so browsers and CDNs can never serve yesterday's app.js with today's view3d.js.
        private static string JsonString(string v)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in v ?? "") { if (c == '"' || c == '\\') sb.Append('\\').Append(c); else if (c < 32 || c == '<' || c == '>') sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c); }
            return sb.Append('"').ToString();
        }

        private static readonly Regex staticImport = new Regex(@"^\s*import\s+(?:[^'""();]*?\sfrom\s*)?['""](\.{1,2}/[^'""]+)['""]", RegexOptions.Multiline | RegexOptions.Compiled);
        private byte[] stampedIndex; private byte[] stampedFrom;
        private byte[] StampIndex(byte[] index)
        {
            if (stampedIndex != null && ReferenceEquals(stampedFrom, index)) return stampedIndex;
            string html = Encoding.UTF8.GetString(index);
            var sb = new StringBuilder();
            foreach (string rel in WebFiles())
            {
                if (!rel.EndsWith(".js", StringComparison.Ordinal) || rel.StartsWith("vendor/", StringComparison.Ordinal)) continue;
                byte[] d = ReadWebFile(rel); if (d == null) continue;
                sb.Append(", \"./").Append(rel).Append("\": \"./").Append(rel).Append("?v=").Append(Fnv(d).ToString("x")).Append('"');
            }
            html = html.Replace("\"three/addons/\": \"./vendor/three/addons/\"", "\"three/addons/\": \"./vendor/three/addons/\"" + sb);
            // Every module app.js imports, directly or not, as a modulepreload: the browser fetches
            // them all at once instead of finding each only when the one importing it has arrived
            // (three round trips deep, ~0.3 s on a home connection). Dynamic import()s stay lazy.
            var preload = new StringBuilder();
            var seen = new HashSet<string>();
            var todo = new Stack<string>();
            todo.Push("js/app.js");
            while (todo.Count > 0)
            {
                string rel = todo.Pop();
                if (!seen.Add(rel)) continue;
                byte[] d = ReadWebFile(rel); if (d == null) continue;
                if (rel != "js/app.js") preload.Append("<link rel=\"modulepreload\" href=\"").Append(rel).Append("?v=").Append(Fnv(d).ToString("x")).Append("\">\n");
                string dir = rel.Contains("/") ? rel.Substring(0, rel.LastIndexOf('/')) : "";
                foreach (Match m in staticImport.Matches(Encoding.UTF8.GetString(d)))
                {
                    var parts = new List<string>(dir.Length > 0 ? dir.Split('/') : new string[0]);
                    foreach (string seg in m.Groups[1].Value.Split('/'))
                        if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
                        else if (seg != ".") parts.Add(seg);
                    todo.Push(string.Join("/", parts));
                }
            }
            html = html.Replace("</head>", preload + "</head>");
            html = Regex.Replace(html, "(src|href)=\"((?:js|css|icons)/[^\"?]+)\"", m =>
            {
                byte[] d = ReadWebFile(m.Groups[2].Value);
                return d == null ? m.Value : m.Groups[1].Value + "=\"" + m.Groups[2].Value + "?v=" + Fnv(d).ToString("x") + "\"";
            });
            // the world, so tile URLs can carry it: another world's tiles have the same paths and a
            // browser would show them from its cache after the server switched worlds
            string world = (MuninMap.worldDataPath != null ? Path.GetFileName(MuninMap.worldDataPath) : "") ?? "";
            html = html.Replace("</head>", "<script>window.MUNIN_WORLD=" + JsonString(world) + ";</script></head>");
            stampedFrom = index; stampedIndex = Encoding.UTF8.GetBytes(html);
            return stampedIndex;
        }

        // relative paths of every web file, from disk when the folder exists, else from the DLL
        private IEnumerable<string> WebFiles()
        {
            if (Directory.Exists(publicRoot))
            {
                foreach (string f in Directory.GetFiles(publicRoot, "*", SearchOption.AllDirectories))
                    yield return f.Substring(publicRoot.Length).TrimStart(Path.DirectorySeparatorChar, '/').Replace('\\', '/');
            }
            else
            {
                EmbeddedWebFile("index.html");
                if (embeddedWeb != null) foreach (var k in embeddedWeb.Keys) yield return k;
            }
        }

        // the web app is also compiled into the DLL (see MuninMap.csproj) so a missing web folder
        // is not fatal; the first request logs which copy is in use
        private byte[] EmbeddedWebFile(string rel)
        {
            var asm = Assembly.GetExecutingAssembly();
            if (embeddedWeb == null)
            {
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string name in asm.GetManifestResourceNames())
                    if (name.StartsWith("web/", StringComparison.Ordinal) || name.StartsWith("web\\", StringComparison.Ordinal)) map[name.Substring(4).Replace('\\', '/')] = name;
                embeddedWeb = map;
                if (!Directory.Exists(publicRoot)) ZLog.LogWarning("MuninMap: no web folder next to MuninMap.dll, serving the copy built into the DLL (" + map.Count + " files)");
            }
            if (!embeddedWeb.TryGetValue(rel, out string resName)) return null;
            using (var st = asm.GetManifestResourceStream(resName))
            {
                if (st == null) return null;
                using (var ms = new MemoryStream()) { st.CopyTo(ms); return ms.ToArray(); }
            }
        }

        private static uint Fnv(byte[] d)
        {
            uint h = 2166136261u;
            int step = Math.Max(1, d.Length / 4096);
            for (int i = 0; i < d.Length; i += step) { h ^= d[i]; h *= 16777619u; }
            return h;
        }

        // ---------------------------------------------------------------- response helpers

        private static bool Text(HttpRequestEventArgs e, string text, string ctype, bool nocache = false, int status = 200)
        {
            return Bytes(e, Encoding.UTF8.GetBytes(text ?? ""), ctype, nocache ? "no-cache" : null, compressible: true, status: status);
        }

        // A structures/ruins chunk. The page asks for data/.../cx_cz.json?h=<rev from the index>, and
        // rev is a hash of the chunk's content, so when it still matches, that URL can never mean other
        // bytes: the browser keeps it for good and a return visit costs no request at all.
        private static bool ChunkText(HttpRequestEventArgs e, string json, int rev)
        {
            bool exact = e.Request.QueryString["h"] == rev.ToString(CultureInfo.InvariantCulture);
            return Bytes(e, Encoding.UTF8.GetBytes(json), "application/json", exact ? "public, max-age=31536000, immutable" : "no-cache", compressible: true);
        }

        private static string ETagOf(byte[] data) => "\"" + data.Length.ToString("x") + "-" + TileStore.Fnv1a(data).ToString("x") + "\"";

        // Every 200 that the browser must revalidate (no-cache) carries an ETag, so asking again for
        // something unchanged (fog every 20 s, indexes, markers, tiles) costs a 304 with no body.
        private static bool Bytes(HttpRequestEventArgs e, byte[] data, string ctype, string cache, bool compressible = false, int status = 200, string etag = null)
        {
            var res = e.Response;
            if (etag == null && status == 200 && cache == "no-cache") etag = ETagOf(data);
            if (cache != null) res.Headers.Add(HttpResponseHeader.CacheControl, cache);
            res.Headers.Add("Access-Control-Allow-Origin", "*");
            if (etag != null)
            {
                res.Headers.Add("ETag", etag);
                if (e.Request.Headers["If-None-Match"] == etag)
                {
                    res.StatusCode = 304;
                    res.Close();
                    return true;
                }
            }
            res.ContentType = ctype;
            res.StatusCode = status;
            if (compressible && data.Length > 200)
            {
                string ae = e.Request.Headers["Accept-Encoding"] ?? "";
                if (ae.Contains("gzip"))
                {
                    using (var ms = new MemoryStream(data.Length / 3 + 64))
                    {
                        using (var gz = new GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, true)) gz.Write(data, 0, data.Length);
                        data = ms.ToArray();
                    }
                    res.Headers.Add(HttpResponseHeader.ContentEncoding, "gzip");
                    res.Headers.Add(HttpResponseHeader.Vary, "Accept-Encoding");
                }
            }
            res.ContentLength64 = data.Length;
            if (e.Request.HttpMethod == "HEAD") { res.Close(); return true; }
            res.Close(data, true);
            return true;
        }

        private static void NotFound(HttpListenerResponse res)
        {
            res.StatusCode = 404;
            res.Close();
        }
    }
}
