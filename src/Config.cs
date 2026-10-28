using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace MuninMap
{
    internal static class MuninConfig
    {
        public static int TEXTURE_SIZE = 2048;
        public static int PIXEL_SIZE = 12;
        public static float EXPLORE_RADIUS = 110f;   // a little more than the game's 100 m
        public static bool REVEAL_VISITED = true;
        public static int REVEAL_VISITED_MARGIN = 4;
        public static float UPDATE_FOG_TEXTURE_INTERVAL = 2f;
        public static float SAVE_FOG_TEXTURE_INTERVAL = 30f;
        public static int MAX_PINS_PER_USER = 50;
        public static bool WEB_PINS = true;
        public static bool WEB_PINGS = true;
        public static bool WEB_EDIT_BASES = true;
        public static int BASE_MIN_PIECES = 30;
        public static int BASE_MIN_PER_CELL = 6;
        public static bool WEBSOCKET_COMPRESSION = false;
        public static bool TILE_WEBP = true;
        public static int MAX_MESSAGES = 100;
        public static bool ALWAYS_MAP = true;
        public static bool ALWAYS_VISIBLE = false;
        public static bool DEBUG = false;
        public static bool PERF_REPORT = false;   // log what Munin takes of the game's thread, every 5 minutes
        public static bool TEST = false;

        public static int SERVER_PORT = 3000;
        public static float PLAYER_UPDATE_INTERVAL = 1f;
        public static bool CACHE_SERVER_FILES = true;

        public static string WORLD_NAME = "";
        public static Vector3 WORLD_START_POS = Vector3.zero;
        public static int DEFAULT_ZOOM = 100;

        public static bool SHOW_VEHICLES = true;

        public static string ANNOUNCE_NAME = "Server";
        public static string DISCORD_WEBHOOK = "";
        public static string DISCORD_INVITE_URL = "";
        public static bool DISCORD_WORLD_EVENTS = true;

        public static string URL = "";
        public static string MAP_TITLE = "";

        // tile renderer
        public static int RENDER_THREADS = 1;
        public static int PRERENDER_ZOOM = 5;
        public static int MAX_RENDER_ZOOM = 7;
        public static int HEIGHT_MAX_ZOOM = 7;
        public static int MAIN_THREAD_ROWS_PER_FRAME = 24;

        // world sweep
        public static float SWEEP_INTERVAL = 120f;
        public static float FIRST_SWEEP_DELAY = 5f;
        public static float SECOND_SWEEP_DELAY = 30f;
        public static int SWEEP_ZDOS_PER_FRAME = 3000;
        public static float SWEEP_FRAME_MS = 4f;

        // markers, stats, privacy
        public static bool SHOW_LAST_SEEN_POSITION = false;
        // characters never shown on the map, in the stats or the event feed (admins, alts)
        public static HashSet<string> HIDDEN_PLAYERS = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public static bool IsHidden(string name) => !string.IsNullOrEmpty(name) && HIDDEN_PLAYERS.Contains(name.Trim());
        public static bool EVENT_LOG = true;
        public static float STATS_SAVE_INTERVAL = 60f;
        public static bool ENABLE_3D = true;
        public static bool SHOW_GITHUB_LINK = true;
        public static bool CHECK_UPDATES = true;
        // height tiles only feed the 3D view and the export: with 3D off nothing renders them
        public static int HeightMaxZoom => HEIGHT_MAX_ZOOM;   // the 3D view and the 2D contour lines both use them
        public static bool LEGACY_MAP = true;
        public static bool REVEAL_ALL = false;

        // 3.1: model export for the 3D view
        public static bool EXPORT_MODELS = true;
        public static string OBJECT_CATEGORIES = "piece,other,rock,bush,tree";
        public static bool USE_TEXTURES = true;
        public static bool EXTRACT_MESHES = true;
        public static int TEXTURE_MAX_SIZE = 512;
        public static int MODEL_EXPORT_MS_PER_FRAME = 6;

        public static void ReadConfigFile(ConfigFile config)
        {
            TEXTURE_SIZE = config.Bind("Texture", "texture_size",
                MuninConfig.TEXTURE_SIZE,
                "How large is the map texture? Probably dont change this.").Value;

            PIXEL_SIZE = config.Bind("Texture", "pixel_size",
                MuninConfig.PIXEL_SIZE,
                "How many in game units does a map pixel represent? Probably dont change this.").Value;

            EXPLORE_RADIUS = config.Bind<float>("Texture", "explore_radius",
                MuninConfig.EXPLORE_RADIUS,
                "Metres of fog cleared around each player as they explore. The in-game map uses 100; the default 110 is a little more so trails do not look tighter than people remember.").Value;

            REVEAL_VISITED = config.Bind("Texture", "reveal_visited",
                MuninConfig.REVEAL_VISITED,
                "Lift the fog everywhere players have already been, including before the mod was installed. "
                + "The world save remembers which 64 m zones the game generated; those only exist where someone "
                + "stood nearby. The in-game map itself lives in each player's character file, which the server "
                + "never sees, so this is the closest thing to it. Runs at start and after each world walk.").Value;

            REVEAL_VISITED_MARGIN = config.Bind("Texture", "reveal_visited_margin",
                MuninConfig.REVEAL_VISITED_MARGIN,
                new BepInEx.Configuration.ConfigDescription(
                "How tightly reveal_visited follows where players went. The game builds zones about 5 (320 m) out "
                + "from a player; a zone counts when every zone within this many of it was built too. 4 (default) "
                + "matched maps players recorded to cartography tables best: it uncovers about 99% of what they "
                + "had explored and little else. 5 = tighter (misses some), 3 or lower = wider (0 = everything the "
                + "game built, about 3x too much). Only adds fog-free ground: to tighten an existing map, stop the "
                + "server and delete map_data/<world>/fog.png.",
                new BepInEx.Configuration.AcceptableValueRange<int>(0, 5))).Value;

            UPDATE_FOG_TEXTURE_INTERVAL = config.Bind<float>("Interval", "update_fog_texture_interval",
                MuninConfig.UPDATE_FOG_TEXTURE_INTERVAL,
                "How often do we update the fog texture on the server in seconds.").Value;

            SAVE_FOG_TEXTURE_INTERVAL = config.Bind<float>("Interval", "save_fog_texture_interval",
                MuninConfig.SAVE_FOG_TEXTURE_INTERVAL,
                "How often do we save the fog texture in seconds.").Value;

            MAX_PINS_PER_USER = config.Bind("User", "max_pins_per_user",
                MuninConfig.MAX_PINS_PER_USER,
                "How many pins each client is allowed to make before old ones start being deleted.").Value;

            WEB_PINS = config.Bind("User", "web_pins",
                MuninConfig.WEB_PINS,
                "Let people place and remove their own pins from the web page (right click or long press the map). Chat pins (!pin) only reach the server while two or more players are online, so this is the way that always works.").Value;

            WEB_PINGS = config.Bind("User", "web_pings",
                MuninConfig.WEB_PINGS,
                "Let people ping the map from the web page (middle click), shown in game like a player's ping, once every 10 seconds each.").Value;

            WEB_EDIT_BASES = config.Bind("User", "web_edit_bases",
                MuninConfig.WEB_EDIT_BASES,
                "Let people rename or hide the auto-detected player bases from the web page (click a base marker). Off: only the token holder can, through the API.").Value;

            BASE_MIN_PIECES = config.Bind("User", "base_min_pieces",
                MuninConfig.BASE_MIN_PIECES,
                new ConfigDescription("How many built pieces a cluster needs before it counts as a player base and gets a marker. Higher = fewer, bigger bases.", new AcceptableValueRange<int>(5, 2000))).Value;

            BASE_MIN_PER_CELL = config.Bind("User", "base_min_per_cell",
                MuninConfig.BASE_MIN_PER_CELL,
                new ConfigDescription("How many pieces a 32 m cell needs before it joins a base. Lower = fences and outposts join their base; higher = only dense building counts.", new AcceptableValueRange<int>(1, 100))).Value;

            WEBSOCKET_COMPRESSION = config.Bind("Server", "websocket_compression",
                MuninConfig.WEBSOCKET_COMPRESSION,
                "Allow permessage-deflate on the live websocket. Off by default: some reverse proxies (IIS ARR) accept the handshake and then drop every frame.").Value;

            TILE_WEBP = config.Bind("Server", "tile_webp",
                MuninConfig.TILE_WEBP,
                "Store map and overlay tiles as lossless WebP: same picture, 25-35% less upload per tile. New tiles are served as PNG at once and converted in the background. Browsers without WebP still get PNG. ImageSharp is merged into MuninMap.dll.").Value;

            SERVER_PORT = config.Bind("Server", "server_port",
                MuninConfig.SERVER_PORT,
                "HTTP port for the website. The map will be display on this site.").Value;

            PLAYER_UPDATE_INTERVAL = config.Bind("Interval", "player_update_interval",
                MuninConfig.PLAYER_UPDATE_INTERVAL,
                "How often do we send position data to web browsers in seconds.").Value;

            CACHE_SERVER_FILES = config.Bind("Server", "cache_server_files",
                MuninConfig.CACHE_SERVER_FILES,
                "Keep the web files (page, scripts, styles) in memory once read. Set false while testing changes to them: each is then read from disk when asked for, so a new file shows on the next page refresh without restarting the server.").Value;

            DEFAULT_ZOOM = config.Bind("Texture", "default_zoom",
                MuninConfig.DEFAULT_ZOOM,
                "How zoomed in should the web map start at? Higher is more zoomed in.").Value;

            MAX_MESSAGES = config.Bind("Server", "max_messages",
                MuninConfig.MAX_MESSAGES,
                "How many messages to keep buffered and display to client.").Value;

            ALWAYS_MAP = config.Bind("User", "always_map",
                MuninConfig.ALWAYS_MAP,
                "Update the map to show where hidden players have traveled.").Value;

            ALWAYS_VISIBLE = config.Bind("User", "always_visible",
                MuninConfig.ALWAYS_VISIBLE,
                "Completely ignore the players preference to be hidden.").Value;

            DEBUG = config.Bind("Server", "debug",
                MuninConfig.DEBUG,
                "Output debugging information.").Value;

            PERF_REPORT = config.Bind("Debug", "perf_report", false,
                "Every 5 minutes, log how much of the game's own thread Munin takes, job by job (ms a second). For checking the map doesn't slow the server.").Value;

            TEST = config.Bind("Server", "test",
                MuninConfig.TEST,
                "Enable test features (bugs).").Value;

            SHOW_VEHICLES = config.Bind("Server", "show_vehicles",
                MuninConfig.SHOW_VEHICLES,
                "Report boats and carts at /vehicles. They are only ever reported in "
                + "territory players have already explored, but turning this off stops "
                + "the endpoint reporting anything at all.").Value;

            DISCORD_WEBHOOK = config.Bind("Server", "discord_webhook",
                MuninConfig.DISCORD_WEBHOOK,
                "Discord webhook URL").Value;

            DISCORD_WORLD_EVENTS = config.Bind("Server", "discord_world_events",
                MuninConfig.DISCORD_WORLD_EVENTS,
                "Also post raids and invasions (starting and ending) to the Discord webhook, with a link to the spot on the map.").Value;

            DISCORD_INVITE_URL = config.Bind("Server", "discord_invite_url",
                MuninConfig.DISCORD_INVITE_URL,
                "Optional Discord invite URL to be added to the webpage.").Value;

            URL = config.Bind("Server", "map_url",
                MuninConfig.URL,
                "URL to view the web map.").Value;

            MAP_TITLE = config.Bind("Server", "map_title",
                MuninConfig.MAP_TITLE,
                "Title shown in the web map header. Empty = the server name.").Value;

            RENDER_THREADS = config.Bind("Render", "render_threads",
                MuninConfig.RENDER_THREADS,
                "Worker threads for rendering map tiles. 1 is right for most servers; 2 on a box with spare cores. "
                + "0 samples terrain on the game thread in small slices (slowest, safest).").Value;

            PRERENDER_ZOOM = config.Bind("Render", "prerender_zoom",
                MuninConfig.PRERENDER_ZOOM,
                "Render the whole world up to this zoom on first start (0 = 128 m/px ... 7 = 1 m/px). "
                + "5 (4 m/px, ~400 tiles) takes about a minute. Closer zooms are only rendered where players have explored.").Value;

            MAX_RENDER_ZOOM = config.Bind("Render", "max_render_zoom",
                MuninConfig.MAX_RENDER_ZOOM,
                "Closest zoom rendered over explored ground. 7 = 1 m/px (full detail), 6 = 2 m/px (a quarter of the disk and CPU).").Value;

            HEIGHT_MAX_ZOOM = config.Bind("Render", "height_max_zoom",
                MuninConfig.HEIGHT_MAX_ZOOM,
                "Closest zoom for height tiles (used by the 3D view and the contour lines). 6 = 2 m/px is plenty for contours; -1 turns height tiles (and contours) off.").Value;

            MAIN_THREAD_ROWS_PER_FRAME = config.Bind("Render", "main_thread_rows_per_frame",
                MuninConfig.MAIN_THREAD_ROWS_PER_FRAME,
                "When sampling on the game thread, how many tile rows to sample per frame.").Value;

            SWEEP_INTERVAL = config.Bind<float>("Sweep", "sweep_interval",
                MuninConfig.SWEEP_INTERVAL,
                "Seconds between walks over the world's objects (structures, trees, terraforming, portals).").Value;

            FIRST_SWEEP_DELAY = config.Bind<float>("Sweep", "first_sweep_delay",
                MuninConfig.FIRST_SWEEP_DELAY,
                "Seconds after world load before the first sweep. Buildings and world structures stay empty on the map until it has run.").Value;
            SECOND_SWEEP_DELAY = config.Bind<float>("Sweep", "second_sweep_delay",
                MuninConfig.SECOND_SWEEP_DELAY,
                "Seconds after world load for one extra sweep, to pick up anything still loading during the first one. "
                + "After it the regular sweep_interval applies. 0 turns it off.").Value;

            SWEEP_ZDOS_PER_FRAME = config.Bind("Sweep", "zdos_per_frame",
                MuninConfig.SWEEP_ZDOS_PER_FRAME,
                "Objects inspected per game frame during a sweep, at most. Lower is gentler on the game, higher finishes sooner.").Value;

            SWEEP_FRAME_MS = config.Bind("Sweep", "frame_ms",
                MuninConfig.SWEEP_FRAME_MS,
                "Milliseconds of each game frame the sweep may use (a dedicated server runs about 30 frames a second, 33 ms each). "
                + "Lower keeps the game smoother, higher finishes sooner.").Value;

            REVEAL_ALL = config.Bind("Markers", "reveal_all",
                MuninConfig.REVEAL_ALL,
                "Ignore the fog of war entirely: no black veil, render and show the whole world, every build, tree and marker. Testing only. "
                + "The full world at 1 m/px is ~6400 tiles and renders on demand as people browse.").Value;

            SHOW_LAST_SEEN_POSITION = config.Bind("User", "show_last_seen_position",
                MuninConfig.SHOW_LAST_SEEN_POSITION,
                "Show where offline players were last seen in the stats panel.").Value;

            string hidden = config.Bind("User", "hidden_players", "",
                "Character names, comma separated, never shown on the map, in the stats or in the event feed (an admin, an alt).").Value;
            HIDDEN_PLAYERS = new HashSet<string>((hidden ?? "").Split(',').Select(n => n.Trim()).Where(n => n.Length > 0), StringComparer.OrdinalIgnoreCase);

            EVENT_LOG = config.Bind("Server", "event_log",
                MuninConfig.EVENT_LOG,
                "Append joins, leaves, deaths, chat and pings to events.jsonl in the world's map data.").Value;

            STATS_SAVE_INTERVAL = config.Bind<float>("Interval", "stats_save_interval",
                MuninConfig.STATS_SAVE_INTERVAL,
                "How often to save stats.json, in seconds.").Value;

            ENABLE_3D = config.Bind("Server", "enable_3d",
                MuninConfig.ENABLE_3D,
                "Offer the 3D view in the web map.").Value;

            SHOW_GITHUB_LINK = config.Bind("Server", "show_github_link",
                MuninConfig.SHOW_GITHUB_LINK,
                "Show a link to Munin Map on GitHub in the web map's top bar (with a dot on it when a newer version is out).").Value;

            CHECK_UPDATES = config.Bind("Server", "check_updates",
                MuninConfig.CHECK_UPDATES,
                "Ask GitHub for Munin Map's latest release when the server starts and every 12 hours, to tell when an update is out "
                + "(the server log says so, and the web map's GitHub link). Off: the server never contacts GitHub.").Value;

            EXPORT_MODELS = config.Bind("Models", "export_models",
                MuninConfig.EXPORT_MODELS,
                "Export the game's prefab meshes (buildings, trees, rocks, ruins) as glTF for the 3D view. "
                + "Runs once per prefab on the game thread, a few milliseconds per frame, and is cached under map_data/models.").Value;

            OBJECT_CATEGORIES = config.Bind("Models", "object_categories",
                MuninConfig.OBJECT_CATEGORIES,
                "Which kinds of world object the 3D view gets, comma separated: piece (everything built), other (ruins, "
                + "dungeon entrances, furniture, boats), rock, bush, tree. Trees and bushes are ~half the objects of a world; "
                + "their leaves only show once the leaf textures are extracted, trunks always do.").Value;

            USE_TEXTURES = config.Bind("Models", "use_textures",
                MuninConfig.USE_TEXTURES,
                "Texture the 3D models. The mod reads the textures its models need out of the game's own asset "
                + "files on a background thread (a minute or two on first start, once per game version). "
                + "Off: flat material colours; existing texture files are ignored.").Value;

            EXTRACT_MESHES = config.Bind("Models", "extract_meshes",
                MuninConfig.EXTRACT_MESHES,
                "Read the meshes the engine keeps locked (most of them: carts, beehives, ruins, rocks...) out of the game's "
                + "own asset files, so the 3D view shows the real shape instead of a box. Background thread, a few minutes "
                + "on first start, once per game version. Off: locked meshes stay boxes.").Value;

            TEXTURE_MAX_SIZE = config.Bind("Models", "texture_max_size",
                MuninConfig.TEXTURE_MAX_SIZE,
                "Longest side of exported textures, in pixels. 256 is plenty for a map; 512 looks sharper up close.").Value;

            MODEL_EXPORT_MS_PER_FRAME = config.Bind("Models", "export_ms_per_frame",
                MuninConfig.MODEL_EXPORT_MS_PER_FRAME,
                "Game-thread time budget per frame for model export while the queue drains.").Value;

            LEGACY_MAP = config.Bind("Server", "legacy_map",
                MuninConfig.LEGACY_MAP,
                "Build the single-image 2048px world render (map.png, served at /map and /map.jpg) if it does not exist yet. "
                + "Takes a few seconds on the game thread at world load; the web map does not need it.").Value;
        }

        public static string GetWorldName()
        {
            if (ZNet.instance != null)
            {
                WORLD_NAME = ZNet.instance.GetWorldName();
            }
            else
            {
                string[] arguments = Environment.GetCommandLineArgs();
                string worldName = "";
                for (int t = 0; t < arguments.Length; t++)
                    if (arguments[t] == "-world")
                    {
                        worldName = arguments[t + 1];
                        break;
                    }
                WORLD_NAME = worldName;
            }
            return WORLD_NAME;
        }

        public static string MakeClientConfigJson()
        {
            Dictionary<string, object> config = new Dictionary<string, object>();

            config["world_name"] = GetWorldName();
            config["world_start_pos"] = WORLD_START_POS;
            config["default_zoom"] = DEFAULT_ZOOM;
            config["texture_size"] = TEXTURE_SIZE;
            config["pixel_size"] = PIXEL_SIZE;
            config["fog_size"] = World.Fog.GRID;
            config["fog_pixel"] = World.Fog.CELL;
            config["update_interval"] = PLAYER_UPDATE_INTERVAL;
            config["explore_radius"] = EXPLORE_RADIUS;
            config["max_messages"] = MAX_MESSAGES;
            config["web_pins"] = WEB_PINS;
            config["web_pings"] = WEB_PINGS;
            config["web_edit_bases"] = WEB_EDIT_BASES;
            config["max_pins_per_user"] = MAX_PINS_PER_USER;
            config["always_map"] = ALWAYS_MAP;
            config["always_visible"] = ALWAYS_VISIBLE;
            config["title"] = string.IsNullOrEmpty(MAP_TITLE) ? (MuninMap.serverInfo != null && MuninMap.serverInfo.ContainsKey("serverName") ? MuninMap.serverInfo["serverName"].ToString() : "Valheim") : MAP_TITLE;
            config["discord_invite_url"] = DISCORD_INVITE_URL;
            config["version"] = MuninMap.VERSION;
            config["tile_size"] = Tiles.TileMath.TILE_SIZE;
            config["max_zoom"] = Tiles.TileMath.MAX_ZOOM;
            config["max_render_zoom"] = MAX_RENDER_ZOOM;
            config["height_max_zoom"] = HEIGHT_MAX_ZOOM;
            config["world_size"] = Tiles.TileMath.WORLD_SIZE;
            config["chunk_size"] = Tiles.TileMath.CHUNK_SIZE;
            config["water_level"] = Tiles.TileJob.WaterLevel;
            config["enable_3d"] = ENABLE_3D;
            config["github_link"] = SHOW_GITHUB_LINK;
            config["latest_version"] = UpdateCheck.Latest;
            config["latest_url"] = UpdateCheck.LatestUrl;
            config["show_last_seen_position"] = SHOW_LAST_SEEN_POSITION;
            config["reveal_all"] = REVEAL_ALL;
            config["models"] = EXPORT_MODELS;

            string json = DictionaryToJson(config);
            return json;
        }

        static string DictionaryToJson(Dictionary<string, object> dict)
        {
            var entries = dict.Select(d =>
            {
                switch (d.Value)
                {
                    case float o:
                        return $"\"{d.Key}\": {o.ToString("F2", CultureInfo.InvariantCulture)}";
                    case double o:
                        return $"\"{d.Key}\": {o.ToString("F2", CultureInfo.InvariantCulture)}";
                    case string o:
                        return $"\"{d.Key}\": \"{o}\"";
                    case bool o:
                        return $"\"{d.Key}\": {o.ToString().ToLower()}";
                    case Vector3 o:
                        return $"\"{d.Key}\": \"{o.x.ToString("F2", CultureInfo.InvariantCulture)}," +
                               $"{o.y.ToString("F2", CultureInfo.InvariantCulture)}," +
                               $"{o.z.ToString("F2", CultureInfo.InvariantCulture)}\"";
                    default:
                        return $"\"{d.Key}\": {d.Value}";
                }
            });
            return "{\n    " + string.Join(",\n    ", entries) + "\n}\n";
        }
    }
}
