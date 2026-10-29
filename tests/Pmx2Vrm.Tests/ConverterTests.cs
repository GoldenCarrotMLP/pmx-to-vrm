using System.Text.Json.Nodes;
using Pmx2Vrm.Convert;
using Pmx2Vrm.Images;

namespace Pmx2Vrm.Tests;

public static class ConverterTests
{
    /// <summary>Writes the synthetic model, its texture and toon ramp into a fresh folder.</summary>
    public static string WriteSample(string? sub = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "pmx2vrm-tests", sub ?? Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "tex"));
        File.WriteAllBytes(Path.Combine(dir, "tex", "BODY.bmp"), SyntheticModel.Bmp24()); // different case on purpose
        File.WriteAllBytes(Path.Combine(dir, "toon_body.bmp"), SyntheticModel.Bmp24());
        File.WriteAllText(Path.Combine(dir, "readme.txt"), "利用規約: test terms");
        var path = Path.Combine(dir, "test.pmx");
        File.WriteAllBytes(path, PmxWriter.Write(SyntheticModel.Build()));
        return path;
    }

    static (Glb Glb, ConvertReport Report) Convert(ConvertOptions? o = null)
    {
        var (bytes, rep) = Converter.Convert(WriteSample(), o ?? new ConvertOptions());
        var glb = Glb.Parse(bytes);
        glb.CheckStructure();
        return (glb, rep);
    }

    static JsonObject Vrm(Glb g) => g.Json["extensions"]!["VRMC_vrm"]!.AsObject();
    static int Node(Glb g, string human) => Vrm(g)["humanoid"]!["humanBones"]![human]!["node"]!.GetValue<int>();

    static System.Numerics.Vector3 World(Glb g, int node)
    {
        var nodes = g.Arr("nodes");
        var parent = new int[nodes.Count];
        Array.Fill(parent, -1);
        for (int i = 0; i < nodes.Count; i++)
            if (nodes[i]!["children"] is JsonArray k) foreach (var c in k) parent[c!.GetValue<int>()] = i;
        var p = System.Numerics.Vector3.Zero;
        for (int n = node; n >= 0; n = parent[n])
            if (nodes[n]!["translation"] is JsonArray t) p += new System.Numerics.Vector3(t[0]!.GetValue<float>(), t[1]!.GetValue<float>(), t[2]!.GetValue<float>());
        return p;
    }

    public static void TestOutputIsGltfWithVrmExtensions()
    {
        var (g, rep) = Convert();
        var used = g.Arr("extensionsUsed").Select(x => x!.GetValue<string>()).ToList();
        Assert.True(used.Contains("VRMC_vrm") && used.Contains("VRMC_springBone") && used.Contains("VRMC_materials_mtoon"), "extensionsUsed");
        Assert.Equal("1.0", Vrm(g)["specVersion"]!.GetValue<string>(), "VRMC_vrm.specVersion");
        var meta = Vrm(g)["meta"]!;
        Assert.Equal("テストモデル", meta["name"]!.GetValue<string>(), "meta.name");
        Assert.Equal("テスト作者", meta["authors"]![0]!.GetValue<string>(), "author from the PMX comment");
        Assert.Equal("onlyAuthor", meta["avatarPermission"]!.GetValue<string>(), "avatarPermission");
        Assert.Equal("prohibited", meta["modification"]!.GetValue<string>(), "modification");
        Assert.Equal(false, meta["allowRedistribution"]!.GetValue<bool>(), "allowRedistribution");
        Assert.Equal(1, g.Arr("meshes").Count, "one mesh");
        Assert.Equal(2, ((JsonArray)g.Arr("meshes")[0]!["primitives"]!).Count, "a primitive per material");
        Console.WriteLine("    " + rep.ToString().Replace("\n", "\n    ").TrimEnd());
    }

    public static void TestHumanoid()
    {
        var (g, rep) = Convert();
        foreach (var b in Humanoid.Required) Assert.True(Vrm(g)["humanoid"]!["humanBones"]![b] != null, $"humanBones.{b}");
        Assert.True(rep.MissingRequired.Count == 0, "all required bones: missing " + string.Join(",", rep.MissingRequired));
        Assert.Equal(1, Node(g, "hips"), "hips = センター (common ancestor of spine and legs)");
        Assert.Equal(30, Node(g, "leftUpperLeg"), "left leg uses the D-bone");
        Assert.Equal(31, Node(g, "leftLowerLeg"), "left knee uses the D-bone");
        Assert.Equal(32, Node(g, "leftFoot"), "left foot uses the D-bone");
        Assert.Equal(23, Node(g, "rightUpperLeg"), "right leg (no D-bone)");
        Assert.True(Vrm(g)["humanoid"]!["humanBones"]!["leftToes"] == null, "つま先 under the non-D ankle is not the D foot's toes");
        Assert.Equal(26, Node(g, "rightToes"), "right toes");
        Assert.Equal(6, Node(g, "leftEye"), "left eye");
        Assert.Equal(12, Node(g, "leftIndexProximal"), "full-width digit finger name");
        foreach (var hb in Vrm(g)["humanoid"]!["humanBones"]!.AsObject())
            Assert.True(hb.Value!["node"]!.GetValue<int>() is not (SyntheticModel.Hair1 or SyntheticModel.Hair2), "physics bones are not human bones");
        // Facing +Z: the left arm is on +X, the eyes in front (+Z) of the head.
        Assert.True(World(g, Node(g, "leftUpperArm")).X > 0, "left arm on +X");
        Assert.True(World(g, Node(g, "leftEye")).Z > World(g, Node(g, "head")).Z, "eyes toward +Z");
        // Hips moved between the legs and the spine.
        var hips = World(g, Node(g, "hips"));
        Assert.True(hips.Y > World(g, Node(g, "leftUpperLeg")).Y && hips.Y < World(g, Node(g, "spine")).Y, $"hips between legs and spine (y={hips.Y})");
    }

    public static void TestTPoseAndGround()
    {
        var (g, rep) = Convert();
        foreach (var side in new[] { "left", "right" })
        {
            var a = World(g, Node(g, side + "UpperArm"));
            var b = World(g, Node(g, side + "LowerArm"));
            var c = World(g, Node(g, side + "Hand"));
            var d = System.Numerics.Vector3.Normalize(b - a);
            var e = System.Numerics.Vector3.Normalize(c - b);
            float sign = side == "left" ? 1 : -1;
            Assert.Near(sign, d.X, 1e-3, side + " upper arm along X");
            Assert.Near(sign, e.X, 1e-3, side + " lower arm along X");
        }
        var pos = g.Arr("accessors")[g.Arr("meshes")[0]!["primitives"]![0]!["attributes"]!["POSITION"]!.GetValue<int>()]!;
        Assert.Near(0, pos["min"]![1]!.GetValue<double>(), 1e-4, "feet on y=0");
        Assert.Near(19.75 * 0.08, pos["max"]![1]!.GetValue<double>(), 0.01, "height in metres (head top 19.5 + toe box 0.25 below 0)");
        // Skinning at rest is identity: each inverse bind matrix is the inverse of its joint's world translation.
        var skin = g.Arr("skins")[0]!;
        var ibm = g.Floats(skin["inverseBindMatrices"]!.GetValue<int>());
        var joints = (JsonArray)skin["joints"]!;
        for (int j = 0; j < joints.Count; j++)
        {
            var w = World(g, joints[j]!.GetValue<int>());
            Assert.Near(-w.X, ibm[j * 16 + 12], 1e-4, "ibm x"); Assert.Near(-w.Y, ibm[j * 16 + 13], 1e-4, "ibm y"); Assert.Near(-w.Z, ibm[j * 16 + 14], 1e-4, "ibm z");
            Assert.True(ibm[j * 16] == 1 && ibm[j * 16 + 5] == 1 && ibm[j * 16 + 10] == 1 && ibm[j * 16 + 15] == 1 && ibm[j * 16 + 1] == 0, "ibm has no rotation");
        }
        // No T-pose: arms stay down.
        var (g2, _) = Convert(new ConvertOptions { TPose = false });
        var a2 = World(g2, Node(g2, "leftUpperArm")); var b2 = World(g2, Node(g2, "leftLowerArm"));
        Assert.True(b2.Y < a2.Y - 0.05f, "--no-tpose keeps the A-pose");
    }

    public static void TestExpressions()
    {
        var (g, rep) = Convert();
        var preset = Vrm(g)["expressions"]!["preset"]!.AsObject();
        foreach (var name in new[] { "aa", "blink", "blinkLeft", "blinkRight", "happy" })
            Assert.True(preset.ContainsKey(name), $"expression {name}");
        var names = (JsonArray)g.Arr("meshes")[0]!["extras"]!["targetNames"]!;
        string Target(string expr) => names[preset[expr]!["morphTargetBinds"]![0]!["index"]!.GetValue<int>()]!.GetValue<string>();
        Assert.Equal("ウィンク２", Target("blinkLeft"), "blinkLeft = ウィンク２ (moves +X vertices)");
        Assert.Equal("ｳｨﾝｸ２右", Target("blinkRight"), "blinkRight = the half-width ｳｨﾝｸ２右");
        Assert.True(preset["happy"]!["materialColorBinds"] is JsonArray { Count: 1 }, "happy (group with a material morph) has a material color bind");
        Assert.True(rep.Warnings.Any(w => w.Contains("bone morph")), "bone morph part reported");
        // The morph target of the group morph is the expanded sum: blink*0.5 + a*0.3.
        int ti = preset["happy"]!["morphTargetBinds"]![0]!["index"]!.GetValue<int>();
        var prim = g.Arr("meshes")[0]!["primitives"]![0]!;
        var delta = g.Floats(prim["targets"]![ti]!["POSITION"]!.GetValue<int>());
        float minY = 0;
        for (int i = 1; i < delta.Length; i += 3) minY = MathF.Min(minY, delta[i]);
        Assert.Near(-0.2 * 0.5 * 0.08, minY, 1e-5, "group morph expanded with influences");
        Assert.True(Vrm(g)["lookAt"]!["type"]!.GetValue<string>() == "bone", "lookAt bone type");
        Assert.True(Vrm(g)["firstPerson"]!["meshAnnotations"] is JsonArray { Count: 1 }, "firstPerson annotation");
        // --all-morphs adds custom expressions.
        var (g2, _) = Convert(new ConvertOptions { AllMorphs = true });
        Assert.True(Vrm(g2)["expressions"]!["custom"] != null || ((JsonArray)g2.Arr("meshes")[0]!["extras"]!["targetNames"]!).Count >= names.Count, "--all-morphs");
    }

    public static void TestPhysics()
    {
        var (g, rep) = Convert();
        var sb = g.Json["extensions"]!["VRMC_springBone"]!;
        Assert.Equal("1.0", sb["specVersion"]!.GetValue<string>(), "springBone specVersion");
        var springs = (JsonArray)sb["springs"]!;
        Assert.Equal(1, springs.Count, "one spring chain (the hair)");
        var joints = (JsonArray)springs[0]!["joints"]!;
        var nodes = joints.Select(j => j!["node"]!.GetValue<int>()).ToArray();
        Assert.True(nodes.SequenceEqual(new[] { SyntheticModel.Hair1, SyntheticModel.Hair2, SyntheticModel.Hair3 }), "hair chain 髪1 -> 髪2 -> tail 髪3: " + string.Join(",", nodes));
        foreach (var j in joints)
        {
            float st = j!["stiffness"]!.GetValue<float>(), dr = j["dragForce"]!.GetValue<float>(), gp = j["gravityPower"]!.GetValue<float>(), hr = j["hitRadius"]!.GetValue<float>();
            Assert.True(st >= 0.4f && st <= 4 && dr >= 0.2f && dr <= 0.9f && gp >= 0 && gp <= 0.3f && hr > 0 && hr < 0.2f, $"joint parameters in range ({st}, {dr}, {gp}, {hr})");
        }
        Assert.Near(0.3 * 0.08, joints[0]!["hitRadius"]!.GetValue<double>(), 1e-4, "hit radius from the capsule radius");
        var colliders = (JsonArray)sb["colliders"]!;
        Assert.Equal(4, colliders.Count, "head, two legs, upper body");
        Assert.True(colliders.Any(c => c!["shape"]!["sphere"] != null) && colliders.Count(c => c!["shape"]!["capsule"] != null) == 3, "sphere + capsules (box as capsule)");
        var groups = ((JsonArray)sb["colliderGroups"]!).Select(x => x!["name"]!.GetValue<string>()).ToList();
        var used = ((JsonArray)springs[0]!["colliderGroups"]!).Select(x => groups[x!.GetValue<int>()]).ToList();
        Assert.True(used.Contains("Head") && used.Contains("Body") && !used.Any(u => u.Contains("Leg")), "hair collides with head + body but not legs (PMX masks): " + string.Join(",", used));
        // A leg collider capsule is vertical and in the leg's local space.
        var leg = colliders.First(c => c!["node"]!.GetValue<int>() == 30)!["shape"]!["capsule"]!;
        Assert.True(Math.Abs(leg["offset"]![1]!.GetValue<double>() - leg["tail"]![1]!.GetValue<double>()) > 0.2, "leg capsule along Y");
        var (g2, _) = Convert(new ConvertOptions { Physics = false });
        Assert.True(g2.Json["extensions"]!["VRMC_springBone"] == null, "--no-physics");
    }

    public static void TestTexturesEmbeddedAsPng()
    {
        var (g, rep) = Convert();
        Assert.Equal(1, g.Arr("images").Count, "the BMP texture is embedded (the toon ramp is not)");
        var img = g.Arr("images")[0]!;
        Assert.Equal("image/png", img["mimeType"]!.GetValue<string>(), "BMP converted to PNG");
        var png = Png.Decode(g.View(img["bufferView"]!.GetValue<int>()));
        Assert.True(png.Width == 4 && png.Height == 2, "size");
        Assert.True(png.Pixels[0] == 255 && png.Pixels[1] == 0 && png.Pixels[2] == 0, "top-left pixel red (BMP bottom-up handled)");
        var mats = g.Arr("materials");
        Assert.Equal("OPAQUE", mats[0]!["alphaMode"]!.GetValue<string>(), "body opaque");
        Assert.Equal("BLEND", mats[1]!["alphaMode"]!.GetValue<string>(), "hair (diffuse alpha 0.9) blended");
        Assert.True(mats[1]!["doubleSided"]!.GetValue<bool>(), "double-sided flag");
        var mt = mats[0]!["extensions"]!["VRMC_materials_mtoon"]!;
        Assert.Equal("worldCoordinates", mt["outlineWidthMode"]!.GetValue<string>(), "edge -> outline");
        // Shade from the toon ramp's bottom row (R=40, G=50, B=60 of 255) times the lit color.
        Assert.Near(40 / 255.0, mt["shadeColorFactor"]![0]!.GetValue<double>(), 0.01, "shade from toon ramp");
        Assert.True(!rep.Warnings.Any(w => w.Contains("not found")), "texture found case-insensitively");
    }

    public static void TestLicenseSidecarAndCli()
    {
        var pmx = WriteSample();
        var outPath = Path.Combine(Path.GetDirectoryName(pmx)!, "out", "model.vrm");
        int code = Pmx2Vrm.Program.Main(new[] { pmx, outPath, "--quiet", "--author", "Someone", "--avatar-permission", "everyone" });
        Assert.Equal(0, code, "exit code");
        Assert.True(File.Exists(outPath), "vrm written");
        var lic = File.ReadAllText(Path.ChangeExtension(outPath, null) + ".license.txt");
        Assert.True(lic.Contains("改変OK") && lic.Contains("test terms"), "license sidecar has the PMX comment and the readme");
        var g = Glb.Parse(File.ReadAllBytes(outPath));
        Assert.Equal("Someone", Vrm(g)["meta"]!["authors"]![0]!.GetValue<string>(), "--author");
        Assert.Equal("everyone", Vrm(g)["meta"]!["avatarPermission"]!.GetValue<string>(), "--avatar-permission");
        // Folder mode.
        var folder = Path.GetDirectoryName(pmx)!;
        Assert.Equal(0, Pmx2Vrm.Program.Main(new[] { folder, Path.Combine(folder, "batch"), "--quiet" }), "folder mode exit code");
        Assert.True(File.Exists(Path.Combine(folder, "batch", "test.vrm")), "folder mode output");
        Assert.Equal(2, Pmx2Vrm.Program.Main(new[] { pmx, "--bogus" }), "bad option exit code");
    }

    public static void TestGuessAuthor()
    {
        Assert.Equal("ABC", Converter.GuessAuthor("モデル：ABC") ?? Converter.GuessAuthor("モデル製作：ABC"), "jp");
        Assert.Equal("Foo Bar", Converter.GuessAuthor("Some model\nModel by: Foo Bar\n"), "en");
        Assert.Equal(null, Converter.GuessAuthor("nothing here"), "none");
    }

    /// <summary>Runs the Khronos glTF-Validator (tools/validator, npm) when it is installed.</summary>
    public static void TestKhronosValidator()
    {
        var root = FindRepoRoot();
        var script = Path.Combine(root, "tools", "validator", "validate.mjs");
        var node = Environment.GetEnvironmentVariable("PMX2VRM_NODE") ?? "node";
        if (!Directory.Exists(Path.Combine(root, "tools", "validator", "node_modules", "gltf-validator")))
        {
            Console.WriteLine("    (skipped: run `npm install` in tools/validator to enable)");
            return;
        }
        var pmx = WriteSample();
        var (bytes, _) = Converter.Convert(pmx, new ConvertOptions());
        var vrm = Path.ChangeExtension(pmx, ".vrm");
        File.WriteAllBytes(vrm, bytes);
        var psi = new System.Diagnostics.ProcessStartInfo(node, $"\"{script}\" \"{vrm}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Console.WriteLine("    " + stdout.Trim().Replace("\n", "\n    "));
        Assert.True(p.ExitCode == 0, "glTF-Validator reported errors: " + stdout + stderr);
    }

    static string FindRepoRoot()
    {
        var d = AppContext.BaseDirectory;
        while (d != null && !File.Exists(Path.Combine(d, "pmx-to-vrm.sln"))) d = Path.GetDirectoryName(d);
        return d ?? Directory.GetCurrentDirectory();
    }
}
