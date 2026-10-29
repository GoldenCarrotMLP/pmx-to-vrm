using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pmx2Vrm.Gltf;

/// <summary>Builds a glTF 2.0 binary (GLB): one buffer, buffer views, accessors, plus the JSON document.</summary>
public sealed class GltfBuilder
{
    public const int Float = 5126, UnsignedShort = 5123, UnsignedInt = 5125, UnsignedByte = 5121;
    const int ArrayBuffer = 34962, ElementArrayBuffer = 34963;

    readonly MemoryStream _bin = new();
    public readonly JsonArray BufferViews = new(), Accessors = new(), Images = new(), Textures = new(), Samplers = new(),
        Materials = new(), Meshes = new(), Nodes = new(), Skins = new();

    public int AddBufferView(ReadOnlySpan<byte> data, int? target)
    {
        while (_bin.Length % 4 != 0) _bin.WriteByte(0);
        long offset = _bin.Length;
        _bin.Write(data);
        var v = new JsonObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = data.Length };
        if (target.HasValue) v["target"] = target.Value;
        BufferViews.Add(v);
        return BufferViews.Count - 1;
    }

    int Accessor(int view, int componentType, int count, string type, float[]? min = null, float[]? max = null)
    {
        var a = new JsonObject { ["bufferView"] = view, ["componentType"] = componentType, ["count"] = count, ["type"] = type };
        if (min != null) a["min"] = Exact(min);
        if (max != null) a["max"] = Exact(max);
        Accessors.Add(a);
        return Accessors.Count - 1;
    }

    public int AddVec3(Vector3[] data, bool minMax, bool vertexAttribute = true)
    {
        int view = AddBufferView(MemoryMarshal.AsBytes(data.AsSpan()), vertexAttribute ? ArrayBuffer : null);
        float[]? min = null, max = null;
        if (minMax && data.Length > 0)
        {
            var mn = new Vector3(float.MaxValue); var mx = new Vector3(float.MinValue);
            foreach (var v in data) { mn = Vector3.Min(mn, v); mx = Vector3.Max(mx, v); }
            min = new[] { mn.X, mn.Y, mn.Z }; max = new[] { mx.X, mx.Y, mx.Z };
        }
        return Accessor(view, Float, data.Length, "VEC3", min, max);
    }

    public int AddVec2(Vector2[] data) => Accessor(AddBufferView(MemoryMarshal.AsBytes(data.AsSpan()), ArrayBuffer), Float, data.Length, "VEC2");
    public int AddVec4(Vector4[] data) => Accessor(AddBufferView(MemoryMarshal.AsBytes(data.AsSpan()), ArrayBuffer), Float, data.Length, "VEC4");

    public int AddJoints(ushort[] data) => Accessor(AddBufferView(MemoryMarshal.AsBytes(data.AsSpan()), ArrayBuffer), UnsignedShort, data.Length / 4, "VEC4");

    public int AddIndices(uint[] data, int vertexCount)
    {
        if (vertexCount <= 65535)
        {
            var s = new ushort[data.Length];
            for (int i = 0; i < data.Length; i++) s[i] = (ushort)data[i];
            return Accessor(AddBufferView(MemoryMarshal.AsBytes(s.AsSpan()), ElementArrayBuffer), UnsignedShort, s.Length, "SCALAR");
        }
        return Accessor(AddBufferView(MemoryMarshal.AsBytes(data.AsSpan()), ElementArrayBuffer), UnsignedInt, data.Length, "SCALAR");
    }

    /// <summary>Column-major 4x4 float matrices.</summary>
    public int AddMat4(float[] columnMajor) =>
        Accessor(AddBufferView(MemoryMarshal.AsBytes(columnMajor.AsSpan()), null), Float, columnMajor.Length / 16, "MAT4");

    public int AddImage(byte[] data, string mime, string name)
    {
        int view = AddBufferView(data, null);
        Images.Add(new JsonObject { ["name"] = name, ["bufferView"] = view, ["mimeType"] = mime });
        return Images.Count - 1;
    }

    public int AddTexture(int image, int sampler)
    {
        Textures.Add(new JsonObject { ["source"] = image, ["sampler"] = sampler });
        return Textures.Count - 1;
    }

    /// <summary>Floats written exactly (accessor min/max must match the data bit for bit).</summary>
    static JsonArray Exact(float[] v)
    {
        var a = new JsonArray();
        foreach (var f in v) a.Add(JsonValue.Create((double)f));
        return a;
    }

    public static JsonArray Arr(params float[] v)
    {
        var a = new JsonArray();
        foreach (var f in v) a.Add(Num(f));
        return a;
    }

    public static JsonArray Arr(Vector3 v) => Arr(v.X, v.Y, v.Z);

    /// <summary>A float as a short JSON number (6 significant decimals; NaN/inf become 0).</summary>
    public static JsonNode Num(float f)
    {
        if (!float.IsFinite(f)) f = 0;
        double d = Math.Round((double)f, 6);
        if (d == 0) d = 0; // no "-0"
        return JsonValue.Create(d);
    }

    public byte[] ToGlb(JsonObject root)
    {
        while (_bin.Length % 4 != 0) _bin.WriteByte(0);
        root["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = _bin.Length });
        void Set(string key, JsonArray arr) { if (arr.Count > 0) root[key] = arr; }
        Set("bufferViews", BufferViews); Set("accessors", Accessors); Set("images", Images); Set("samplers", Samplers);
        Set("textures", Textures); Set("materials", Materials); Set("meshes", Meshes); Set("nodes", Nodes); Set("skins", Skins);
        var json = Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        int jsonPad = (4 - json.Length % 4) % 4;
        int jsonLen = json.Length + jsonPad;
        int binLen = (int)_bin.Length;
        int total = 12 + 8 + jsonLen + 8 + binLen;
        var ms = new MemoryStream(total);
        var w = new BinaryWriter(ms);
        w.Write(0x46546C67u); w.Write(2u); w.Write((uint)total);
        w.Write((uint)jsonLen); w.Write(0x4E4F534Au); w.Write(json);
        for (int i = 0; i < jsonPad; i++) w.Write((byte)' ');
        w.Write((uint)binLen); w.Write(0x004E4942u); w.Write(_bin.GetBuffer(), 0, binLen);
        w.Flush();
        return ms.ToArray();
    }
}
