using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace Pmx2Vrm.Tests;

/// <summary>Reads a GLB back for the tests and runs structural checks on it.</summary>
public sealed class Glb
{
    public JsonObject Json = null!;
    public byte[] Bin = Array.Empty<byte>();

    public static Glb Parse(byte[] d)
    {
        Assert.True(d.Length >= 20, "glb too short");
        Assert.Equal(0x46546C67u, BinaryPrimitives.ReadUInt32LittleEndian(d), "glb magic");
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(4)), "glb version");
        Assert.Equal((uint)d.Length, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(8)), "glb length");
        int jl = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(12));
        Assert.Equal(0x4E4F534Au, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(16)), "json chunk type");
        Assert.True(jl % 4 == 0, "json chunk padded to 4");
        var g = new Glb { Json = JsonNode.Parse(Encoding.UTF8.GetString(d, 20, jl))!.AsObject() };
        int bo = 20 + jl;
        if (bo < d.Length)
        {
            int bl = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(bo));
            Assert.Equal(0x004E4942u, BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(bo + 4)), "bin chunk type");
            g.Bin = d.AsSpan(bo + 8, bl).ToArray();
        }
        return g;
    }

    public JsonArray Arr(string key) => Json[key] as JsonArray ?? new JsonArray();

    public byte[] View(int view)
    {
        var v = Arr("bufferViews")[view]!;
        int off = v["byteOffset"]?.GetValue<int>() ?? 0, len = v["byteLength"]!.GetValue<int>();
        return Bin.AsSpan(off, len).ToArray();
    }

    public float[] Floats(int accessor)
    {
        var a = Arr("accessors")[accessor]!;
        Assert.Equal(5126, a["componentType"]!.GetValue<int>(), "float accessor");
        var b = View(a["bufferView"]!.GetValue<int>());
        var f = new float[b.Length / 4];
        Buffer.BlockCopy(b, 0, f, 0, f.Length * 4);
        return f;
    }

    static int Components(string type) => type switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16, _ => throw new Exception(type) };
    static int ComponentSize(int ct) => ct switch { 5120 or 5121 => 1, 5122 or 5123 => 2, 5125 or 5126 => 4, _ => throw new Exception($"component {ct}") };

    /// <summary>The structural rules of glTF 2.0 the converter relies on.</summary>
    public void CheckStructure()
    {
        Assert.Equal("2.0", Json["asset"]!["version"]!.GetValue<string>(), "asset.version");
        var buffers = Arr("buffers");
        Assert.Equal(1, buffers.Count, "one buffer");
        Assert.True(buffers[0]!["byteLength"]!.GetValue<long>() <= Bin.Length, "buffer fits the BIN chunk");
        foreach (var v in Arr("bufferViews"))
        {
            long off = v!["byteOffset"]?.GetValue<long>() ?? 0, len = v["byteLength"]!.GetValue<long>();
            Assert.True(off >= 0 && len > 0 && off + len <= Bin.Length, "bufferView inside the buffer");
            Assert.True(off % 4 == 0, "bufferView aligned");
        }
        var views = Arr("bufferViews");
        foreach (var a in Arr("accessors"))
        {
            int view = a!["bufferView"]!.GetValue<int>();
            int count = a["count"]!.GetValue<int>();
            Assert.True(count > 0, "accessor count > 0");
            long need = (long)count * Components(a["type"]!.GetValue<string>()) * ComponentSize(a["componentType"]!.GetValue<int>());
            Assert.True(need <= views[view]!["byteLength"]!.GetValue<long>(), "accessor inside its view");
        }
        int nodes = Arr("nodes").Count;
        var parentCount = new int[nodes];
        foreach (var n in Arr("nodes"))
        {
            Assert.True(n!["rotation"] == null && n["scale"] == null && n["matrix"] == null, $"node '{n["name"]}' has only a translation");
            if (n["children"] is JsonArray kids)
                foreach (var k in kids) { int c = k!.GetValue<int>(); Assert.True(c >= 0 && c < nodes, "child index"); parentCount[c]++; }
        }
        Assert.True(parentCount.All(c => c <= 1), "every node has at most one parent");
        foreach (var s in Arr("scenes"))
            foreach (var n in (JsonArray)s!["nodes"]!) Assert.Equal(0, parentCount[n!.GetValue<int>()], "scene nodes are roots");
        var accessors = Arr("accessors");
        foreach (var mesh in Arr("meshes"))
        {
            int? targets = null;
            foreach (var p in (JsonArray)mesh!["primitives"]!)
            {
                var attrs = p!["attributes"]!.AsObject();
                int vc = accessors[attrs["POSITION"]!.GetValue<int>()]!["count"]!.GetValue<int>();
                foreach (var (_, acc) in attrs) Assert.Equal(vc, accessors[acc!.GetValue<int>()]!["count"]!.GetValue<int>(), "attribute counts match");
                Assert.True(accessors[attrs["POSITION"]!.GetValue<int>()]!["min"] != null, "POSITION has min/max");
                int tc = (p["targets"] as JsonArray)?.Count ?? 0;
                targets ??= tc;
                Assert.Equal(targets.Value, tc, "every primitive has the same targets");
                Assert.True(accessors[p["indices"]!.GetValue<int>()]!["count"]!.GetValue<int>() % 3 == 0, "triangle indices");
            }
        }
        foreach (var skin in Arr("skins"))
        {
            int joints = ((JsonArray)skin!["joints"]!).Count;
            Assert.Equal(joints, accessors[skin["inverseBindMatrices"]!.GetValue<int>()]!["count"]!.GetValue<int>(), "one bind matrix per joint");
        }
    }
}
