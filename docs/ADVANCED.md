# Munin Map: advanced

Everything the [README](../README.md) leaves out: every setting, your own
markers, the HTTP API, server load, and building from source.

* [All settings](#all-settings)
* [Your own markers](#your-own-markers)
* [Where things are kept](#where-things-are-kept)
* [Server load and disk](#server-load-and-disk)
* [Textures and meshes for 3D](#textures-and-meshes-for-3d)
* [Pins and bases](#pins-and-bases)
* [Exporting a 3D scene](#exporting-a-3d-scene)
* [HTTP API](#http-api)
* [Caches and Cloudflare](#caches-and-cloudflare)
* [Building from source](#building-from-source)
* [Trying it without a real server](#trying-it-without-a-real-server)

## All settings

File: `BepInEx/config/com.cocopuff2u.muninmap.cfg`. Read at server start only.

| Section | Key | Default | What |
|---|---|---|---|
| Server | `server_port` | 3000 | HTTP port |
| Server | `map_title` | (server name) | title on the page |
| Server | `enable_3d` | true | offer the 3D view |
| Server | `webmap_url` | | the map's address, used in Discord posts and in game |
| Server | `discord_webhook` | | Discord webhook URL for events |
| Server | `discord_world_events` | true | also post raids and invasions, with a link to the spot |
| Server | `discord_invite_url` | | Discord invite shown on the page |
| Server | `event_log` | true | write events to `events.jsonl` |
| Server | `max_messages` | 100 | how many events to keep in memory for the page |
| Server | `show_vehicles` | true | report boats and carts |
| Server | `tile_webp` | true | store tiles as lossless WebP (25-35% smaller than PNG) |
| Server | `websocket_compression` | false | permessage-deflate on the live feed. Some proxies (IIS ARR) drop every frame with it on |
| Server | `legacy_map` | true | build one big `map.png` for `/map` |
| Server | `cache_server_files` | true | keep web files in memory. `false` for web development |
| Server | `debug` | false | extra log lines |
| Interval | `player_update_interval` | 1 | seconds between player updates to the page |
| Interval | `update_fog_texture_interval` | 2 | seconds between fog updates |
| Texture | `explore_radius` | 110 | metres of fog cleared around a player (the in-game map uses 100) |
| Texture | `reveal_visited` | true | at start, clear the fog everywhere the world save shows players have been |
| Texture | `reveal_visited_margin` | 4 | how tightly that follows players; lower is wider. 4 matched recorded in-game maps best |
| Texture | `default_zoom` | 100 | starting zoom of the page |
| User | `always_map` | true | clear fog where hidden players walk (their position stays hidden) |
| User | `always_visible` | false | ignore players' "hidden" setting |
| User | `show_last_seen_position` | false | offline players' last position in stats |
| User | `web_pins` | true | let the page place pins (right click, long press) |
| User | `max_pins_per_user` | 50 | pins per browser; the oldest go first |
| User | `web_edit_bases` | true | let the page rename or hide bases |
| User | `base_min_pieces` | 30 | pieces in one spot before it counts as a base |
| User | `base_min_per_cell` | 6 | pieces in a 32 m cell before it joins a base |
| Render | `render_threads` | 1 | threads drawing tiles (0 = on the game thread, slowly) |
| Render | `prerender_zoom` | 5 | draw the whole world up to this zoom at first start |
| Render | `max_render_zoom` | 7 | closest zoom over explored ground (7 = 1 m per pixel) |
| Render | `height_max_zoom` | 7 | closest zoom for height tiles (3D) |
| Sweep | `sweep_interval` | 120 | seconds between walks over the world's objects |
| Sweep | `zdos_per_frame` | 3000 | objects looked at per game frame, at most |
| Sweep | `frame_ms` | 4 | milliseconds of each game frame a walk may use while players are on |
| Markers | `reveal_all` | false | no fog at all: the whole world is shown and sent out. Testing only |
| Models | `export_models` | true | export the game's models for 3D |
| Models | `object_categories` | piece,other,rock,bush,tree | what the 3D view and export get |
| Models | `use_textures` | true | textures on 3D models (off: flat colours) |
| Models | `extract_meshes` | true | read locked meshes out of the game files (off: boxes) |
| Models | `texture_max_size` | 512 | longest texture edge |
| Models | `export_ms_per_frame` | 6 | game-thread milliseconds per frame for model export |

## Your own markers

Put a `markers.json` in the world's folder:
`BepInEx/plugins/MuninMap/map_data/<world>/markers.json`. It's read again
whenever it changes.

```json
{ "sets": [
  { "id": "mines", "label": "Mines", "markers": [
    { "x": 2210, "z": 980, "label": "Silver mine", "icon": "mine", "description": "north face" }
  ] }
] }
```

Icons: `pin dot fire mine house cave boss trader dungeon camp village ruin runestone wreck portal tombstone boat cart poi spawn`.

## Where things are kept

Everything is under `BepInEx/plugins/MuninMap/map_data/`. Keep it when you
update.

| Path | What |
|---|---|
| `<world>/tiles/` | the drawn map |
| `<world>/fog.png` | explored area |
| `<world>/stats.json` | player and world stats |
| `<world>/events.jsonl` | the events log |
| `<world>/bases.json` | base names you set |
| `<world>/markers.json` | your own markers (you make this one) |
| `<world>/*-cache.*` | caches so a restart serves the map at once |
| `models/` | 3D models, textures and meshes from the game files |
| `runestones.txt` | which locations hold runestones and Vegvisirs |

To start a world's map over, stop the server and delete its folder.

## Server load and disk

Map drawing runs on its own thread (`render_threads`, low priority). On the
game's thread the mod only does small slices of work:

* a walk over the world's objects every `sweep_interval` seconds, at most
  `frame_ms` milliseconds per frame while players are on
* a player update once a second
* fog updates
* model export, within `export_ms_per_frame`

Disk: the whole world at 1 m per pixel would be a few hundred MB, but close
zooms are only drawn where players have been, so a normal world is tens to
hundreds of MB. `max_render_zoom = 6` cuts that by four.

Upload: tiles are WebP and cached by browsers for good. A repeat visit loads
a few KB.

## Textures and meshes for 3D

A dedicated server doesn't let mods read most textures and meshes, so Munin
Map reads them straight out of the game's own files instead. It does this
once per game version, on a low-priority background thread, a few minutes
after the first start. Log:

```
MuninMap: extracting 142 textures from the game files in .../valheim_server_Data
MuninMap: 139 of 142 textures extracted from 61 files in 74s, 3 not found
MuninMap: extracting 1900 meshes from the game files in .../valheim_server_Data
MuninMap: 1880 of 1900 meshes extracted from 800 files in 180s, 20 not found
```

These are the game's own files. They're never in this repo or the zip, and
only ever sit in your `map_data/models/` folder. Only the finished `.glb`
models go to browsers. `use_textures = false` and `extract_meshes = false`
turn this off.

To do it by hand (plain Python 3, no packages):

```
python3 tools/extract_textures.py <valheim_server_Data> <plugins>/MuninMap/map_data/models
python3 tools/extract_meshes.py <valheim_server_Data> <plugins>/MuninMap/map_data/models
```

## Pins and bases

**From the page**: right-click the map (long press on a phone), pick a type,
write a label. Only the browser that made a pin can remove it.
`web_pins = false` turns this off.

**From chat**: `!pin [type] [text]` (types `dot`, `fire`, `mine`, `house`,
`cave`), `!undoPin`, `!deletePin [text]`. The game sends chat from player to
player, so the server only sees it while two or more players are online.

**Bases** are found from what's built. Click a base marker to rename it, hide
it, or go back to the automatic name (a nearby portal's tag, else "Base").
Saved in `bases.json`. If a lone workbench shows up as a base, raise
`base_min_pieces`.

## Exporting a 3D scene

The download button in the top bar exports an area as glTF (Blender, Godot,
three.js), glTF with one object per node (Unreal, Unity), or an Unreal pack
with a heightmap. Fog applies: nothing unexplored leaves the server. See
[EXPORT.md](EXPORT.md) for each editor.

## HTTP API

| Path | Returns |
|---|---|
| `/tiles/map/{z}/{x}/{y}.png` | map tile. 404 + `X-WebMap-Tile: pending` while drawing |
| `/tiles/height/{z}/{x}/{y}.png` | height tile, [Terrarium](https://github.com/tilezen/joerd/blob/master/docs/formats.md#terrarium) encoding |
| `/data/players.json` | players online: position, health, gear, state |
| `/data/markers.json` | every marker set |
| `/data/stats.json`, `/data/events.json`, `/data/pins.json` | stats, recent events, pins |
| `/data/structures/index.json`, `/data/structures/{cx}_{cz}.json` | built pieces per 256 m chunk |
| `/data/objects/index.json`, `/data/objects/{cx}_{cz}.bin` | placed objects per chunk (for 3D) |
| `/data/prefabs.json`, `/models/{hash}.glb` | the 3D model library |
| `/data/fog.png` | explored area |
| `/config`, `/api/status` | settings for the page, drawing and sweep status |
| `/map`, `/map.jpg` | the whole world as one image |
| `POST /api/sweep` | walk the world now |
| `POST /api/pin`, `POST /api/unpin`, `POST /api/base` | add or remove a pin, rename or hide a base |

Need the token (`X-Announce-Token: <secret>`, from `announce.token` next to
the DLL):

| Path | Does |
|---|---|
| `POST /announce` | the request body is shown on every player's screen |
| `POST /api/reload` | forget cached web files and refresh every open browser |
| `POST /api/rerender?zoom=N` | draw tiles again from zoom N up |
| `POST /api/reexport` | export every 3D model again |
| `POST /api/bases/reset` | forget every base rename and hide |
| `GET /api/serverlog` | the game's log lines (the page's Server log view) |

Websocket at `/ws`: JSON frames `hello`, `players`, `events`, `tiles`,
`world`, `ping`, `pin`, `rmpin`, `reload`.

Coordinates: the map grid is a 21,504 m square around the world's centre.
At zoom 7, one pixel is one metre; each zoom out halves that.

## Caches and Cloudflare

Every script and stylesheet URL carries a hash of the file, so a browser or
CDN can never mix old and new files. Only the page itself (`/` and
`/index.html`) must not be cached for long. Behind Cloudflare, bypass the
cache for those two, or purge after each update.

## Building from source

Needs the .NET SDK (8 or newer) and the Valheim dedicated server's files.

* **Linux / macOS**: `./build.sh --managed <server>/valheim_server_Data/Managed`
* **Windows**: `.\build.ps1` (finds the Steam "Valheim Dedicated Server"
  tool, or pass `-ValheimManaged <path>`)

Output: `dist/MuninMap-<version>.zip`. Add `--deploy <BepInEx/plugins>` (or
`-Deploy`) to copy it straight to a server.

**Releases** are built by GitHub Actions. Set the version in `manifest.json`
and `VERSION` in `WebMap/WebMap.cs`, add a section to `CHANGELOG.md`, then
push a tag:

```
git tag v3.0.1 && git push origin main v3.0.1
```

The workflow downloads the dedicated server with SteamCMD, builds, and
attaches the zip, a `SHA256SUMS` file and the changelog section to the
release.

**Changing only the web page?** Copy the files into `web/` next to the DLL,
then `POST /api/reload` with the token: every open browser refreshes, no game
restart needed. (A copy of the page is also built into the DLL, used when the
`web/` folder is missing.)

## Trying it without a real server

* `node tools/mockserver.js`: the page with a made-up world, fake players and
  fake events, on `http://localhost:3000`. No packages needed.
* `docker compose -f tools/docker-compose.demo.yml up -d --build`: the same
  as a small public demo site. No game files inside.
* `tools/docker-compose.test.yml`: a real dedicated server in Docker with a
  copy of your world. Set `reveal_all = true` in its config to see the whole
  map.
