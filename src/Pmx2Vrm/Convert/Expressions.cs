using System.Numerics;
using System.Text.Json.Nodes;
using Pmx2Vrm.Gltf;
using Pmx2Vrm.Pmx;

namespace Pmx2Vrm.Convert;

public sealed class MorphTarget
{
    public string Name = "";
    public int PmxMorph;
    public Vector3[] Delta = Array.Empty<Vector3>();
}

/// <summary>A PMX morph with group morphs expanded: its vertex offsets and material operations.</summary>
sealed class ExpandedMorph
{
    public readonly Dictionary<int, Vector3> Vertex = new(); // MMD space
    public readonly List<(int Material, byte Op, Vector4 Diffuse, float Weight)> Material = new();
    public bool HasBone, HasUv, HasOther;
}

sealed class ExpressionPlan
{
    /// <summary>VRM preset name -> PMX morph index.</summary>
    public readonly List<(string Preset, int Morph)> Presets = new();
    public readonly HashSet<int> AlphaAnimated = new();
    public readonly Dictionary<int, ExpandedMorph> Expanded = new();
    /// <summary>PMX morphs that become morph targets (in this order).</summary>
    public readonly List<int> TargetMorphs = new();
}

public sealed partial class Converter
{
    static readonly (string Preset, string[] Names)[] PresetNames =
    {
        ("aa", new[] { "あ", "a", "aa" }),
        ("ih", new[] { "い", "i", "ih" }),
        ("ou", new[] { "う", "u", "ou" }),
        ("ee", new[] { "え", "e", "ee" }),
        ("oh", new[] { "お", "o", "oh" }),
        ("blink", new[] { "まばたき", "瞬き", "blink" }),
        ("happy", new[] { "笑い", "笑顔", "smile", "happy", "joy" }),
        ("angry", new[] { "怒り", "angry", "anger" }),
        ("sad", new[] { "困る", "悲しい", "sad", "sorrow", "troubled" }),
        ("relaxed", new[] { "にこり", "にっこり", "なごみ", "relaxed", "fun" }),
        ("surprised", new[] { "びっくり", "驚き", "surprised", "surprise" }),
    };

    // Plain winks first (ウィンク２ is the "normal" closed eye; ウィンク is the smiling wink).
    static readonly string[] WinkLeftNames = { "ウィンク2", "ウィンク", "wink2", "wink", "winkl", "wink2l", "blinkl", "blinkleft" };
    static readonly string[] WinkRightNames = { "ウィンク2右", "ウィンク右", "wink2r", "winkr", "wink2right", "blinkr", "blinkright" };

    ExpressionPlan AnalyzeExpressions()
    {
        var plan = new ExpressionPlan();
        var byName = new Dictionary<string, int>();
        var byEn = new Dictionary<string, int>();
        for (int i = 0; i < _m.Morphs.Count; i++)
        {
            byName.TryAdd(Names.Norm(_m.Morphs[i].Name), i);
            var en = Names.Norm(_m.Morphs[i].NameEn);
            if (en.Length > 0) byEn.TryAdd(en, i);
        }
        int Find(IEnumerable<string> names)
        {
            var list = names.Select(Names.Norm).ToList();
            foreach (var n in list) if (byName.TryGetValue(n, out var i)) return i;
            foreach (var n in list) if (byEn.TryGetValue(n, out var i)) return i;
            return -1;
        }
        foreach (var (preset, names) in PresetNames)
        {
            int i = Find(names);
            if (i >= 0) plan.Presets.Add((preset, i));
        }

        // Winks: pick the candidates, then check the side from the geometry (the model's left is +X).
        int wl = Find(WinkLeftNames), wr = Find(WinkRightNames);
        if (wl == wr) wr = -1;
        var winks = new List<int>();
        if (wl >= 0) winks.Add(wl);
        if (wr >= 0) winks.Add(wr);
        foreach (var w in winks)
        {
            float side = MorphSideX(w);
            if (float.IsNaN(side))
            {
                // No vertex motion to judge by: trust the name.
                plan.Presets.Add((w == wl ? "blinkLeft" : "blinkRight", w));
                continue;
            }
            string preset = side > 0 ? "blinkLeft" : "blinkRight";
            if (plan.Presets.Any(p => p.Preset == preset))
            {
                _r.Warnings.Add($"Morph '{_m.Morphs[w].Name}' closes the same eye as another wink; skipped.");
                continue;
            }
            if ((w == wl) != (preset == "blinkLeft"))
                _r.Warnings.Add($"Morph '{_m.Morphs[w].Name}' closes the model's {(side > 0 ? "left" : "right")} eye (judged from its vertices), used as {preset}.");
            plan.Presets.Add((preset, w));
        }

        var targetSet = new HashSet<int>();
        foreach (var (_, mi) in plan.Presets) Expand(plan, mi);
        if (_o.AllMorphs)
            for (int i = 0; i < _m.Morphs.Count; i++)
                if (_m.Morphs[i].Type is PmxMorphType.Vertex or PmxMorphType.Group) Expand(plan, i);
        foreach (var (mi, e) in plan.Expanded.OrderBy(k => k.Key))
        {
            if (e.Vertex.Count > 0 && (plan.Presets.Any(p => p.Morph == mi) || _o.AllMorphs)) plan.TargetMorphs.Add(mi);
            foreach (var mat in e.Material)
                if (plan.Presets.Any(p => p.Morph == mi) && mat.Op == 0 && mat.Diffuse.W < 0.999f)
                {
                    if (mat.Material < 0) for (int k = 0; k < _m.Materials.Count; k++) plan.AlphaAnimated.Add(k);
                    else plan.AlphaAnimated.Add(mat.Material);
                }
        }
        return plan;
    }

    ExpandedMorph Expand(ExpressionPlan plan, int morph)
    {
        if (plan.Expanded.TryGetValue(morph, out var done)) return done;
        var e = new ExpandedMorph();
        plan.Expanded[morph] = e;
        Accumulate(e, morph, 1, 0);
        return e;
    }

    void Accumulate(ExpandedMorph e, int morph, float weight, int depth)
    {
        if (morph < 0 || morph >= _m.Morphs.Count || depth > 8) return;
        var mo = _m.Morphs[morph];
        switch (mo.Type)
        {
            case PmxMorphType.Vertex:
                foreach (var (v, off) in mo.Vertex)
                {
                    if (v < 0 || v >= _pos.Length) continue;
                    e.Vertex[v] = (e.Vertex.TryGetValue(v, out var cur) ? cur : Vector3.Zero) + off * weight;
                }
                break;
            case PmxMorphType.Group:
                foreach (var (sub, w) in mo.Group)
                    if (sub != morph) Accumulate(e, sub, weight * w, depth + 1);
                break;
            case PmxMorphType.Material:
                foreach (var o in mo.Material) e.Material.Add((o.Material, o.Operation, o.Diffuse, weight));
                break;
            case PmxMorphType.Bone: e.HasBone = true; break;
            case PmxMorphType.Uv:
            case PmxMorphType.Uv1:
            case PmxMorphType.Uv2:
            case PmxMorphType.Uv3:
            case PmxMorphType.Uv4: e.HasUv = true; break;
            default: e.HasOther = true; break;
        }
    }

    /// <summary>Weighted mean X (glTF space, before the bake) of the vertices a morph moves; NaN if none.</summary>
    float MorphSideX(int morph)
    {
        var e = new ExpandedMorph();
        Accumulate(e, morph, 1, 0);
        double sum = 0, wsum = 0;
        foreach (var (v, off) in e.Vertex)
        {
            double w = off.Length();
            sum += _pos[v].X * w; wsum += w;
        }
        return wsum > 1e-9 ? (float)(sum / wsum) : float.NaN;
    }

    List<MorphTarget> BuildTargets(ExpressionPlan plan)
    {
        var list = new List<MorphTarget>();
        var names = new HashSet<string>();
        foreach (var mi in plan.TargetMorphs)
        {
            var e = plan.Expanded[mi];
            var delta = new Vector3[_pos.Length];
            bool any = false;
            foreach (var (v, off) in e.Vertex)
            {
                var d = BakeDelta(v, P(off));
                if (!float.IsFinite(d.X) || !float.IsFinite(d.Y) || !float.IsFinite(d.Z)) continue;
                delta[v] = d;
                any |= d.LengthSquared() > 0;
            }
            if (!any) continue;
            var n = _m.Morphs[mi].Name.Length > 0 ? _m.Morphs[mi].Name : $"morph{mi}";
            var u = n;
            for (int k = 2; !names.Add(u); k++) u = $"{n}_{k}";
            list.Add(new MorphTarget { Name = u, PmxMorph = mi, Delta = delta });
        }
        return list;
    }

    JsonObject BuildExpressions(ExpressionPlan plan, List<MorphTarget> targets, MaterialInfo[] mats, int meshNode)
    {
        var preset = new JsonObject();
        foreach (var (name, mi) in plan.Presets)
        {
            if (preset.ContainsKey(name)) continue;
            var e = plan.Expanded[mi];
            var binds = new JsonArray();
            int t = targets.FindIndex(x => x.PmxMorph == mi);
            if (t >= 0) binds.Add(new JsonObject { ["node"] = meshNode, ["index"] = t, ["weight"] = 1.0 });

            // Material morphs: the color they reach at full weight, applied in order.
            var colors = new Dictionary<int, Vector4>();
            foreach (var (mat, op, diff, w) in e.Material)
            {
                var mlist = mat < 0 ? Enumerable.Range(0, _m.Materials.Count) : new[] { mat };
                foreach (var k in mlist)
                {
                    if (k < 0 || k >= mats.Length) continue;
                    var c = colors.TryGetValue(k, out var cur) ? cur : mats[k].BaseColor;
                    c = op == 0 ? c * Vector4.Lerp(Vector4.One, diff, w) : c + diff * w;
                    colors[k] = Vector4.Clamp(c, Vector4.Zero, Vector4.One);
                }
            }
            var colorBinds = new JsonArray();
            foreach (var (k, c) in colors.OrderBy(x => x.Key))
            {
                if (Vector4.DistanceSquared(c, mats[k].BaseColor) < 1e-8f) continue;
                colorBinds.Add(new JsonObject { ["material"] = mats[k].Gltf, ["type"] = "color", ["targetValue"] = GltfBuilder.Arr(c.X, c.Y, c.Z, c.W) });
            }

            var label = $"{name} ({_m.Morphs[mi].Name})";
            if (e.HasBone) _r.Warnings.Add($"Expression {label}: bone morph parts skipped (VRM expressions cannot move bones).");
            if (e.HasUv) _r.Warnings.Add($"Expression {label}: UV morph parts skipped.");
            if (e.HasOther) _r.Warnings.Add($"Expression {label}: flip/impulse morph parts skipped.");
            if (binds.Count == 0 && colorBinds.Count == 0)
            {
                _r.Warnings.Add($"Expression {label}: nothing convertible; left out.");
                continue;
            }
            var ex = new JsonObject
            {
                ["isBinary"] = false,
                ["overrideBlink"] = "none", ["overrideLookAt"] = "none", ["overrideMouth"] = "none",
            };
            if (binds.Count > 0) ex["morphTargetBinds"] = binds;
            if (colorBinds.Count > 0) ex["materialColorBinds"] = colorBinds;
            // Emotions close or change the eyes themselves: blinking on top would double it.
            if (name is "happy" or "angry" or "sad" or "relaxed" or "surprised") ex["overrideBlink"] = "blend";
            preset[name] = ex;
            _r.Expressions.Add(label);
        }
        var result = new JsonObject { ["preset"] = preset };

        // Every other converted morph target is reachable as a custom expression.
        var custom = new JsonObject();
        for (int t = 0; t < targets.Count; t++)
        {
            if (plan.Presets.Any(p => p.Morph == targets[t].PmxMorph)) continue;
            var key = targets[t].Name;
            if (custom.ContainsKey(key)) continue;
            custom[key] = new JsonObject
            {
                ["isBinary"] = false, ["overrideBlink"] = "none", ["overrideLookAt"] = "none", ["overrideMouth"] = "none",
                ["morphTargetBinds"] = new JsonArray(new JsonObject { ["node"] = meshNode, ["index"] = t, ["weight"] = 1.0 }),
            };
        }
        if (custom.Count > 0) result["custom"] = custom;
        return result;
    }
}
