using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using MuninMap.Util;

namespace MuninMap.Models
{
    // A particle system as the game sets it up, for the page's player (web/js/portrait.js) to run the same
    // way: every module an item's effect uses (shape, speeds and sizes with their curves, colour over life,
    // orbit and pull, noise, forces, flipbook, trails and ribbons, stretched sparks) and how its material
    // blends. Curves and gradients are sampled at 9 points; positions and directions are in the portrait's
    // frame (x, y, -z), the system's own axes given as a basis the page turns its local values with.
    internal static class ParticleExport
    {
        private const int Samples = 9;
        private static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        // a number that may vary: a constant, [min, max], {"k": samples}, or {"k", "k2"} (random between two curves)
        private static void Curve(JsonWriter j, string key, ParticleSystem.MinMaxCurve c, float scale = 1f)
        {
            j.Key(key);
            try
            {
                switch (c.mode)
                {
                    case ParticleSystemCurveMode.Constant: j.Value(c.constant * scale, 4); return;
                    case ParticleSystemCurveMode.TwoConstants: j.BeginArray().Value(c.constantMin * scale, 4).Value(c.constantMax * scale, 4).End(); return;
                    case ParticleSystemCurveMode.Curve:
                        j.BeginObject(); Keys(j, "k", c.curve, c.curveMultiplier * scale); j.End(); return;
                    default:
                        j.BeginObject(); Keys(j, "k", c.curveMin, c.curveMultiplier * scale); Keys(j, "k2", c.curveMax, c.curveMultiplier * scale); j.End(); return;
                }
            }
            catch { j.Value(0f, 1); }
        }
        private static void Keys(JsonWriter j, string key, AnimationCurve a, float m)
        {
            j.Key(key).BeginArray();
            for (int i = 0; i < Samples; i++) j.Value((a != null ? a.Evaluate(i / (float)(Samples - 1)) : 0f) * m, 4);
            j.End();
        }
        private static bool Varies(ParticleSystem.MinMaxCurve c) { try { return !(c.mode == ParticleSystemCurveMode.Constant && Mathf.Abs(c.constant) < 1e-5f); } catch { return false; } }

        // a colour that may vary: [r,g,b,a], {"a","b"} (random between two), {"g": samples}, {"g","g2"}, {"g", "rnd": true}
        private static void Colour(JsonWriter j, string key, ParticleSystem.MinMaxGradient g)
        {
            j.Key(key);
            try
            {
                switch (g.mode)
                {
                    case ParticleSystemGradientMode.Color: C(j, g.color); return;
                    case ParticleSystemGradientMode.TwoColors: j.BeginObject().Key("a"); C(j, g.colorMin); j.Key("b"); C(j, g.colorMax); j.End(); return;
                    case ParticleSystemGradientMode.Gradient: j.BeginObject(); Stops(j, "g", g.gradient); j.End(); return;
                    case ParticleSystemGradientMode.TwoGradients: j.BeginObject(); Stops(j, "g", g.gradientMin); Stops(j, "g2", g.gradientMax); j.End(); return;
                    default: j.BeginObject(); Stops(j, "g", g.gradient); j.Prop("rnd", true).End(); return;
                }
            }
            catch { C(j, Color.white); }
        }
        private static void Stops(JsonWriter j, string key, Gradient g)
        {
            j.Key(key).BeginArray();
            for (int i = 0; i < Samples; i++) C(j, g != null ? g.Evaluate(i / (float)(Samples - 1)) : Color.white);
            j.End();
        }
        private static void C(JsonWriter j, Color c) => j.BeginArray().Value(c.r, 4).Value(c.g, 4).Value(c.b, 4).Value(c.a, 4).End();
        private static void V(JsonWriter j, string key, Vector3 v) => j.Key(key).BeginArray().Value(v.x, 4).Value(v.y, 4).Value(-v.z, 4).End();
        private static void V3(JsonWriter j, string key, Vector3 v) => j.Key(key).BeginArray().Value(v.x, 4).Value(v.y, 4).Value(v.z, 4).End();

        // how a material blends: "add" (light adding: fire, sparks, magic), "alpha" (covering: smoke, blood),
        // "mul". From the material's own saved settings (read from the game files: the engine answers 0 on a
        // dedicated server), the particle shaders' blend mode first; by names when there is nothing to go by.
        internal static string Blend(Material mat, Func<Material, string, Color?> saved, string names)
        {
            string shader = ""; try { shader = mat != null && mat.shader != null ? mat.shader.name.ToLowerInvariant() : ""; } catch { }
            float? Num(string p) { var c = mat != null ? saved(mat, "#" + p) : null; return c.HasValue ? c.Value.r : (float?)null; }
            if (shader.Contains("particles/standard"))
            {
                var mode = Num("_Mode");
                if (mode.HasValue)
                {
                    if (mode.Value > 3.5f && mode.Value < 4.5f) return "add";
                    if (mode.Value > 5.5f) return "mul";
                    return "alpha";
                }
            }
            var src = Num("_SrcBlend"); var dst = Num("_DstBlend");
            if (src.HasValue && dst.HasValue && !shader.Contains("lux"))
            {
                if (Mathf.Approximately(dst.Value, 1f)) return "add";                                       // (anything, One)
                if (Mathf.Approximately(dst.Value, 10f)) return Mathf.Approximately(src.Value, 1f) ? "premul" : "alpha";   // (OneMinusSrcAlpha)
                if (Mathf.Approximately(src.Value, 2f) || Mathf.Approximately(src.Value, 4f)) return "mul";   // (DstColor)
            }
            if (shader.Contains("additive") || shader.Contains("add")) return "add";
            string w = (shader + " " + names).ToLowerInvariant();
            bool covers = w.Contains("smoke") || w.Contains("dust") || w.Contains("cloud") || w.Contains("blood") || w.Contains("poison")
                || w.Contains("ooz") || w.Contains("drip") || w.Contains("splat") || w.Contains("steam") || w.Contains("lux");
            return covers ? "alpha" : "add";
        }

        // One particle system. tex(material) gives the page's file for the material's picture (and asks for it).
        internal static string System(ParticleSystem ps, Matrix4x4 toRoot, Func<Material, string> tex, Func<Material, string, Color?> saved, Func<Mesh, MeshCache.Data> meshOf = null)
        {
            var main = ps.main; var em = ps.emission; var sh = ps.shape;
            var pr = ps.GetComponent<ParticleSystemRenderer>();
            if (pr == null || !pr.enabled) return null;
            var tr = ps.trails;
            bool trailed = false; try { trailed = tr.enabled; } catch { }
            bool none = pr.renderMode == ParticleSystemRenderMode.None;
            if (none && !trailed) return null;
            float rate = 0f; try { rate = em.enabled ? (em.rateOverTime.mode == ParticleSystemCurveMode.TwoConstants ? em.rateOverTime.constantMax : em.rateOverTime.constant) : 0f; } catch { }
            int bursts = 0; try { bursts = em.enabled ? em.burstCount : 0; } catch { }
            if (rate <= 0f && bursts == 0 && !(em.enabled && em.rateOverTime.mode != ParticleSystemCurveMode.Constant)) return null;

            // its frame: where it is, its axes (the page turns local directions with them) and its scale
            float scale = 1f;
            try
            {
                if (main.scalingMode == ParticleSystemScalingMode.Hierarchy) scale = Mathf.Abs(ps.transform.lossyScale.x);
                else if (main.scalingMode == ParticleSystemScalingMode.Local) scale = Mathf.Abs(ps.transform.localScale.x);
            }
            catch { }
            if (!(scale > 1e-4f)) scale = 1f;
            Vector3 o = toRoot.MultiplyPoint3x4(ps.transform.position);
            Vector3 bx = toRoot.MultiplyVector(ps.transform.right).normalized, by = toRoot.MultiplyVector(ps.transform.up).normalized, bz = toRoot.MultiplyVector(ps.transform.forward).normalized;

            var j = new JsonWriter(2048);
            j.BeginObject().Prop("kind", "ps").Prop("name", ps.name);
            V(j, "pos", o); j.Key("basis").BeginArray(); foreach (var b in new[] { bx, by, bz }) j.BeginArray().Value(b.x, 4).Value(b.y, 4).Value(-b.z, 4).End(); j.End();
            j.Prop("scale", scale, 4).Prop("loop", main.loop).Prop("duration", main.duration, 3).Prop("max", Math.Min(main.maxParticles, 400));
            j.Prop("world", main.simulationSpace == ParticleSystemSimulationSpace.World);
            Curve(j, "life", main.startLifetime); Curve(j, "speed", main.startSpeed, scale); Curve(j, "size", main.startSize, scale);
            Curve(j, "rot", main.startRotation); Colour(j, "color", main.startColor); Curve(j, "gravity", main.gravityModifier);
            try { if (main.flipRotation != 0f) j.Prop("flip", main.flipRotation, 2); } catch { }

            // emission
            Curve(j, "rate", em.enabled ? em.rateOverTime : new ParticleSystem.MinMaxCurve(0f));
            if (bursts > 0)
            {
                j.Key("bursts").BeginArray();
                for (int b = 0; b < bursts; b++)
                {
                    var bu = em.GetBurst(b);
                    float n = bu.count.mode == ParticleSystemCurveMode.TwoConstants ? (bu.count.constantMin + bu.count.constantMax) * 0.5f : bu.count.constant;
                    j.BeginArray().Value(bu.time, 3).Value(n, 2).Value(bu.cycleCount).Value(bu.repeatInterval, 3).Value(bu.probability, 2).End();
                }
                j.End();
            }

            // the shape they're born in (in its own frame: offset, turned and scaled within the system)
            if (sh.enabled)
            {
                j.Key("shape").BeginObject().Prop("type", sh.shapeType.ToString()).Prop("radius", sh.radius * scale, 4).Prop("thick", sh.radiusThickness, 3)
                    .Prop("angle", sh.angle, 2).Prop("arc", sh.arc, 1).Prop("length", sh.length * scale, 4).Prop("donut", sh.donutRadius * scale, 4)
                    .Prop("randomDir", sh.randomDirectionAmount, 3).Prop("spherize", sh.sphericalDirectionAmount, 3).Prop("align", sh.alignToDirection);
                V3(j, "offset", sh.position * scale); V3(j, "rotation", sh.rotation); V3(j, "box", sh.scale * scale);
                try { j.Key("boxThick"); V3Raw(j, sh.boxThickness); } catch { j.Raw("[0,0,0]"); }
                // a mesh to emit from: a sample of its points (and their normals), in the shape's frame
                Mesh mesh = null;
                try
                {
                    if (sh.shapeType == ParticleSystemShapeType.Mesh) mesh = sh.mesh;
                    else if (sh.shapeType == ParticleSystemShapeType.MeshRenderer && sh.meshRenderer != null) mesh = sh.meshRenderer.GetComponent<MeshFilter>()?.sharedMesh;
                    else if (sh.shapeType == ParticleSystemShapeType.SkinnedMeshRenderer && sh.skinnedMeshRenderer != null) mesh = sh.skinnedMeshRenderer.sharedMesh;
                }
                catch { }
                if (mesh != null) MeshPoints(j, mesh, sh, ps.transform);
                j.End();
            }

            // over their life
            var vel = ps.velocityOverLifetime;
            if (vel.enabled)
            {
                j.Key("vel").BeginObject().Prop("world", vel.space == ParticleSystemSimulationSpace.World);
                Curve(j, "x", vel.x, scale); Curve(j, "y", vel.y, scale); Curve(j, "z", vel.z, scale);
                try
                {
                    if (Varies(vel.orbitalX) || Varies(vel.orbitalY) || Varies(vel.orbitalZ)) { Curve(j, "ox", vel.orbitalX); Curve(j, "oy", vel.orbitalY); Curve(j, "oz", vel.orbitalZ); V3(j, "oOffset", new Vector3(vel.orbitalOffsetX.constant, vel.orbitalOffsetY.constant, vel.orbitalOffsetZ.constant) * scale); }
                    if (Varies(vel.radial)) Curve(j, "radial", vel.radial, scale);
                    Curve(j, "speedMod", vel.speedModifier);
                }
                catch { }
                j.End();
            }
            var lim = ps.limitVelocityOverLifetime;
            try { if (lim.enabled) { j.Key("limit").BeginObject(); Curve(j, "speed", lim.limit, scale); j.Prop("dampen", lim.dampen, 3); Curve(j, "drag", lim.drag); j.End(); } } catch { }
            var force = ps.forceOverLifetime;
            if (force.enabled) { j.Key("force").BeginObject().Prop("world", force.space == ParticleSystemSimulationSpace.World); Curve(j, "x", force.x, scale); Curve(j, "y", force.y, scale); Curve(j, "z", force.z, scale); j.End(); }
            var nz = ps.noise;
            if (nz.enabled)
            {
                j.Key("noise").BeginObject();
                Curve(j, "strength", nz.strength, scale);
                j.Prop("freq", nz.frequency / scale, 3).Prop("damping", nz.damping).Prop("octaves", nz.octaveCount).Prop("octMul", nz.octaveMultiplier, 3).Prop("octScale", nz.octaveScale, 3);
                Curve(j, "scroll", nz.scrollSpeed); Curve(j, "pos", nz.positionAmount);
                j.End();
            }
            var col = ps.colorOverLifetime;
            if (col.enabled) Colour(j, "colOL", col.color);
            var sol = ps.sizeOverLifetime;
            if (sol.enabled) Curve(j, "sizeOL", sol.size);
            var rol = ps.rotationOverLifetime;
            if (rol.enabled) Curve(j, "rotOL", rol.z);

            // a flipbook: which picture of the sheet, over their life
            var ts = ps.textureSheetAnimation;
            try
            {
                if (ts.enabled && ts.mode == ParticleSystemAnimationMode.Grid)
                {
                    j.Key("sheet").BeginObject().Prop("x", ts.numTilesX).Prop("y", ts.numTilesY).Prop("cycles", ts.cycleCount)
                        .Prop("row", ts.animation == ParticleSystemAnimationType.SingleRow ? (ts.rowMode == ParticleSystemAnimationRowMode.Random ? -1 : ts.rowIndex) : -2);
                    Curve(j, "frame", ts.frameOverTime); Curve(j, "start", ts.startFrame);
                    j.End();
                }
            }
            catch { }

            // trails: behind each particle, or ribbons joining the particles (lightning)
            if (trailed)
            {
                j.Key("trail").BeginObject().Prop("ribbon", tr.mode == ParticleSystemTrailMode.Ribbon).Prop("ribbons", Math.Max(1, tr.ribbonCount)).Prop("ratio", tr.ratio, 3)
                    .Prop("minDist", tr.minVertexDistance * scale, 4).Prop("stretch", tr.textureMode == ParticleSystemTrailTextureMode.Stretch)
                    .Prop("sizeWidth", tr.sizeAffectsWidth).Prop("sizeLife", tr.sizeAffectsLifetime).Prop("inherit", tr.inheritParticleColor).Prop("die", tr.dieWithParticles);
                Curve(j, "life", tr.lifetime); Curve(j, "width", tr.widthOverTrail); Colour(j, "colOT", tr.colorOverTrail); Colour(j, "colOL", tr.colorOverLifetime);
                var tm = pr.trailMaterial;
                if (tm != null) Material(j, "tmat", tm, tex, saved, ps.name);
                j.End();
            }

            // how it's drawn
            j.Key("render").BeginObject().Prop("mode", pr.renderMode.ToString()).Prop("lengthScale", pr.lengthScale, 3).Prop("velScale", pr.velocityScale, 3)
                .Prop("minSize", pr.minParticleSize, 3).Prop("maxSize", pr.maxParticleSize, 3).Prop("align", pr.alignment.ToString()).End();
            if (!none && pr.sharedMaterial != null) Material(j, "mat", pr.sharedMaterial, tex, saved, ps.name);
            // mesh particles (the blood splats): the mesh, in its own frame (x, y, z as Unity), each particle drawn
            // as it, sized and turned (the page: facing the viewer, as the alignment here is View)
            if (pr.renderMode == ParticleSystemRenderMode.Mesh && pr.mesh != null && meshOf != null)
            {
                var md = meshOf(pr.mesh);
                if (md != null && md.subMeshes.Count > 0 && md.positions.Length / 3 <= 2000)
                {
                    j.Key("pmesh").BeginObject().Key("p").BeginArray(); foreach (float f in md.positions) j.Value(f, 4); j.End();
                    if (md.uvs != null) { j.Key("uv").BeginArray(); foreach (float f in md.uvs) j.Value(f, 4); j.End(); }
                    j.Key("i").BeginArray(); foreach (var sm in md.subMeshes) foreach (uint i in sm) j.Value((int)i); j.End();
                    j.End();
                }
            }

            // gradient-mapped (most fire, frost and lightning): the picture is only light and dark, and the
            // colour is picked along a ramp from the second custom colour (dark) to the first (bright), both
            // changing over the particle's life: [bright at birth, bright at death, dark at birth, dark at death]
            try
            {
                var cd = ps.customData;
                if (cd.enabled && cd.GetMode(ParticleSystemCustomData.Custom1) == ParticleSystemCustomDataMode.Color
                    && cd.GetMode(ParticleSystemCustomData.Custom2) == ParticleSystemCustomDataMode.Color)
                {
                    j.Key("ramp").BeginObject(); Colour(j, "hi", cd.GetColor(ParticleSystemCustomData.Custom1)); Colour(j, "lo", cd.GetColor(ParticleSystemCustomData.Custom2)); j.End();
                }
            }
            catch { }
            Bake(ps, toRoot, j);
            j.End();
            return j.ToString();
        }

        // The game's own particles: the system run by the engine (as it runs in the game) after it has been going a
        // while, every particle read 30 times a second. The page plays these back, so the motion is the game's own
        // (its orbits, noise, ribbons, timing), not a copy of the rules. Made into a loop with no seam: the
        // particles born in one stretch of the emitter's run (its duration, or a multiple near two seconds) are
        // each followed through their whole life, and each comes round again a stretch later, so when the loop
        // starts over the older ones carry on and none appear or vanish all at once (as the game, which never
        // starts over). Each particle in a frame is 24 bytes (little-endian): id u16 (the same particle from frame
        // to frame; one coming round again is seedStep higher, looking back past the loop's start), x, y, z i16
        // (mm), size u16 (0.1 mm), turn i16 (mrad), r, g, b, a u8, velocity x, y, z i16 (cm/s), age u8 (0 born,
        // 255 gone), its own seed's low byte u8 (the same each time round: its ribbon and its random looks).
        internal const int BakeFps = 30, BakeBytes = 24, SeedStep = 40503;
        private sealed class Track { public int Born; public uint Seed; public readonly List<byte[]> Frames = new List<byte[]>(); }
        private static void Bake(ParticleSystem ps, Matrix4x4 toRoot, JsonWriter j)
        {
            try
            {
                var main = ps.main;
                int cap = Math.Min(main.maxParticles, 400);
                var buf = new ParticleSystem.Particle[cap];
                float life = 1f;
                try
                {
                    var sl = main.startLifetime;
                    life = sl.mode == ParticleSystemCurveMode.Constant ? sl.constant : sl.mode == ParticleSystemCurveMode.TwoConstants ? sl.constantMax : main.startLifetimeMultiplier;
                } catch { }
                float dur = main.duration;
                float span = dur >= 0.25f && dur <= 4f ? dur * Mathf.Max(1, Mathf.Round(2f / dur)) : 2f;
                int loop = Mathf.Clamp(Mathf.RoundToInt(span * BakeFps), 15, 150);
                int frames = loop + Mathf.Clamp(Mathf.CeilToInt(life * BakeFps) + 2, 2, 8 * BakeFps);
                float warm = Mathf.Clamp(Mathf.Max(dur, life) + 1f, 1f, 12f);
                ps.Simulate(warm, false, true, false);
                bool local = main.simulationSpace == ParticleSystemSimulationSpace.Local;
                var tm = ps.transform;
                short S16(float v) => (short)Mathf.Clamp(Mathf.Round(v), -32768f, 32767f);
                ushort U16(float v) => (ushort)Mathf.Clamp(Mathf.Round(v), 0f, 65535f);
                byte U8(float v) => (byte)Mathf.Clamp(Mathf.Round(v), 0f, 255f);
                // each particle born in the first stretch, frame by frame until it dies (or the recording ends)
                var already = new HashSet<uint>();
                var tracks = new Dictionary<uint, Track>();
                for (int f = 0; f <= frames; f++)
                {
                    ps.Simulate(1f / BakeFps, false, false, false);   // (not in fixed steps: those are the physics tick, 0.02 s, and 1/30 s became two of them)
                    int n = ps.GetParticles(buf);
                    for (int i = 0; i < n; i++)
                    {
                        var p = buf[i];
                        if (f == 0) { already.Add(p.randomSeed); continue; }
                        if (already.Contains(p.randomSeed)) continue;
                        if (!tracks.TryGetValue(p.randomSeed, out var t))
                        {
                            if (f > loop) continue;
                            tracks[p.randomSeed] = t = new Track { Born = f % loop, Seed = p.randomSeed };
                        }
                        Vector3 q = toRoot.MultiplyPoint3x4(local ? tm.TransformPoint(p.position) : p.position);
                        Vector3 qv = toRoot.MultiplyVector(local ? tm.TransformVector(p.totalVelocity) : p.totalVelocity);
                        Color32 c = p.GetCurrentColor(ps);
                        float age = p.startLifetime > 0f ? Mathf.Clamp01(1f - p.remainingLifetime / p.startLifetime) : 0f;
                        float rot = Mathf.Repeat(p.rotation, 360f) * Mathf.Deg2Rad;
                        var ms1 = new System.IO.MemoryStream(22);
                        var w1 = new System.IO.BinaryWriter(ms1);
                        w1.Write(S16(q.x * 1000f)); w1.Write(S16(q.y * 1000f)); w1.Write(S16(-q.z * 1000f));
                        w1.Write(U16(p.GetCurrentSize(ps) * 10000f)); w1.Write(S16(rot * 1000f));
                        w1.Write(c.r); w1.Write(c.g); w1.Write(c.b); w1.Write(c.a);
                        w1.Write(S16(qv.x * 100f)); w1.Write(S16(qv.y * 100f)); w1.Write(S16(-qv.z * 100f));
                        w1.Write(U8(age * 255f)); w1.Write((byte)(p.randomSeed & 255));
                        w1.Flush(); t.Frames.Add(ms1.ToArray());
                    }
                }
                if (tracks.Count == 0) return;   // (the engine ran nothing here: the page plays it from the settings instead)
                // the loop: in frame t, each particle as it is (t - born) frames after its birth, and again a stretch,
                // two, ... later while it lives; oldest first (the order the engine keeps them in, which ribbons follow)
                var counts = new int[loop];
                var ms = new System.IO.MemoryStream();
                var w = new System.IO.BinaryWriter(ms);
                var list = new List<KeyValuePair<int, Track>>();
                for (int t = 0; t < loop; t++)
                {
                    list.Clear();
                    foreach (var tr in tracks.Values)
                        for (int k = ((t - tr.Born) % loop + loop) % loop; k < tr.Frames.Count; k += loop) list.Add(new KeyValuePair<int, Track>(k, tr));
                    list.Sort((x, y) => y.Key.CompareTo(x.Key));
                    if (list.Count > cap) list.RemoveRange(cap, list.Count - cap);
                    counts[t] = list.Count;
                    foreach (var kv in list)
                    {
                        int round = (int)Math.Floor((t - kv.Key) / (double)loop);   // (which time round: one less after the loop starts over)
                        w.Write((ushort)((kv.Value.Seed + (uint)(round * SeedStep)) & 0xFFFF));
                        w.Write(kv.Value.Frames[kv.Key]);
                    }
                }
                w.Flush();
                j.Key("bake").BeginObject().Prop("fps", BakeFps).Prop("seamless", 1).Prop("seedStep", SeedStep);
                j.Key("counts").BeginArray(); foreach (var c in counts) j.Value(c); j.End();
                j.Prop("data", Convert.ToBase64String(ms.ToArray())).End();
            }
            catch (Exception e) { j.Prop("bakeError", e.Message); }
        }

        private static void V3Raw(JsonWriter j, Vector3 v) => j.BeginArray().Value(v.x, 4).Value(v.y, 4).Value(v.z, 4).End();

        private static void Material(JsonWriter j, string key, Material m, Func<Material, string> tex, Func<Material, string, Color?> saved, string names)
        {
            j.Key(key).BeginObject().Prop("name", m.name).Prop("blend", Blend(m, saved, names + " " + m.name));
            string t = tex(m); if (t != null) j.Prop("tex", t);
            // a gradient-mapped material: which channel of its picture picks the colour, and whether it is the see-through too
            var gc = saved(m, "#_GradientChannel"); var ga = saved(m, "#_GradientAsAlpha");
            if (gc.HasValue) j.Prop("gradChannel", (int)gc.Value.r);
            if (ga.HasValue) j.Prop("gradAlpha", ga.Value.r > 0.5f);
            var tint = saved(m, "_TintColor") ?? saved(m, "_Color");
            if (tint.HasValue) { j.Key("tint"); C(j, tint.Value); }
            try { var sc = m.mainTextureScale; var of = m.mainTextureOffset; if (sc != Vector2.one || of != Vector2.zero) j.Key("uv").BeginArray().Value(sc.x, 3).Value(sc.y, 3).Value(of.x, 3).Value(of.y, 3).End(); } catch { }
            j.End();
        }

        // up to 96 points of the mesh a shape emits from, in the system's frame (the shape's own offset and turn applied)
        private static void MeshPoints(JsonWriter j, Mesh mesh, ParticleSystem.ShapeModule sh, Transform t)
        {
            Vector3[] v = null, n = null;
            try { if (mesh.isReadable) { v = mesh.vertices; n = mesh.normals; } } catch { }
            j.Key("points").BeginArray();
            if (v != null && v.Length > 0)
            {
                var shapeM = Matrix4x4.TRS(sh.position, Quaternion.Euler(sh.rotation), sh.scale);
                int step = Math.Max(1, v.Length / 96);
                for (int i = 0; i < v.Length; i += step)
                {
                    var p = shapeM.MultiplyPoint3x4(v[i]); var q = n != null && n.Length == v.Length ? shapeM.MultiplyVector(n[i]).normalized : Vector3.up;
                    j.BeginArray().Value(p.x, 4).Value(p.y, 4).Value(p.z, 4).Value(q.x, 3).Value(q.y, 3).Value(q.z, 3).End();
                }
            }
            else
            {
                // (a locked mesh: its box)
                var b = mesh.bounds; var shapeM = Matrix4x4.TRS(sh.position, Quaternion.Euler(sh.rotation), sh.scale);
                for (int i = 0; i < 8; i++)
                {
                    var p = shapeM.MultiplyPoint3x4(b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                    j.BeginArray().Value(p.x, 4).Value(p.y, 4).Value(p.z, 4).Value(0f, 1).Value(1f, 1).Value(0f, 1).End();
                }
            }
            j.End();
        }
    }
}
