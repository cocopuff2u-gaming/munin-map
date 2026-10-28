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
        }

        private static readonly Dictionary<string, Spec> specs = new Dictionary<string, Spec>();
        private static readonly Queue<string> queue = new Queue<string>();
        private static readonly HashSet<string> queued = new HashSet<string>();
        private static readonly Dictionary<string, List<string>> missingTex = new Dictionary<string, List<string>>();   // id -> textures it was made without
        private static volatile bool texDone;
        private static readonly object sync = new object();

        private static readonly string[] Slots = { "helmet", "chest", "legs", "shoulder", "utility", "trinket", "right", "left" };

        // Main thread: the portrait id for this look and gear (remembered, so it can be made when asked)
        public static string Id(Players.Look look, Func<string, string> gearOf)
        {
            var sb = new StringBuilder("p1|");
            sb.Append(look.model).Append('|').Append(Players.Hex(look.skin)).Append('|').Append(Players.Hex(look.hair)).Append('|').Append(look.hairItem).Append('|').Append(look.beardItem);
            var spec = new Spec { model = look.model, skin = look.skin, hair = look.hair, hairItem = look.hairItem, beardItem = look.beardItem };
            foreach (var slot in Slots)
            {
                string item = gearOf(slot) ?? "";
                sb.Append('|').Append(item);
                if (item.Length > 0) spec.gear[slot] = item;
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
                if (texDone)
                {
                    // textures came in: make again the portraits that went without them
                    texDone = false;
                    var again = new List<string>();
                    lock (sync) foreach (var kv in missingTex) foreach (var t in kv.Value) if (HasHd(t)) { again.Add(kv.Key); break; }
                    foreach (var a in again) { try { File.Delete(InfoPath(a)); } catch { } lock (sync) missingTex.Remove(a); }
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
                ve.SetHairItem(string.IsNullOrEmpty(spec.hairItem) ? 0 : spec.hairItem.GetStableHashCode());
                ve.SetBeardItem(string.IsNullOrEmpty(spec.beardItem) ? 0 : spec.beardItem.GetStableHashCode());
                ve.SetHelmetItem(H("helmet")); ve.SetChestItem(H("chest")); ve.SetLegItem(H("legs"));
                ve.SetShoulderItem(H("shoulder"), 0, 1); ve.SetUtilityItem(H("utility"));
                ve.SetRightItem(H("right"), 1); ve.SetLeftItem(H("left"), 0, 1);
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
                    if (!r.enabled || !(r is SkinnedMeshRenderer || r is MeshRenderer)) continue;
                    Mesh mesh; Matrix4x4 m;
                    if (r is SkinnedMeshRenderer smr)
                    {
                        if (smr.sharedMesh == null) continue;
                        mesh = new Mesh();
                        try { smr.BakeMesh(mesh); } catch { continue; }
                        m = toRoot * Matrix4x4.TRS(r.transform.position, r.transform.rotation, Vector3.one);
                    }
                    else
                    {
                        var mf = r.GetComponent<MeshFilter>();
                        mesh = mf != null ? mf.sharedMesh : null;
                        if (mesh == null) continue;
                        bool readable = false; try { readable = mesh.isReadable; } catch { }
                        if (!readable) continue;
                        m = toRoot * r.transform.localToWorldMatrix;
                    }
                    Vector3[] v; Vector3[] n; Vector2[] uv;
                    try { v = mesh.vertices; n = mesh.normals; uv = mesh.uv; } catch { continue; }
                    if (v == null || v.Length == 0) continue;
                    // a part that came out far from the body (bound to the wrong bones) is left out
                    if (haveCentre) { var c = m.MultiplyPoint3x4(v[0]); if ((c - centre).sqrMagnitude > 9f) continue; }
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
                    for (int s = 0; s < mesh.subMeshCount; s++)
                    {
                        int[] tri;
                        try { tri = mesh.GetTriangles(s); } catch { continue; }
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
                        string main = mat != null ? TexName(mat, "_MainTex") : null;
                        if (main != null) { if (!res.wants.Contains(main)) res.wants.Add(main); prim.textureUri = Tex(main) ?? prim.textureUri; }
                        prim.foliage = false; prim.alphaMask = false;
                        if (r == body && s == 0 && mat != null)
                        {
                            // the body's skin: the page mixes in the armour painted on it
                            prim.name = "munin_body";
                            // (the skin from the body model chosen: the material keeps the prefab's own until the game swaps it)
                            var bm = ve.m_models != null && ve.m_modelIndex < ve.m_models.Length ? ve.m_models[ve.m_modelIndex].m_baseMaterial : null;
                            skinTex = (bm != null ? TexName(bm, "_MainTex") : null) ?? TexName(mat, "_MainTex");
                            chestTex = TexName(mat, "_ChestTex"); legsTex = TexName(mat, "_LegsTex");
                            foreach (var t in new[] { skinTex, chestTex, legsTex }) if (t != null && !res.wants.Contains(t)) res.wants.Add(t);
                        }
                        else if (mat != null && (mat.name.StartsWith("PlayerHair", StringComparison.Ordinal) || r.name.StartsWith("hair", StringComparison.OrdinalIgnoreCase) || r.name.StartsWith("beard", StringComparison.OrdinalIgnoreCase)))
                        {
                            prim.name = "munin_hair";   // tinted with their hair colour
                            prim.alphaMask = true;
                        }
                        else if (prim.textureUri != null) prim.alphaMask = true;   // (capes, fur trims: cut-outs)
                        writer.Add(prim);
                    }
                    parts++;
                }
            }
            finally { if (go != null) UnityEngine.Object.DestroyImmediate(go); }
            if (writer.Count == 0) return;
            byte[] glb = writer.Write("portrait", out float[] bounds);
            File.WriteAllBytes(Path.Combine(Dir, GlbName(id)), glb);
            var missing = new List<string>();
            foreach (var w in res.wants) if (!HasHd(w)) missing.Add(w);
            lock (sync) { if (missing.Count > 0) missingTex[id] = missing; else missingTex.Remove(id); }
            string F(string t) => Tex(t) ?? "";
            var j = new JsonWriter(512);
            j.BeginObject().Prop("glb", GlbName(id)).Prop("skinColor", Players.Hex(spec.skin)).Prop("hairColor", Players.Hex(spec.hair));
            j.Key("body").BeginObject().Prop("skin", F(skinTex)).Prop("chest", F(chestTex)).Prop("legs", F(legsTex)).End();
            j.Key("bounds").BeginArray(); foreach (var b in bounds) j.Value(b, 3); j.End();
            j.Prop("textures", missing.Count == 0).End();
            File.WriteAllText(InfoPath(id), j.ToString());
            if (MuninConfig.DEBUG) ZLog.Log($"MuninMap: portrait {id}: {parts} parts in {sw.ElapsedMilliseconds} ms, {missing.Count} textures to fetch");
        }

        private static string TexName(Material m, string prop)
        {
            try { if (m.HasProperty(prop)) { var t = m.GetTexture(prop); if (t != null && !string.IsNullOrEmpty(t.name)) return t.name; } } catch { }
            return null;
        }
    }
}
