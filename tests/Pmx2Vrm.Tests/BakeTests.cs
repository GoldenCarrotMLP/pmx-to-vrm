using System.Numerics;
using System.Text.Json.Nodes;
using Pmx2Vrm.Convert;
using Pmx2Vrm.Pmx;

namespace Pmx2Vrm.Tests;

public static class BakeTests
{
    /// <summary>The synthetic model plus Tda-style AutoLuminous morphs: an add material morph and a vertex morph.</summary>
    static PmxModel WithAutoLuminous()
    {
        var m = SyntheticModel.Build();
        m.Materials[1].Diffuse = new Vector4(0.1f, 0.2f, 0.3f, 1); // hair: an "AL" color, made whole by AL未使用
        m.Morphs.Add(new PmxMorph
        {
            Name = "AL未使用", Panel = 4, Type = PmxMorphType.Material,
            Material = { new PmxMaterialOffset { Material = 1, Operation = 1, Diffuse = new Vector4(0.5f, 0.5f, 0.5f, 0) } },
        });
        m.Morphs.Add(new PmxMorph { Name = "LightOff", Panel = 4, Type = PmxMorphType.Vertex, Vertex = { (0, new Vector3(0, 100, 0)) } });
        return m;
    }

    static (Glb, ConvertReport) Run(PmxModel m, ConvertOptions o)
    {
        var (bytes, rep) = Converter.Convert(m, Path.GetTempPath(), o);
        var g = Glb.Parse(bytes);
        g.CheckStructure();
        return (g, rep);
    }

    static float[] HairBase(Glb g) => ((JsonArray)g.Arr("materials")[1]!["pbrMetallicRoughness"]!["baseColorFactor"]!).Select(x => x!.GetValue<float>()).ToArray();

    public static void TestAutoLuminousNoteAndFlag()
    {
        var (g, rep) = Run(WithAutoLuminous(), new ConvertOptions());
        Assert.True(rep.Notes.Any(n => n.Contains("--no-autoluminous")), "AutoLuminous model is reported");
        Assert.True(rep.Baked.Count == 0, "nothing baked without the flag");
        Assert.Near(0.1 * 0.6 + 0.1, HairBase(g)[0], 1e-3, "hair keeps its AL color (diffuse 0.1, ambient 0.1)");

        var (g2, rep2) = Run(WithAutoLuminous(), new ConvertOptions { NoAutoLuminous = true });
        Assert.Equal(2, rep2.Baked.Count, "both AL morphs baked");
        Assert.True(rep2.Notes.Count == 0, "no note once baked");
        // diffuse 0.1 + 0.5 = 0.6 -> base = max(0.6*0.6 + ambient 0.1, 0.6*0.8) = 0.48
        Assert.Near(0.48, HairBase(g2)[0], 1e-3, "add material morph baked into the diffuse");
        var pos = g2.Arr("accessors")[g2.Arr("meshes")[0]!["primitives"]![0]!["attributes"]!["POSITION"]!.GetValue<int>()]!;
        Assert.True(pos["max"]![1]!.GetValue<double>() > 7, "LightOff vertex offset baked into the positions");
    }

    public static void TestBakeMorphWithWeightAndMultiply()
    {
        var m = SyntheticModel.Build(); // 照れ: multiply body diffuse by (1, 0.8, 0.8)
        m.Materials[0].Ambient = new Vector3(0.1f);
        var (g, rep) = Run(m, new ConvertOptions { BakeMorphs = { ("照れ", 0.5f), ("no such morph", 1) } });
        var body = ((JsonArray)g.Arr("materials")[0]!["pbrMetallicRoughness"]!["baseColorFactor"]!).Select(x => x!.GetValue<float>()).ToArray();
        // diffuse G = lerp(1, 0.8, 0.5) = 0.9 -> base = max(0.9*0.6 + 0.1, 0.9*0.8) = 0.72; red: max(0.7, 0.8) = 0.8
        Assert.Near(0.72, body[1], 1e-3, "multiply morph baked at half weight");
        Assert.Near(0.8, body[0], 1e-3, "red untouched");
        Assert.True(rep.Warnings.Any(w => w.Contains("no such morph")), "unknown morph reported");
    }

    public static void TestCollidersOnPlainLegMoveToDBones()
    {
        var m = SyntheticModel.Build();
        m.RigidBodies[1].Bone = 19; // the left leg body on 左足 (the humanoid uses 左足D)
        var (g, rep) = Run(m, new ConvertOptions());
        var sb = g.Json["extensions"]!["VRMC_springBone"]!;
        var colliders = (JsonArray)sb["colliders"]!;
        Assert.True(colliders.Any(c => c!["node"]!.GetValue<int>() == 30), "collider hung on 左足D");
        Assert.True(!colliders.Any(c => c!["node"]!.GetValue<int>() == 19), "not on the unused 左足");
        var groups = ((JsonArray)sb["colliderGroups"]!).Select(x => x!["name"]!.GetValue<string>()).ToList();
        Assert.True(groups.Contains("LeftLeg"), "LeftLeg group: " + string.Join(",", groups));
    }

    /// <summary>An add material morph as an expression: targetValue is the color at weight 1, the material keeps its own.</summary>
    public static void TestAddMaterialMorphExpression()
    {
        var m = SyntheticModel.Build();
        m.Morphs.Add(new PmxMorph
        {
            Name = "びっくり", Panel = 4, Type = PmxMorphType.Material,
            Material = { new PmxMaterialOffset { Material = 1, Operation = 1, Diffuse = new Vector4(0.2f, 0, 0, 0) } },
        });
        var (g, _) = Run(m, new ConvertOptions());
        var bind = g.Json["extensions"]!["VRMC_vrm"]!["expressions"]!["preset"]!["surprised"]!["materialColorBinds"]![0]!;
        Assert.Equal(1, bind["material"]!.GetValue<int>(), "bound to the hair material");
        // hair diffuse R 0.2, ambient R 0.1: base = max(0.12 + 0.1, 0.16) = 0.22; +0.2 -> max(0.24 + 0.1, 0.32) = 0.34
        Assert.Near(0.34, bind["targetValue"]![0]!.GetValue<double>(), 1e-3, "target = color at weight 1");
        Assert.Near(0.22, HairBase(g)[0], 1e-3, "the material itself (weight 0) keeps its color");
        Assert.Near(0.9, bind["targetValue"]![3]!.GetValue<double>(), 1e-3, "alpha unchanged by an add of 0");
    }
}
