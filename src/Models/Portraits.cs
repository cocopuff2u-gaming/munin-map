using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using MuninMap.Live;
using MuninMap.Tiles;
using MuninMap.Util;

namespace MuninMap.Models
{
    // A 3D portrait of each character for their card: the game's own player model, made the way the
    // character-select screen makes it (the Player prefab with networking off), dressed through its
    // VisEquipment in what they wear (body, skin and hair colour, hair, beard, armour, cape, what's in
    // their hands), posed by its animator, read back as meshes and written as a glTF model beside the
    // prefab models (map_data/models/portrait_<id>.glb). The instance lives for an instant and is
    // never part of the world.
    //
    // The id is a hash of the look and the gear: the same outfit is made once. The page asks
    // data/portrait.json?id= and gets the model's name and what the body needs: the game paints chest
    // and leg armour onto the body (its skin shader mixes skin, chest and leg textures), so the page
    // does that mix. Textures come from the game's files (TextureExtractor), fetched the first time
    // a portrait wants one; the portrait is made again once they're in.
    internal static class Portraits
    {
        private sealed class Spec
        {
            public int model; public Vector3 skin, hair; public string hairItem, beardItem;
            public Dictionary<string, string> gear = new Dictionary<string, string>();
            public Dictionary<string, int> variants = new Dictionary<string, int>();
        }

        private static readonly Dictionary<string, Spec> specs = new Dictionary<string, Spec>();
        private static readonly Queue<string> queue = new Queue<string>();
        private static readonly HashSet<string> queued = new HashSet<string>();
        private static readonly Dictionary<string, List<string>> missingTex = new Dictionary<string, List<string>>();   // id -> textures it was made without
        private static readonly Dictionary<string, List<string>> missingMesh = new Dictionary<string, List<string>>();  // id -> locked meshes (MeshCache keys) it was made without
        private static readonly Dictionary<string, List<string>> missingMat = new Dictionary<string, List<string>>();   // id -> materials whose colours weren't read yet
        private static volatile bool meshDone, matDone;
        private static volatile bool texDone;
        private static readonly object sync = new object();

        private static readonly string[] Slots = { "helmet", "chest", "legs", "shoulder", "utility", "trinket", "right", "left", "rightBack", "leftBack" };

        // Main thread: the portrait id for this look, gear and gear styles (remembered, so it can be made when asked)
        public static string Id(Players.Look look, Func<string, string> gearOf, Func<string, int> variantOf)
        {
            var sb = new StringBuilder("p4|");
            sb.Append(look.model).Append('|').Append(Players.Hex(look.skin)).Append('|').Append(Players.Hex(look.hair)).Append('|').Append(look.hairItem).Append('|').Append(look.beardItem);
            var spec = new Spec { model = look.model, skin = look.skin, hair = look.hair, hairItem = look.hairItem, beardItem = look.beardItem };
            foreach (var slot in Slots)
            {
                string item = gearOf(slot) ?? "";
                sb.Append('|').Append(item);
                if (item.Length == 0) continue;
                spec.gear[slot] = item;
                int v = variantOf != null ? variantOf(slot) : 0;
                if (v > 0) { sb.Append(':').Append(v); spec.variants[slot] = v; }
            }
            string id = unchecked((uint)sb.ToString().GetStableHashCode()).ToString("x8");
            lock (sync)
                if (!specs.ContainsKey(id))
                {
                    specs[id] = spec;
                    // made ahead, so the card has it the moment it's opened
                    if (Dir != null && !File.Exists(InfoPath(id)) && queued.Add(id)) queue.Enqueue(id);
                }
            return id;
        }

        private static string Dir => ModelStore.Root;
        // the portraits' own textures, at twice the 3D view's size (they're seen close up): models/hd/
        private const int HdSize = 1024;
        private static string HdDir { get { string d = Path.Combine(Dir, "hd"); Directory.CreateDirectory(d); return d; } }
        private static bool HasHd(string tex) => File.Exists(Path.Combine(HdDir, PrefabExporter.TextureFileName(tex)));
        // the texture file to use, relative to models/: the close-up one, else the 3D view's, else none
        private static string Tex(string tex)
        {
            if (string.IsNullOrEmpty(tex) || !MuninConfig.USE_TEXTURES) return null;
            string f = PrefabExporter.TextureFileName(tex);
            return HasHd(tex) ? "hd/" + f : File.Exists(Path.Combine(Dir, f)) ? f : null;
        }
        private static string GlbName(string id) => "portrait_" + id + ".glb";
        private static string InfoPath(string id) => Path.Combine(Dir, "portrait_" + id + ".json");

        // Web thread: data/portrait.json?id= -> the portrait's details once made, {"pending":true} while
        // it's being made, null for an id never seen
        public static string Json(string id)
        {
            if (Dir == null || string.IsNullOrEmpty(id) || id.Length != 8) return null;
            foreach (char c in id) if (!Uri.IsHexDigit(c)) return null;
            string info = InfoPath(id);
            if (File.Exists(info) && File.Exists(Path.Combine(Dir, GlbName(id))))
            {
                try { return File.ReadAllText(info); } catch { }
            }
            lock (sync)
            {
                if (!specs.ContainsKey(id)) return null;
                if (queued.Add(id)) queue.Enqueue(id);
            }
            return "{\"pending\":true}";
        }

        // Main thread: make what was asked for, one a frame; fetch textures portraits went without
        public static IEnumerator Pump()
        {
            while (true)
            {
                string id = null;
                lock (sync) if (queue.Count > 0) id = queue.Dequeue();
                if (id != null)
                {
                    Spec spec;
                    lock (sync) specs.TryGetValue(id, out spec);
                    try { if (spec != null) Make(id, spec); }
                    catch (Exception e) { ZLog.LogWarning("MuninMap: portrait " + id + " failed: " + e.Message); }
                    lock (sync) queued.Remove(id);
                    yield return null;
                    continue;
                }
                if (matDone)
                {
                    // material colours came in: make again the portraits that went without them
                    matDone = false;
                    var again = new List<string>();
                    lock (sync) foreach (var kv in missingMat) foreach (var k in kv.Value) if (MaterialExtractor.Known(Dir, k)) { again.Add(kv.Key); break; }
                    foreach (var a in again) { try { File.Delete(InfoPath(a)); } catch { } lock (sync) { missingMat.Remove(a); if (specs.ContainsKey(a) && queued.Add(a)) queue.Enqueue(a); } }
                }
                else if (!MaterialExtractor.Running && !MeshExtractor.Running && !TextureExtractor.Running && TryFetchMaterials()) { }
                else if (meshDone)
                {
                    // locked meshes came in: make again the portraits that went without them
                    meshDone = false;
                    var again = new List<string>();
                    lock (sync) foreach (var kv in missingMesh) foreach (var k in kv.Value) if (MeshCache.Exists(Dir, k)) { again.Add(kv.Key); break; }
                    foreach (var a in again) { try { File.Delete(InfoPath(a)); } catch { } lock (sync) { missingMesh.Remove(a); if (specs.ContainsKey(a) && queued.Add(a)) queue.Enqueue(a); } }
                }
                else if (!MeshExtractor.Running && !TextureExtractor.Running && TryFetchMeshes()) { }
                else if (texDone)
                {
                    // textures came in: make again the portraits that went without them
                    texDone = false;
                    var again = new List<string>();
                    lock (sync) foreach (var kv in missingTex) foreach (var t in kv.Value) if (HasHd(t)) { again.Add(kv.Key); break; }
                    foreach (var a in again) { try { File.Delete(InfoPath(a)); } catch { } lock (sync) { missingTex.Remove(a); if (specs.ContainsKey(a) && queued.Add(a)) queue.Enqueue(a); } }
                }
                else if (MuninConfig.USE_TEXTURES && !TextureExtractor.Running && !MeshExtractor.Running)
                {
                    var want = new HashSet<string>();
                    lock (sync) foreach (var l in missingTex.Values) foreach (var t in l) if (!HasHd(t)) want.Add(t);
                    if (want.Count > 0 && TextureExtractor.Start(want, HdDir, HdSize, () => texDone = true))
                        ZLog.Log($"MuninMap: fetching {want.Count} textures for player portraits");
                }
                yield return new WaitForSeconds(1f);
            }
        }

        // What glows (runes, crystals, embers): the material's emission colour and map, as the game draws
        // it. Colours past white (the game's HDR glow) are scaled down to fit; the standard shader glows only
        // with its emission switched on.
        private static void Glow(GlbWriter.Primitive prim, Material mat, PrefabExporter.Result res, List<string> glowReport, List<string> wantMats)
        {
            if (mat == null) return;
            try
            {
                // read straight from the material (its shader is stripped on a dedicated server: asking it
                // whether the property exists says no)
                string map = TexName(mat, "_EmissionMap");
                string[] kws = null; try { kws = mat.shaderKeywords; } catch { }
                bool kwOn = kws != null && Array.IndexOf(kws, "_EMISSION") >= 0;
                if (map == null && !kwOn) return;   // (no sign of a glow: not looked up)
                Color c = Color.black;
                var saved = Saved(mat, "_EmissionColor", wantMats);
                if (saved.HasValue) c = saved.Value;
                else { try { c = mat.GetColor("_EmissionColor"); } catch { } }
                string shader = ""; try { shader = mat.shader != null ? mat.shader.name : ""; } catch { }
                float top = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                if (top >= 0.02f || map != null)
                    glowReport.Add(mat.name + " [" + shader + "] colour " + c.r.ToString("0.##") + "," + c.g.ToString("0.##") + "," + c.b.ToString("0.##") + (map != null ? " map " + map : "") + (kwOn ? " on" : kws != null && kws.Length > 0 ? " off(" + string.Join(" ", kws) + ")" : ""));
                if (top < 0.02f) return;
                if (shader == "Standard" && kws != null && !kwOn) return;   // (the standard shader glows only with emission switched on)
                if (top > 1f) { c.r /= top; c.g /= top; c.b /= top; }
                if (map != null)
                {
                    if (!res.wants.Contains(map)) res.wants.Add(map);
                    prim.emissiveUri = Tex(map);
                    if (prim.emissiveUri == null) return;   // (not fetched yet: made again once it is)
                }
                prim.er = c.r; prim.eg = c.g; prim.eb = c.b;
            }
            catch { }
        }

        // Particle effects and lights as the page needs them: where (the portrait's frame: x, y, -z), which
        // way, how many and how long, speed, size, colour from birth to death, gravity, the shape they come
        // out of, the particle texture and whether it adds light (fire) or covers (smoke). An approximation
        // the page animates, from the game's own numbers.
        private static void Effects(GameObject go, Matrix4x4 toRoot, List<string> outList, PrefabExporter.Result res, List<string> wantMats)
        {
            string V3(Vector3 v) => "[" + F(v.x) + "," + F(v.y) + "," + F(-v.z) + "]";
            string C(Color c) => "[" + F(c.r) + "," + F(c.g) + "," + F(c.b) + "," + F(c.a) + "]";
            foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(false))
            {
                if (ps == null || !ps.gameObject.activeInHierarchy) continue;
                var main = ps.main; var em = ps.emission; var sh = ps.shape;
                float rate = em.enabled ? Val(em.rateOverTime) : 0f;
                float bursts = 0f;
                try { for (int b = 0; b < em.burstCount; b++) bursts += Val(em.GetBurst(b).count) / Mathf.Max(0.2f, em.GetBurst(b).repeatInterval > 0 ? em.GetBurst(b).repeatInterval : main.duration); } catch { }
                rate += bursts;
                if (rate <= 0f) continue;
                var pr = ps.GetComponent<ParticleSystemRenderer>();
                if (pr == null || !pr.enabled || pr.renderMode == ParticleSystemRenderMode.Mesh) continue;
                // render mode None: the particles aren't drawn, only their trails (lightning arcs, swirls)
                var tr = ps.trails;
                bool trailOnly = pr.renderMode == ParticleSystemRenderMode.None;
                bool trailed = false; try { trailed = tr.enabled; } catch { }
                if (trailOnly && !trailed) continue;
                var mat = trailOnly && pr.trailMaterial != null ? pr.trailMaterial : pr.sharedMaterial;
                string tex = null; string shader = ""; Color tint = Color.white;
                if (mat != null)
                {
                    tex = PrefabExporter.ColorTextureName(mat);
                    try { shader = mat.shader != null ? mat.shader.name.ToLowerInvariant() : ""; } catch { }
                    var tc = Saved(mat, "_TintColor", wantMats) ?? Saved(mat, "_Color", wantMats);
                    if (tc.HasValue) tint = tc.Value;
                }
                if (tex != null && !res.wants.Contains(tex)) res.wants.Add(tex);
                // light-adding (fire) or covering (smoke): the shader says on a client; on a dedicated server its name
                // is all that's left, so the effect's own names are read too (flames, embers, sparks, flares glow)
                string words = (shader + " " + (mat != null ? mat.name : "") + " " + (tex ?? "") + " " + ps.name).ToLowerInvariant();
                // light-adding (fire, sparks, magic: most item effects) or covering (smoke, blood): the shader
                // says on a client; on a dedicated server only names are left, so covering is told by them
                bool additive = !(words.Contains("smoke") || words.Contains("dust") || words.Contains("cloud") || words.Contains("blood")
                    || words.Contains("poison") || words.Contains("ooz") || words.Contains("drip") || words.Contains("splat") || words.Contains("steam"));
                float scale = 1f;
                try { if (main.scalingMode != ParticleSystemScalingMode.Shape) scale = Mathf.Max(0.01f, ps.transform.lossyScale.x); } catch { }
                Color c0 = Grad(main.startColor), cA = Color.white, cB = Color.white, cZ = Color.white;
                var col = ps.colorOverLifetime;
                if (col.enabled) { cA = col.color.Evaluate(0f, 0.5f); cB = col.color.Evaluate(0.5f, 0.5f); cZ = col.color.Evaluate(1f, 0.5f); }
                float s0 = 1f, s1 = 1f, s2 = 1f;
                var sol = ps.sizeOverLifetime;
                if (sol.enabled) { s0 = sol.size.Evaluate(0f, 0.5f); s1 = sol.size.Evaluate(0.5f, 0.5f); s2 = sol.size.Evaluate(1f, 0.5f); }
                Vector3 pos = toRoot.MultiplyPoint3x4(ps.transform.position), dir = toRoot.MultiplyVector(ps.transform.forward).normalized;
                string shape = sh.enabled ? sh.shapeType.ToString() : "Sphere";
                float radius = sh.enabled ? sh.radius * scale : 0.01f, angle = sh.enabled ? sh.angle : 25f;
                int tiles = 1; try { var ts = ps.textureSheetAnimation; if (ts.enabled) tiles = Math.Max(1, ts.numTilesX); } catch { }
                float life = Val(main.startLifetime);
                if (!(life > 0.01f)) life = main.duration > 0.05f ? main.duration : 1f;   // (no lifetime given: as long as the effect runs)
                var j = new JsonWriter(512);
                j.BeginObject().Prop("kind", "particles").Key("pos").Raw(V3(pos)).Key("dir").Raw(V3(dir))
                    .Prop("rate", Mathf.Min(rate, 120f), 2).Prop("life", life, 3).Prop("speed", Val(main.startSpeed) * scale, 3)
                    .Prop("size", Mathf.Min(Val(main.startSize) * scale, 0.6f), 3).Prop("gravity", Val(main.gravityModifier), 3).Prop("max", Math.Min(main.maxParticles, 200))
                    .Prop("shape", shape).Prop("radius", radius, 3).Prop("angle", angle, 1).Prop("additive", additive).Prop("tiles", tiles)
                    .Key("color").Raw(C(c0 * tint)).Key("fade").BeginArray().Raw(C(cA)).Raw(C(cB)).Raw(C(cZ)).End()
                    .Key("grow").BeginArray().Value(s0, 3).Value(s1, 3).Value(s2, 3).End();
                // gradient-mapped (most fire, frost and lightning): the texture is only light and dark, and the
                // colour is picked along a ramp from the second custom colour (dark) to the first (bright), both
                // changing over the particle's life: [bright at birth, bright at death, dark at birth, dark at death]
                try
                {
                    var cd = ps.customData;
                    if (cd.enabled && cd.GetMode(ParticleSystemCustomData.Custom1) == ParticleSystemCustomDataMode.Color
                        && cd.GetMode(ParticleSystemCustomData.Custom2) == ParticleSystemCustomDataMode.Color)
                    {
                        var hi = cd.GetColor(ParticleSystemCustomData.Custom1); var lo = cd.GetColor(ParticleSystemCustomData.Custom2);
                        j.Key("ramp").BeginArray().Raw(C(GradAt(hi, 0f))).Raw(C(GradAt(hi, 1f))).Raw(C(GradAt(lo, 0f))).Raw(C(GradAt(lo, 1f))).End();
                    }
                }
                catch { }
                // trails: how long they stay (a share of the particle's life) and how wide; trail-only ones
                // draw nothing else
                if (trailed)
                {
                    float width = 1f, ratio = 0.5f;
                    try { width = Val(tr.widthOverTrail) * (tr.sizeAffectsWidth ? Val(main.startSize) * scale : 1f); ratio = Val(tr.lifetime); } catch { }
                    j.Key("trail").BeginObject().Prop("life", Mathf.Clamp(ratio, 0.05f, 1f), 3).Prop("width", Mathf.Clamp(width, 0.003f, 0.3f), 4).Prop("only", trailOnly).End();
                }
                // noise: the wander the game adds to each particle's path (what makes lightning jagged)
                try
                {
                    var nz = ps.noise;
                    if (nz.enabled) j.Key("noise").BeginObject().Prop("strength", Val(nz.strength) * scale, 3).Prop("freq", nz.frequency, 3).End();
                }
                catch { }
                string tf = tex != null ? Tex(tex) : null;
                if (tf != null) j.Prop("tex", tf);
                j.Prop("name", ps.name).End();
                outList.Add(j.ToString());
            }
            foreach (var l in go.GetComponentsInChildren<Light>(false))
            {
                if (l == null || !l.enabled || !l.gameObject.activeInHierarchy || l.intensity <= 0f) continue;
                var j = new JsonWriter(160);
                j.BeginObject().Prop("kind", "light").Key("pos").Raw(V3(toRoot.MultiplyPoint3x4(l.transform.position)))
                    .Key("color").Raw(C(l.color)).Prop("intensity", l.intensity, 2).Prop("range", l.range, 2).Prop("name", l.name).End();
                outList.Add(j.ToString());
            }
        }
        private static string F(float v) => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        private static float Val(ParticleSystem.MinMaxCurve c)
        {
            try
            {
                switch (c.mode)
                {
                    case ParticleSystemCurveMode.Constant: return c.constant;
                    case ParticleSystemCurveMode.TwoConstants: return (c.constantMin + c.constantMax) * 0.5f;
                    default: return c.Evaluate(0.5f, 0.5f);
                }
            }
            catch { return 0f; }
        }
        private static Color Grad(ParticleSystem.MinMaxGradient g)
        {
            try
            {
                switch (g.mode)
                {
                    case ParticleSystemGradientMode.Color: return g.color;
                    case ParticleSystemGradientMode.TwoColors: return Color.Lerp(g.colorMin, g.colorMax, 0.5f);
                    default: return g.Evaluate(0.5f, 0.5f);
                }
            }
            catch { return Color.white; }
        }

        // a particle colour at this point of its life (0 born, 1 gone)
        private static Color GradAt(ParticleSystem.MinMaxGradient g, float t)
        {
            try
            {
                switch (g.mode)
                {
                    case ParticleSystemGradientMode.Color: return g.color;
                    case ParticleSystemGradientMode.TwoColors: return Color.Lerp(g.colorMin, g.colorMax, 0.5f);
                    case ParticleSystemGradientMode.TwoGradients: return Color.Lerp(g.gradientMin.Evaluate(t), g.gradientMax.Evaluate(t), 0.5f);
                    default: return g.gradient != null ? g.gradient.Evaluate(t) : g.Evaluate(t, 0.5f);
                }
            }
            catch { return Color.white; }
        }

        // the style (variant) of the item this part belongs to: the slot it was put on for
        private static int StyleOf(VisEquipment ve, Renderer r, Func<string, int> V)
        {
            bool In(GameObject g) => g != null && r.transform.IsChildOf(g.transform);
            bool InAny(List<GameObject> l) { if (l != null) foreach (var g in l) if (In(g)) return true; return false; }
            if (InAny(ve.m_shoulderItemInstances)) return V("shoulder");
            if (In(ve.m_leftItemInstance)) return V("left");
            if (In(ve.m_leftBackItemInstance)) return V("leftBack");
            return 0;
        }

        private static bool TryFetchMaterials()
        {
            var want = new HashSet<string>();
            lock (sync) foreach (var l in missingMat.Values) foreach (var k in l) if (!MaterialExtractor.Known(Dir, k)) want.Add(k);
            if (want.Count == 0 || !MaterialExtractor.Start(want, Dir, () => matDone = true)) return false;
            ZLog.Log($"MuninMap: reading {want.Count} materials for player portraits");
            return true;
        }

        // A colour saved on the material, read from the game files (the engine answers black on a
        // dedicated server); null until read (it's then asked for, and the portrait made again)
        private static Color? Saved(Material mat, string prop, List<string> wantMats)
        {
            string name = mat.name.Replace(" (Instance)", "");
            if (MaterialExtractor.TryColor(Dir, name, prop, out Color c, out bool read)) return c;
            if (!read && !MaterialExtractor.Known(Dir, name) && !wantMats.Contains(name)) wantMats.Add(name);
            return null;
        }

        private static bool TryFetchMeshes()
        {
            var want = new HashSet<string>();
            lock (sync) foreach (var l in missingMesh.Values) foreach (var k in l) if (!MeshCache.Exists(Dir, k)) want.Add(k);
            if (want.Count == 0 || !MeshExtractor.Start(want, Dir, () => meshDone = true)) return false;
            ZLog.Log($"MuninMap: fetching {want.Count} meshes for player portraits");
            return true;
        }

        private static readonly int hairShader = Shader.PropertyToID("_SkinColor");

        private static void Make(string id, Spec spec)
        {
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("Player") : null;
            if (prefab == null || Dir == null) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            GameObject go = null;
            var writer = new GlbWriter();
            var res = new PrefabExporter.Result();
            string skinTex = null, chestTex = null, legsTex = null;
            int parts = 0;
            var skipped = new List<string>();      // parts left out, and why
            var untextured = new List<string>();   // parts whose material has no colour map to be found
            var meshes = new List<string>();       // locked meshes not read from the game files yet
            var glows = new List<string>();        // materials with glow settings, as read (for the report)
            var mats2 = new List<string>();        // materials whose saved colours aren't read yet
            var effects = new List<string>();      // particle effects and lights, as JSON objects
            var weaponGlow = new List<string>();   // parts drawn by the WeaponGlow shader, as JSON objects
            var partInfo = new List<string>();     // each part: how it was read, its size (for the report)
            var partFix = new List<string>();      // parts read from the files that were held to the engine's size
            try
            {
                ZNetView.m_forceDisableInit = true;
                try { go = UnityEngine.Object.Instantiate(prefab, new Vector3(0f, -10000f, 0f), Quaternion.identity); }
                finally { ZNetView.m_forceDisableInit = false; }
                var ve = go.GetComponent<VisEquipment>();
                if (ve == null) return;
                ve.m_modelIndex = Mathf.Clamp(spec.model, 0, Math.Max(0, ve.m_models.Length - 1));
                ve.m_skinColor = spec.skin; ve.m_hairColor = spec.hair;
                int H(string slot) => spec.gear.TryGetValue(slot, out string n) && n.Length > 0 ? n.GetStableHashCode() : 0;
                int V(string slot) => spec.variants.TryGetValue(slot, out int v) ? v : 0;
                ve.SetHairItem(string.IsNullOrEmpty(spec.hairItem) ? 0 : spec.hairItem.GetStableHashCode());
                ve.SetBeardItem(string.IsNullOrEmpty(spec.beardItem) ? 0 : spec.beardItem.GetStableHashCode());
                ve.SetHelmetItem(H("helmet")); ve.SetChestItem(H("chest")); ve.SetLegItem(H("legs"));
                ve.SetShoulderItem(H("shoulder"), V("shoulder"), 1); ve.SetUtilityItem(H("utility"));
                ve.SetRightItem(H("right"), 1); ve.SetLeftItem(H("left"), V("left"), 1);
                ve.SetRightBackItem(H("rightBack"), 1); ve.SetLeftBackItem(H("leftBack"), V("leftBack"), 1);
                if (spec.gear.TryGetValue("trinket", out string trinket)) ve.SetTrinketItem(trinket);
                ve.UpdateVisuals();
                // the idle stance: the animator run once (by name, as the build doesn't reference Unity's animation module)
                try
                {
                    var animType = Type.GetType("UnityEngine.Animator, UnityEngine.AnimationModule");
                    var anim = animType != null ? go.GetComponentInChildren(animType) : null;
                    if (anim != null)
                    {
                        // animated though no camera sees it (the server has none), then run for a second
                        // and a half so the idle has settled in, arms down
                        animType.GetProperty("cullingMode")?.SetValue(anim, Enum.ToObject(animType.GetProperty("cullingMode").PropertyType, 0));
                        var update = animType.GetMethod("Update", new[] { typeof(float) });
                        update?.Invoke(anim, new object[] { 0f });
                        for (int i = 0; i < 30; i++) update?.Invoke(anim, new object[] { 0.05f });
                    }
                }
                catch { }

                Matrix4x4 toRoot = go.transform.worldToLocalMatrix;
                var body = ve.m_bodyModel;
                Vector3 centre = Vector3.zero; bool haveCentre = false;
                if (body != null) { centre = toRoot.MultiplyPoint3x4(body.bounds.center); haveCentre = true; }
                foreach (var r in go.GetComponentsInChildren<Renderer>(false))
                {
                    if (!(r is SkinnedMeshRenderer || r is MeshRenderer)) continue;
                    string what = r.name;
                    if (!r.enabled) continue;   // (the game's own hidden parts: hair under a helmet, sheath of a drawn weapon)
                    Mesh mesh; Matrix4x4 m; List<int[]> cachedTris = null;
                    Vector3[] v = null, n = null; Vector2[] uv = null;
                    if (r is SkinnedMeshRenderer smr)
                    {
                        if (smr.sharedMesh == null) { skipped.Add(what + ": no mesh"); continue; }
                        mesh = new Mesh();
                        try { smr.BakeMesh(mesh); } catch (Exception e) { skipped.Add(what + ": " + e.Message); continue; }
                        // bound to the skeleton: baked in place, unscaled; with no bones (a beard hung on the jaw
                        // like a helmet) it's the mesh as it is, placed by its transform, scale and all
                        bool boned = false; try { boned = smr.bones != null && smr.bones.Length > 0; } catch { }
                        m = toRoot * Matrix4x4.TRS(r.transform.position, r.transform.rotation, Vector3.one);
                        if (!boned)
                        {
                            // with no bones, whether the engine's bake includes the part's scale differs (a beard
                            // came out at a hundredth, hats a hundred times over): both are tried, and the one
                            // the size of something worn, nearest the body, is kept
                            var scaled = toRoot * r.transform.localToWorldMatrix;
                            float Fit(Matrix4x4 mm)
                            {
                                var bv = mesh.vertices; if (bv == null || bv.Length == 0) return float.MaxValue;
                                Vector3 lo = mm.MultiplyPoint3x4(bv[0]), hi = lo;
                                for (int i = 1; i < bv.Length; i += Math.Max(1, bv.Length / 200)) { var q = mm.MultiplyPoint3x4(bv[i]); lo = Vector3.Min(lo, q); hi = Vector3.Max(hi, q); }
                                float ext = (hi - lo).magnitude, off = haveCentre ? ((lo + hi) * 0.5f - centre).magnitude : 0f;
                                return (ext < 0.03f || ext > 1.5f ? 100f : 0f) + off;
                            }
                            try { if (Fit(scaled) < Fit(m)) m = scaled; } catch { }
                        }
                    }
                    else
                    {
                        var mf = r.GetComponent<MeshFilter>();
                        mesh = mf != null ? mf.sharedMesh : null;
                        if (mesh == null) { skipped.Add(what + ": no mesh"); continue; }
                        m = toRoot * r.transform.localToWorldMatrix;
                        bool readable = false; try { readable = mesh.isReadable; } catch { }
                        if (!readable)
                        {
                            // a mesh the engine keeps locked: the copy read from the game files (MeshExtractor)
                            int mvc = 0, subCount = 0, idx0 = 0;
                            try { mvc = mesh.vertexCount; subCount = mesh.subMeshCount; } catch { }
                            try { idx0 = (int)mesh.GetIndexCount(0); } catch { }
                            string key = string.IsNullOrEmpty(mesh.name) || mvc <= 0 ? null : MeshCache.Key(mesh.name, mvc, subCount, idx0);
                            var cached = key != null ? MeshCache.Load(Dir, key) : null;
                            if (cached == null)
                            {
                                if (key != null && !meshes.Contains(key)) meshes.Add(key);
                                skipped.Add(what + ": locked mesh" + (key != null ? " (fetching)" : ""));
                                continue;
                            }
                            int cn = cached.positions.Length / 3;
                            v = new Vector3[cn];
                            for (int i = 0; i < cn; i++) v[i] = new Vector3(cached.positions[i * 3], cached.positions[i * 3 + 1], cached.positions[i * 3 + 2]);
                            // the copy read from the files, held to the engine's own size of the mesh (which it
                            // keeps even for a locked one): a mesh of the same name and counts elsewhere in the
                            // game (a decoration at another scale) must not stand in for it
                            try
                            {
                                Bounds eb = mesh.bounds;
                                Vector3 clo = v[0], chi = v[0];
                                for (int i = 1; i < cn; i++) { clo = Vector3.Min(clo, v[i]); chi = Vector3.Max(chi, v[i]); }
                                Vector3 csz = chi - clo, ccn = (clo + chi) * 0.5f;
                                float ce = csz.magnitude, ee = eb.size.magnitude;
                                if (ce > 1e-5f && ee > 1e-5f && (Mathf.Abs(ce / ee - 1f) > 0.05f || (ccn - eb.center).magnitude > ee * 0.05f))
                                {
                                    float k = ee / ce;
                                    for (int i = 0; i < cn; i++) v[i] = eb.center + (v[i] - ccn) * k;
                                    partFix.Add(what + " resized " + (1f / k).ToString("0.##") + "x");
                                }
                            }
                            catch { }
                            if (cached.normals != null) { n = new Vector3[cn]; for (int i = 0; i < cn; i++) n[i] = new Vector3(cached.normals[i * 3], cached.normals[i * 3 + 1], cached.normals[i * 3 + 2]); }
                            if (cached.uvs != null) { uv = new Vector2[cn]; for (int i = 0; i < cn; i++) uv[i] = new Vector2(cached.uvs[i * 2], cached.uvs[i * 2 + 1]); }
                            cachedTris = new List<int[]>();
                            foreach (var sm in cached.subMeshes) { var t = new int[sm.Length]; for (int i = 0; i < sm.Length; i++) t[i] = (int)sm[i]; cachedTris.Add(t); }
                        }
                    }
                    if (v == null)
                        try { v = mesh.vertices; n = mesh.normals; uv = mesh.uv; } catch (Exception e) { skipped.Add(what + ": " + e.Message); continue; }
                    if (v == null || v.Length == 0) { skipped.Add(what + ": empty"); continue; }
                    // a part that came out far from the body (bound to the wrong bones) is left out
                    // (judged by the middle of the part: one stray vertex doesn't make it far)
                    if (haveCentre)
                    {
                        Vector3 lo = v[0], hi = v[0];
                        for (int i = 1; i < v.Length; i++) { lo = Vector3.Min(lo, v[i]); hi = Vector3.Max(hi, v[i]); }
                        var c = m.MultiplyPoint3x4((lo + hi) * 0.5f);
                        if ((c - centre).sqrMagnitude > 9f) { skipped.Add(what + ": far from the body (" + (c - centre).magnitude.ToString("0.0") + " m)"); continue; }
                    }
                    // the part's make-up and size, for the report (how it was read and how big it came out)
                    {
                        Vector3 lo = m.MultiplyPoint3x4(v[0]), hi = lo;
                        for (int i = 1; i < v.Length; i += Math.Max(1, v.Length / 300)) { var q = m.MultiplyPoint3x4(v[i]); lo = Vector3.Min(lo, q); hi = Vector3.Max(hi, q); }
                        string how = r is SkinnedMeshRenderer sk2 ? ((sk2.bones?.Length ?? 0) > 0 ? "skinned" : "unbound") : cachedTris != null ? "from files" : "mesh";
                        Vector3 ls = r.transform.lossyScale;
                        partInfo.Add(what + " (" + how + ", " + (hi - lo).magnitude.ToString("0.00") + " m, scale " + ls.x.ToString("0.###") + ")");
                    }
                    Matrix4x4 nm = m.inverse.transpose;
                    bool mirrored = m.determinant < 0;
                    int vc = v.Length;
                    var pos = new float[vc * 3]; var nrm = n != null && n.Length == vc ? new float[vc * 3] : null; var uvs = uv != null && uv.Length == vc ? new float[vc * 2] : null;
                    for (int i = 0; i < vc; i++)
                    {
                        var p = m.MultiplyPoint3x4(v[i]);
                        pos[i * 3] = p.x; pos[i * 3 + 1] = p.y; pos[i * 3 + 2] = -p.z;
                        if (nrm != null) { var q = nm.MultiplyVector(n[i]).normalized; nrm[i * 3] = q.x; nrm[i * 3 + 1] = q.y; nrm[i * 3 + 2] = -q.z; }
                        if (uvs != null) { uvs[i * 2] = uv[i].x; uvs[i * 2 + 1] = 1f - uv[i].y; }
                    }
                    Material[] mats = r.sharedMaterials;
                    int subs = cachedTris != null ? cachedTris.Count : mesh.subMeshCount;
                    for (int s = 0; s < subs; s++)
                    {
                        int[] tri;
                        if (cachedTris != null) tri = cachedTris[s];
                        else try { tri = mesh.GetTriangles(s); } catch { continue; }
                        if (tri == null || tri.Length < 3) continue;
                        var idx = new uint[tri.Length];
                        for (int i = 0; i + 2 < tri.Length; i += 3)
                        {
                            if (mirrored) { idx[i] = (uint)tri[i]; idx[i + 1] = (uint)tri[i + 1]; idx[i + 2] = (uint)tri[i + 2]; }
                            else { idx[i] = (uint)tri[i + 2]; idx[i + 1] = (uint)tri[i + 1]; idx[i + 2] = (uint)tri[i]; }
                        }
                        Material mat = mats != null && mats.Length > 0 ? mats[Math.Min(s, mats.Length - 1)] : null;
                        var prim = new GlbWriter.Primitive { positions = pos, normals = nrm, uvs = uvs, indices = idx, roughness = 0.85f };
                        PrefabExporter.ApplyMaterial(prim, mat, Dir, new Palette.Rgb(150, 130, 110), res);
                        // its colour map at the portraits' size (the same lookup the 3D view uses: shaders name it differently)
                        string main = PrefabExporter.ColorTextureName(mat);
                        if (main != null) { if (!res.wants.Contains(main)) res.wants.Add(main); prim.textureUri = Tex(main) ?? prim.textureUri; }
                        bool cutout = prim.alphaMask;   // (a cut-out material, as the 3D view judges it: fur trims, leaves)
                        // its cut-out level, read from the game files (the engine answers 0 on a dedicated server):
                        // chainmail, feathers, the sheets on Storm Fang (the standard shader's alpha is shininess)
                        if (mat != null && !cutout)
                        {
                            string sh = ""; try { sh = mat.shader != null ? mat.shader.name.ToLowerInvariant() : ""; } catch { }
                            var cut = Saved(mat, "#_Cutoff", mats2);
                            if (cut.HasValue && cut.Value.r > 0.01f && !sh.Contains("standard")) cutout = true;
                        }
                        prim.foliage = false; prim.alphaMask = false;
                        if (r == body && s == 0 && mat != null)
                        {
                            // the body's skin: the page mixes in the armour painted on it
                            prim.name = "munin_body";
                            // (the skin from the body model chosen: the material keeps the prefab's own until the game swaps it)
                            var bm = ve.m_models != null && ve.m_modelIndex < ve.m_models.Length ? ve.m_models[ve.m_modelIndex].m_baseMaterial : null;
                            skinTex = PrefabExporter.ColorTextureName(bm) ?? main;
                            chestTex = TexName(mat, "_ChestTex"); legsTex = TexName(mat, "_LegsTex");
                            foreach (var t in new[] { skinTex, chestTex, legsTex }) if (t != null && !res.wants.Contains(t)) res.wants.Add(t);
                            if (skinTex == null) untextured.Add(what + " (skin)");
                        }
                        else if (main == null && mat != null && r != body && PrefabExporter.Has(mat, "_ChestTex") && PrefabExporter.Has(mat, "_LegsTex"))
                        {
                            // a part painted like the body (shorts and skirts under some chest armour): the same
                            // mix of skin, chest and leg textures, so the page paints it as the body
                            prim.name = "munin_body";
                            string c2 = TexName(mat, "_ChestTex"), l2 = TexName(mat, "_LegsTex");
                            if (chestTex == null) chestTex = c2; if (legsTex == null) legsTex = l2;
                            foreach (var t in new[] { c2, l2 }) if (t != null && !res.wants.Contains(t)) res.wants.Add(t);
                        }
                        else
                        {
                            if (mat != null && (mat.name.StartsWith("PlayerHair", StringComparison.Ordinal) || r.name.StartsWith("hair", StringComparison.OrdinalIgnoreCase) || r.name.StartsWith("beard", StringComparison.OrdinalIgnoreCase)))
                            {
                                prim.name = "munin_hair";   // tinted with their hair colour
                                prim.alphaMask = true;
                            }
                            // only a cut-out material is see-through where its texture is: on others the texture's
                            // alpha is shininess (the axe head would vanish)
                            else if (prim.textureUri != null) prim.alphaMask = cutout;
                            if (main == null && mat != null)
                            {
                                string style = TexName(mat, "_StyleTex");
                                if (style != null)
                                {
                                    // dyed (the linen cape): no picture of its own; its colour is one cell of a 4 x 4
                                    // palette, the cell its style picks, which its texture coordinates are mapped into
                                    if (!res.wants.Contains(style)) res.wants.Add(style);
                                    prim.textureUri = Tex(style);
                                    if (prim.textureUri == null || uv == null || uv.Length != vc) untextured.Add(what + "/" + mat.name + " (style sheet " + style + ")");
                                    else
                                    {
                                        int st = Math.Max(0, StyleOf(ve, r, V));
                                        float cu = (st % 4) * 0.25f, cv = (st / 4 % 4) * 0.25f;
                                        var suv = new float[vc * 2];
                                        for (int i = 0; i < vc; i++)
                                        {
                                            float u0 = uv[i].x - Mathf.Floor(uv[i].x), v0 = uv[i].y - Mathf.Floor(uv[i].y);
                                            suv[i * 2] = cu + 0.004f + u0 * 0.242f; suv[i * 2 + 1] = 1f - (cv + 0.004f + v0 * 0.242f);   // (kept off the cell edges: no bleed from the next colour)
                                        }
                                        prim.uvs = suv;
                                        prim.r = prim.g = prim.b = 1f;
                                    }
                                }
                                else if (HasAnyColourMap(mat)) untextured.Add(what + "/" + mat.name + TexSlots(mat));
                                else
                                {
                                    // drawn by the game in a plain colour (chains, stone, glass): that colour, as it is
                                    var pc = Saved(mat, "_Color", mats2);
                                    if (pc.HasValue) { prim.r = pc.Value.r; prim.g = pc.Value.g; prim.b = pc.Value.b; }
                                    prim.name = "munin_plain";
                                }
                            }
                            else if (main == null) untextured.Add(what + "/no material");
                        }
                        Glow(prim, mat, res, glows, mats2);
                        WeaponGlow(prim, mat, weaponGlow, mats2);
                        writer.Add(prim);
                    }
                    parts++;
                }
                // the effects the game plays on what they carry (a fire staff's flames, sparks, glowing
                // mist) and the lights some items give: their settings, for the page to play
                try { Effects(go, toRoot, effects, res, mats2); } catch (Exception e) { skipped.Add("effects: " + e.Message); }
            }
            finally { if (go != null) UnityEngine.Object.DestroyImmediate(go); }
            if (writer.Count == 0) return;
            byte[] glb = writer.Write("portrait", out float[] bounds);
            File.WriteAllBytes(Path.Combine(Dir, GlbName(id)), glb);
            var missing = new List<string>();
            foreach (var w in res.wants) if (!HasHd(w)) missing.Add(w);
            lock (sync)
            {
                if (missing.Count > 0) missingTex[id] = missing; else missingTex.Remove(id);
                if (meshes.Count > 0) missingMesh[id] = meshes; else missingMesh.Remove(id);
                if (mats2.Count > 0) missingMat[id] = mats2; else missingMat.Remove(id);
            }
            string F(string t) => Tex(t) ?? "";
            var j = new JsonWriter(512);
            j.BeginObject().Prop("glb", GlbName(id)).Prop("skinColor", Players.Hex(spec.skin)).Prop("hairColor", Players.Hex(spec.hair));
            j.Key("body").BeginObject().Prop("skin", F(skinTex)).Prop("chest", F(chestTex)).Prop("legs", F(legsTex)).End();
            j.Key("bounds").BeginArray(); foreach (var b in bounds) j.Value(b, 3); j.End();
            j.Prop("textures", missing.Count == 0 && meshes.Count == 0 && mats2.Count == 0).Prop("parts", parts);   // (false: made again once what it waits for is in)
            // what couldn't be drawn as the game draws it (a check: both empty when the model is complete)
            j.Key("skipped").BeginArray(); foreach (var x in skipped) j.Value(x); j.End();
            j.Key("untextured").BeginArray(); foreach (var x in untextured) j.Value(x); j.End();
            j.Key("glow").BeginArray(); foreach (var x in glows) j.Value(x); j.End();
            j.Key("partList").BeginArray(); foreach (var x in partInfo) j.Value(x); j.End();
            j.Key("resized").BeginArray(); foreach (var x in partFix) j.Value(x); j.End();
            j.Key("effects").BeginArray(); foreach (var x in effects) j.Raw(x); j.End();
            j.Key("weaponGlow").BeginArray(); foreach (var x in weaponGlow) j.Raw(x); j.End();
            j.End();
            File.WriteAllText(InfoPath(id), j.ToString());
            if (MuninConfig.DEBUG) ZLog.Log($"MuninMap: portrait {id}: {parts} parts in {sw.ElapsedMilliseconds} ms, {missing.Count} textures and {meshes.Count} meshes to fetch, {skipped.Count} left out, {untextured.Count} untextured");
        }

        // The glow some weapons carry (the storm, blood and nature ones, some staffs): a copy of the
        // weapon's shape a little bigger, drawn by the game's WeaponGlow shader as moving noise in a
        // bright colour, adding light. Named here for the page to draw that way: its colour and the noise.
        private static void WeaponGlow(GlbWriter.Primitive prim, Material mat, List<string> outList, List<string> wantMats)
        {
            if (mat == null) return;
            string shader = ""; try { shader = mat.shader != null ? mat.shader.name : ""; } catch { }
            if (shader != "Unlit/WeaponGlow" && !(PrefabExporter.Has(mat, "_ScrollSpeed") && PrefabExporter.Has(mat, "_CurlTex"))) return;
            var c = Saved(mat, "_Color", wantMats) ?? new Color(0.5f, 0.8f, 1f, 1f);
            prim.name = "munin_weaponglow" + outList.Count;
            float Num(string prop, float dflt) { var v = Saved(mat, "#" + prop, wantMats); return v.HasValue ? v.Value.r : dflt; }
            var j = new JsonWriter(200);
            j.BeginObject().Prop("name", prim.name).Key("color").BeginArray().Value(c.r, 3).Value(c.g, 3).Value(c.b, 3).Value(c.a, 3).End()
                .Prop("scroll", Num("_ScrollSpeed", 0.13f), 3).Prop("scale", Num("_TexScale", 1f), 3).Prop("push", Num("_VertPush", 0.004f), 4)
                .Prop("pixel", Num("_PixelSize", 64f), 1).Prop("mat", mat.name).End();
            outList.Add(j.ToString());
        }

        // any texture on it that isn't a normal, mask or style map (one the colour could come from)
        private static bool HasAnyColourMap(Material m)
        {
            try
            {
                foreach (var p in m.GetTexturePropertyNames())
                {
                    string l = p.ToLowerInvariant();
                    if (l.Contains("bump") || l.Contains("normal") || l.Contains("nrm") || l.Contains("style") || l.Contains("noise") || l.Contains("mask") || l.Contains("detail") || l.Contains("metal") || l.Contains("emis") || l.Contains("occl") || l.Contains("parallax")) continue;
                    if (m.GetTexture(p) != null) return true;
                }
            }
            catch { }
            return false;
        }

        // a material's texture slots and what's in them, for the report: " [shader: _MainTex=x, _BumpMap=-]"
        private static string TexSlots(Material m)
        {
            try
            {
                var l = new List<string>();
                foreach (var p in m.GetTexturePropertyNames()) { var t = m.GetTexture(p); l.Add(p + "=" + (t != null ? t.name : "-")); }
                return " [" + (m.shader != null ? m.shader.name : "?") + ": " + string.Join(", ", l) + "]";
            }
            catch { return ""; }
        }

        private static string TexName(Material m, string prop)
        {
            try { if (PrefabExporter.Has(m, prop)) { var t = m.GetTexture(prop); if (t != null && !string.IsNullOrEmpty(t.name)) return t.name; } } catch { }
            return null;
        }
    }
}
