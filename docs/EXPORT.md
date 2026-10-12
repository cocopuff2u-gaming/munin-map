# Export to Blender, Unreal, Unity, Godot

Top bar, download icon. Choose area, detail, content, format. File downloads.
Everything is built in the browser from the data the map already has.

## What is in the file

* `Terrain`: one mesh per 256 m chunk, 1/2/4 m grid, textured with the map
  tile (ground colour, terraforming, roads). Only explored chunks.
* `Water`: one plane at sea level, 70 % blue.
* `Objects`: every building piece, ruin, rock, bush, tree in the area with
  its real mesh, exact rotation and scale. Trees carry a `_leaves` billboard
  (three crossed quads with the leaf texture) because the game's leaf cards
  are never exported as geometry.
* `Markers`: empties named `set: label` (portals, tombstones, bases, boats).

Units: metres, Y up, glTF right-handed (`x` east, `-z` north). Valheim
coordinate `(x, z)` is glTF `(x, -z)`.

## Blender

File > Import > glTF 2.0. Use the **instanced** file. Blender reads
`EXT_mesh_gpu_instancing` (3.6+): one mesh per prefab, instanced.
Y-up to Z-up conversion happens on import. Materials come with textures and
alpha for leaves.

## Unreal Engine 5

Two ways.

**Scene as it is.** File > Import Into Level > `scene.glb` from the Unreal
pack (or the *one node per object* glb). Interchange makes one Static Mesh
per prefab and an actor per object, terrain meshes included. Units convert
to centimetres automatically.

**Landscape.** Use `heightmap_r16.png`:

1. Landscape mode > Import from File, pick the PNG.
2. Scale and Location: copy the numbers from `README.txt` in the pack.
   Z scale = height range / 512 × 100. Location puts the map on the same
   coordinates as `instances.csv`.
3. Import. Unreal pads to its component grid.
4. Delete the `Terrain` actors from the scene import.

`instances.csv` is in Unreal units already (cm, X = east, Y = south, Z =
up). `import_munin_map.py` spawns it onto imported meshes as
Hierarchical Instanced Static Meshes (Tools > Execute Python Script; edit
`MESH_ROOT` to where the meshes landed). Optional: the scene import already
placed everything.

## Unity

Use the *one node per object* glb. Unity 2022+: Package Manager, add
`com.unity.cloud.gltfast` (glTFast), drop the file in Assets. Or the
instanced file: glTFast reads `EXT_mesh_gpu_instancing` too.

## Godot

Import the instanced glb (4.x reads the extension). Y up matches.

## three.js

`GLTFLoader` reads both. Instanced file gives `InstancedMesh` objects.

## Limits

* Max 36 chunks per export (1.5 km square). Bigger: several exports.
* Terrain at 1 m is 66k vertices per chunk; use 2 m unless you need it.
* Locations (bosses, dungeons, traders) are never in the data, so never
  in the export. Undiscovered ground has no tiles, so no terrain.

## Content

Prefab meshes and textures are the game's assets, read from your own
server's game files by the mod. Use the export for your own scenes,
renders and level work. Do not redistribute the file.
