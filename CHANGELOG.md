# Changelog

## 2.2.5

Mistlands mist, invasions, shouts and weather on the map, raids and invasions on Discord, and a better phone view. Upgrading: replace the whole `BepInEx/plugins/WebMap` folder; your `map_data` and config are kept. Press Ctrl+Shift+R once in the browser.

**Map**
* Mistlands mist, like the game's map: the Mistlands are covered in mist (from the game's own map mask, explored ground only), and a player wearing a Wisplight clears it around them. In 3D it lies over the ground with tall things showing through. A switch in Layers turns it off. It loads after the map, at low priority, and is drawn only where there is mist.
* Invasions (the game's long-running events, e.g. a Jotun Invasion) drawn like the game's map draws them: the red area and the pulsing "!" with the invasion's name, over the fog. Click one (or a raid) for when it ends, its size and who is near it. They have their own group in Markers and a switch in Layers.
* Invasions starting and ending are in Events (with a filter chip) and pop up as notices.
* Shouts show on the map where they were shouted for 10 seconds, with the game's shout pin, for players who share their position.
* Weather: each online player's weather where they stand (worked out the way their game does) on their tooltip, card and in the Players list; the wind's direction beside the coordinates.
* The raid sprites are fetched ahead, so the first raid or invasion shows at once.

**Discord**
* Raids and invasions starting and ending are posted to the webhook, with who is near and a link to the spot on the map (`url` set). Off with `discord_world_events = false`.

**Phone**
* The zoom buttons no longer float over the full-screen menu.
* Search results use the screen's width, each place's group and position under its name.

**Server**
* Building the markers at the end of a sweep is spread over frames like the rest (Valheim02's worst sweep frame: ~60 ms to ~34 ms).

## 2.2.4

Lighter on the game server, and the page keeps up with players without catching. Upgrading: replace the whole `BepInEx/plugins/WebMap` folder; your `map_data` and config are kept. Press Ctrl+Shift+R once in the browser.

**Game server**
* The world sweep (every 2 minutes) takes at most a few milliseconds of each game frame while players are on (new setting `frame_ms`, default 4). Sorting a big world's trees, ruins and 3D objects used to stop the server for up to half a second at a time, which players felt as a hitch.
* The sweep walks the world a map sector at a time instead of copying the whole object table in one frame (~150 ms on a 3-million-object world).
* Location names are matched with plain (ordinal) string comparison: building the markers went from ~50-90 ms to ~20 ms.
* Player bases are worked out again only when buildings change.
* Fixed: zones whose terrain data changed nothing were re-rendered on every sweep.
* The legacy forest and structure overlay images are made only when asked for, off the game's thread.

**Page**
* Moving players update their arrow and health bar in place instead of being remade every second (about 7x less work per update); the Players panel redraws only while it is open.
* The first fog reveal around a player no longer stalls the page (~300 ms).
* The fog mask is decoded and blurred in a background worker: no freeze at load or each time it refreshes while people explore.
* Marker list rows and Stats discoveries use the same icons as the map.
* The page starts fetching its startup data before its scripts load, and batches redraws as data arrives: far less stutter while the map fills in after a reload.

## 2.2.3

Smoother everywhere, the map gets ready before you need it, and more to see and switch. Upgrading: replace the whole `BepInEx/plugins/WebMap` folder; your `map_data` and config are kept. Press Ctrl+Shift+R once in the browser.

**Smoother**
* Mid-zoom, trees, ruins and buildings are drawn from a picture redrawn a few blocks a frame, instead of every one of them every frame (3-4x less work a frame from zoom 4 to 7).
* With the map still, they are kept as a picture too: a marker hovered, a label fading, a player moving or a drag no longer redraws every tree.
* Bushes and plants smaller than a pixel aren't drawn zoomed out; they ease in from zoom 5.5.
* The wheel zoom eases the way it did before 2.2.2 (2.2.2's spring lagged behind the wheel).
* While the map is idle the page gets ahead: every explored tree, ruin and building fetched and made ready for the GPU (nearest first, ~9 MB once, kept by the browser), the map tiles one zoom in around the mouse and one out, the marker icons and labels, the biome grid. The first zoom no longer catches.
* Faster page load: every script the page starts with is fetched at once (modulepreload), and the 1 MB world-edge backdrop waits until the map is up.

**Map**
* Dungeons: the popup tells what the save knows of the inside: rooms, chests emptied, locked gate open or shut, monsters inside, graves, and what is still in there to pick or mine (by the game's names), and whether anyone has been in.
* Cartography table pins drawn like the game's: its own pin sprites, crossed-out ones with the game's red cross.
* Trees, bushes, berries, rocks, ore, mushrooms and plants each have a switch, and under its arrow every kind can be hidden on its own. Ore and rocks are now told apart (copper, tin, silver, obsidian, muddy scrap; boulders, cliffs, giant bones).
* World structures can be hidden by kind of place: houses, stone ruins, fuling villages, Dvergr sites, Ashlands ruins, shipwrecks, camps, boss altars, Deep North, dungeons.

## 2.2.2

The whole world fits on the map, new things to see (biomes, dungeons, cartography table pins, raids), and zooming is much smoother. Upgrading: replace the whole `BepInEx/plugins/WebMap` folder; your `map_data` and config are kept. The map grid grew, so tiles are redrawn once on the first start. Press Ctrl+Shift+R once in the browser.

**Map**
* The map grid is 21504 m (was 20480), so land right out to the world's edge is never cut off on the north, south, east and west sides.
* World edge like the in-game map: a fine line, the ocean fading into it, a soft glow out into the space backdrop.
* Map style "Biomes": solid biome colours with region names. The biome under the cursor shows beside the coordinates, like in game.
* Dungeon entrances (burial chambers, sunken crypts, troll caves, frost caves, infested mines, charred fortresses), each with its own icon and colour, on explored ground. Off by default.
* Cartography table pins, read from the tables in the world, as their own group with the game's pin icons. Off by default (there can be hundreds).
* Raids: the area the game marks during a raid, with the game's own raid sprites and the raid's message.
* Mushrooms and plants (thistle, magecap, wild flax...) as their own vegetation group with their own colours.
* Fix: "Remove pin" on the page never worked (the server refused the empty request).

**Smoother**
* Wheel zoom eases like a spring: no surge and stall with every notch of a mouse wheel.
* Trees, buildings and ruins are drawn in 1 km blocks (about 6x fewer draw calls around zoom 4-5, where the zoom caught).
* New tiles, labels and blocks are handed to the GPU a few a frame instead of all at once when a zoom crosses a level.
* Building and ruin data is parsed and packed in a worker; the per-frame check of what to load only looks at chunks that have data.
* Markers group with a count when zoomed out, fewer labels far out, all icons in one draw.
* The sidebar's Markers and Stats tabs are only rebuilt while open (rebuilding the markers list after every world update stalled the map).

**Players and stats**
* No "View in 3D" when 3D is off; more stats on the player card. The page refetches markers and stats after a reconnect.

## 2.2.1

**Fog closer to the in-game map**
* Cartography tables are read: everything players recorded to a table (the game's own explored map) is uncovered exactly.
* Player traces count: the explore radius around every building, ship, cart, portal and tombstone is uncovered (someone stood there).
* `reveal_visited` was tuned against recorded table maps: margin 4 (the new default) with a 64 m circle per zone uncovers about 99% of what players had explored, where the old default 3 also opened about twice as much ground they never saw.
* `explore_radius` default 110 (the in-game map clears 100 m; a little more so trails don't look tighter than people remember), and the circle includes its edge cells like the game's.
* Existing configs keep their values. To tighten a map that already opened wider: set `reveal_visited_margin = 4`, stop the server, delete `map_data/<world>/fog.png`, start again.

**Map**
* Past the world's edge: the in-game map's own space backdrop, taken from the game files at startup (clouds as before when it can't be).
* Spawn rings reach the world's edge (whole rings only), the outermost is labelled, and the dashes stay put while zooming.
* Mini-boss lairs (Hildir's sisters: Smouldering Tomb, Howling Cavern, Sealed Tower) on explored ground, with the game's Hildir icons and a "found" event.
* Tile URLs carry the world's name, so a server that switches worlds can't show the old world's cached tiles.

**Stats**
* Mini bosses (Brenna, Geirrhafa, Zil & Thungr), Hildir's quests, what first kills unlocked (troll, surtling, bat raids), other boss keys (from mods too), and the world modifiers in words (Combat: Hard, Resources: More... with the exact effects on hover).

**3D**
* Left out: the Valkyrie on the spawn pillars and creatures inside models, loose pickups, loot chests, cave insides, attack roots and floating crates. Model library format 7: models are exported again once.

## 2.2.0

Smoother on slow connections and slow machines, and a lot more to look at. Upgrading: replace the whole `BepInEx/plugins/WebMap` folder with the one in the zip (it has four new DLLs next to `WebMap.dll`); your `map_data` and config are kept. Press Ctrl+Shift+R once in the browser after updating.

**2D map**
* Everything on the map is drawn on the GPU (WebGL): ground, fog, trees, buildings, world structures, markers, labels, grid and spawn rings. Zooming is continuous and smooth like valheim.tools, without stutter; jumping to a marker glides instead of stretching.
* Round world with a zoom-out limit, clouds past the edge, black fog inside.
* Trees and berries in their own colours (oak, birch, autumn birch, pine, raspberry, blueberry, cloudberry...), seabed rocks hidden, shallow-water ones faded. Trees, bushes & berries and rocks & ore switch on and off on their own.
* The game's own map icons (with a badge), Spawn and boss altars always labelled, traders (Haldor, Hildir, the Bog Witch) once found, distance rings from spawn, grid with coordinates.
* Labels no longer flicker on slow scroll or drag.

**Bandwidth and loading**
* Map tiles as WebP (about 45% smaller), content-hashed URLs cached for good, 304s for everything else: a repeat visit loads ~8 KB.
* Trees, buildings and world structures fetched by region instead of per chunk (421 to 143 requests on first load), unpacked off the main thread.
* After a restart everything is served from disk caches straight away (`markers-cache.json`, `structures-cache.txt`, `ruins-cache.txt`, `vegetation-cache.bin`).

**Events**
* New: raids (start and end, where), boss kills (and who was there), boss altars and traders found, everyone slept, a player's first time in a biome. Remembered in `world-events.txt`.
* Chat reads "Name: message", a leave shows once, the game's "I have arrived!" shout is dropped. Grouped filters with All / None, "Show older" pages back through `events.jsonl`.
* Server log view for admins (the key in `announce.token`): the game's console lines with the time each was written, kept in `server-log.txt`.

**Stats**
* Boss progress, totals (boss kills, raids, nights slept, deaths, peak online, bases, portals, ships and carts), pieces built per player, a leaderboard, discoveries with when and who.

**Sidebar**
* Layers, Markers and Players redesigned: switch cards with a map key, a marker search with folding sets and linked portal pairs, player cards plus who was on recently.

**3D**
* Drawn only when something changes, shadows only when needed, lighter trees, distant clutter left out, lower resolution while moving: much cooler and smoother on laptops. Loads while moving instead of after.
* Banners, rugs, cloth doors and crafting stations now have models (skinned meshes). Model library format 6: models are exported again once.

**Server**
* `GET /api/serverlog`, `GET /data/events/older.json`, `vegetation-kinds.txt` (every object name a sweep met, for checking the tree classifier).
* `tile_webp` config (on by default).

## 2.1.6

* Rename or hide the auto-detected bases from the web page. Click a base marker: type a name and Rename, "Hide this base" to drop it, "Auto name" to go back to the portal tag. Saved in `map_data/<world>/bases.json`, kept across re-scans. `web_edit_bases` turns it off (a token still works). `POST /api/base`, `POST /api/bases/reset`. (#14)
* 3D view links. The URL now follows the 3D camera (`#x,z,zoom,3d,distance,heading,tilt`), so a copied link opens the same spot, same angle. Before, only 2D updated the URL. (#13)
* Sharp 2D close-ups. Past the tiles' native zoom (1 m per pixel) the map used to scale pixels up into blocks. Now trees and rocks are drawn as shapes from the vegetation data, buildings are drawn at the screen's own resolution (crisp on phones and Retina), and the ground is scaled smoothly instead of in blocks. (#12)
* `base_min_pieces` and `base_min_per_cell` config: how much built stuff it takes before a spot counts as a base. Raise them if a lone workbench shows up as one.

## 2.1.5

* GitHub Releases. A version tag builds the mod in GitHub Actions and attaches the zip plus `SHA256SUMS` to a release, so the mod can be pinned by hash. (#15)
* Script and stylesheet URLs carry a hash of the file (via the import map), so a CDN or browser cache can never mix old and new web files. Fixes "WASD works in 2D but not 3D" / "time of day does nothing" after an update behind Cloudflare.
* Fix: a few box-only prefabs were re-exported every minute ("N models to re-export with newly extracted textures" forever), which also kept the texture extractor from starting for anything new.
* Keyboard everywhere: WASD/arrows move, Q E turn, R F tilt, Z X zoom, Shift fast, P follow next player, M 2D/3D, L layers, Home spawn. Double click in 3D centres on that spot. README "Controls".
* Follow works in 3D: camera glides after the player; drag, WASD or Esc lets go. Following carries over when you switch 2D/3D.
* "Lighting (3D)" heading in Layers for time of day and shadows.

## 2.1.4

* Sky, sun, moon and shadows in 3D. Sky dome with sun disc, stars, dawn and dusk colours; fog to the horizon; the sun follows the server's time of day (Layers > "Time of day", or pick a fixed one); buildings, trees and hills cast shadows (toggle, off on phones by default); water ripples and mirrors the sky; filmic tone mapping.

## 2.1.3

* Every model, not one in eight. The engine locks most meshes, so the 3D view drew boxes (a cart with its boxes but no cart, a bare beehive). The mod now reads locked meshes out of the game files like it already did textures: `extract_meshes` config, `map_data/models/meshes/`, `tools/extract_meshes.py` by hand. Model library format 5: everything is exported again once.
* Materials that keep their colour map under `_BaseMap` and friends get their texture too.

## 2.1.1

* Addressed issue [#2](https://github.com/f00d4tehg0dz/valheim-webmap/issues/2) with !pin command not working. Pins from the web page. Right click (long press on a phone), pick a type, label, done. Remove your own from the popup. `POST /api/pin`, `POST /api/unpin?id=`. `web_pins` turns it off. 
* Addressed issue [#3](https://github.com/f00d4tehg0dz/valheim-webmap/issues/3) with title bar in Mobile not being supported.
* Addressed issue [#1](https://github.com/f00d4tehg0dz/valheim-webmap/issues/1) with Web dir not being included in build, causing mod manager Gale to fail installation
* Addressed issue [#5](https://github.com/f00d4tehg0dz/valheim-webmap/issues/5) with @Aughen PR #6. WebSocket compression off by default, `websocket_compression` turns it back on. 
* Addressed issue with fog coverage not being 100% on initial load with @clanofartisans PR #7
* Build script: one `-ValheimManaged` path works. (#6)
* `POST /api/reload` (token): Update the web app without restarting the game.
* Layer list says "Pins", not "Chat pins".

## 2.1.0

* Old trips count. Fog lifts everywhere players have already been, even from
  before the mod was installed. The world save lists every zone the game
  built, and it only builds them near a player. Runs at start and once a
  minute. `reveal_visited` (on) and `reveal_visited_margin` (3 zones, about
  150 m from where they walked).
* Player card. Click a player on the map, in 3D or in the list: health,
  stamina, eitr, what they wear and hold, state, lifetime stats. Updates live.
  Server now sends stamina, eitr and gear.
* Export. Pick an area, get a 3D scene: glTF (instanced, or one node per
  object) or an Unreal pack with a 16-bit heightmap, CSVs in Unreal units
  and an editor script. Made in the browser. Fog applies.
* Demo site. `tools/Dockerfile.demo` and `docker-compose.demo.yml` run the
  mock server as a public demo. Mock draws the tree and rock overlay and
  loops forever.
* Stats table fits the sidebar. README in plain words.

## 1.0.1

First public release. Same as 1.0.0 plus README and screenshot fixes.

## 1.0.0

First release.

* Tiled map, seven zoom levels, 1 m/px. Rendered from world generator plus
  player terraforming (levelled ground, moats, paved roads, farmland). Trees,
  bushes, rocks as a separate overlay layer. Close-zoom tiles render only over explored ground.
  Re-render when ground changes. Worker thread, own PNG encoder, sliced
  main-thread fallback.
* Buildings as vector data per 256 m chunk: footprint, height, material,
  prefab. Drawn as material-coloured footprints with hover info.
* 3D view (three.js). Terrain from height tiles with quadtree LOD, seamless
  between tiles, clean ground tiles with tiled fine grain up close. Water.
  Players. Markers. World objects as game's own meshes: mod exports each
  prefab to glTF once (`map_data/models/`), publishes each chunk's objects
  with prefab, position, rotation, scale. Browser instances models.
* Textures pulled from the game's own asset files by the mod itself, in the
  background, on every platform. `tools/extract_textures.py` as manual
  fallback. `POST /api/reexport` rebuilds models.
* Fog of war always on. Black over unexplored ground. World locations
  (bosses, dungeons, traders) never published.
* Markers: portals with tags and links, tombstones,
  player bases (clusters of built pieces), boats, carts, custom
  `markers.json` sets.
* Layer toggles drive 2D and 3D: buildings with opacity, players, chat pins,
  trees and rocks overlay (2D),
  labels, 256 m grid, marker sets, object categories.
* Stats per player: playtime, sessions, deaths, distance, portal trips,
  biomes. Per server: day, explored %, counts, online history.
* Event feed with `events.jsonl` history. Deaths carry position.
* Web app: dark UI, sidebar, search, permalinks, follow mode, mobile
  layout. No build step.
* Simple endpoints kept for scripts: `/map`, `/players`, `/pins`,
  `/messages`, `/structures`, `/forest`, `/vehicles`. Websocket speaks JSON.
* Discord webhook, `POST /announce`, chat pin commands.
