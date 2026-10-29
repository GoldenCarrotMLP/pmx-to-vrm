using System.Numerics;
using System.Text.Json.Nodes;
using Pmx2Vrm.Gltf;
using Pmx2Vrm.Images;
using Pmx2Vrm.Pmx;

namespace Pmx2Vrm.Convert;

public sealed class MaterialInfo
{
    public int Gltf;
    public Vector4 BaseColor;
    public string AlphaMode = "OPAQUE";
}

sealed class LoadedTexture
{
    public int Texture = -1;
    public RgbaImage? Pixels;
    public AlphaUsage Alpha = AlphaUsage.Opaque;
}

public sealed partial class Converter
{
    readonly Dictionary<string, LoadedTexture?> _texCache = new(StringComparer.OrdinalIgnoreCase);
    int _sampler = -1;

    /// <summary>
    /// MMD materials as glTF + MToon. Base color = texture x (diffuse, brightened by ambient the way MMD's
    /// default light does); shade color from the toon ramp's dark end; alpha mode from the diffuse alpha and
    /// the texture's alpha; outline from the edge settings; additive sphere maps as MToon matcap.
    /// </summary>
    MaterialInfo[] BuildMaterials(ExpressionPlan plan)
    {
        var result = new MaterialInfo[_m.Materials.Count];
        for (int i = 0; i < _m.Materials.Count; i++)
        {
            var pm = _m.Materials[i];
            var info = new MaterialInfo();
            var d = pm.Diffuse;
            info.BaseColor = BaseColorOf(pm);
            var lit = new Vector3(info.BaseColor.X, info.BaseColor.Y, info.BaseColor.Z);
            float alpha = info.BaseColor.W;

            var tex = pm.TextureIndex >= 0 ? LoadTexture(pm.TextureIndex, $"material '{pm.Name}'") : null;
            var alphaUse = alpha < 0.999f ? AlphaUsage.Blend : tex?.Alpha ?? AlphaUsage.Opaque;
            if (alphaUse == AlphaUsage.Opaque && plan.AlphaAnimated.Contains(i)) alphaUse = AlphaUsage.Cutout;
            info.AlphaMode = alphaUse switch { AlphaUsage.Blend => "BLEND", AlphaUsage.Cutout => "MASK", _ => "OPAQUE" };

            // Shade: the dark end of the toon ramp (a texture of the model, or MMD's shared toon01-10).
            Vector3 toonDark = new(0.8f);
            if (!pm.SharedToon && pm.ToonIndex >= 0)
            {
                var toon = LoadTexture(pm.ToonIndex, $"toon of '{pm.Name}'", toon: true);
                if (toon?.Pixels != null)
                {
                    var (r, g, b) = ImageAnalysis.BottomRowColor(toon.Pixels);
                    toonDark = new Vector3(r, g, b);
                }
            }
            else if (pm.SharedToon && pm.ToonIndex >= 0 && pm.ToonIndex < SharedToonDark.Length)
                toonDark = SharedToonDark[pm.ToonIndex];
            var shade = Vector3.Clamp(lit * toonDark, Vector3.Zero, Vector3.One);

            var pbr = new JsonObject
            {
                ["baseColorFactor"] = GltfBuilder.Arr(info.BaseColor.X, info.BaseColor.Y, info.BaseColor.Z, info.BaseColor.W),
                ["metallicFactor"] = 0,
                ["roughnessFactor"] = 1,
            };
            if (tex != null) pbr["baseColorTexture"] = new JsonObject { ["index"] = tex.Texture };
            var mtoon = new JsonObject
            {
                ["specVersion"] = "1.0",
                ["transparentWithZWrite"] = info.AlphaMode == "BLEND",
                ["renderQueueOffsetNumber"] = info.AlphaMode == "BLEND" ? Math.Min(9, CountBlendBefore(result, i)) : 0,
                ["shadeColorFactor"] = GltfBuilder.Arr(shade),
                ["shadingShiftFactor"] = GltfBuilder.Num(0.0f),
                ["shadingToonyFactor"] = GltfBuilder.Num(0.95f),
                ["giEqualizationFactor"] = GltfBuilder.Num(0.9f),
                ["parametricRimColorFactor"] = GltfBuilder.Arr(0, 0, 0),
                ["rimLightingMixFactor"] = GltfBuilder.Num(1),
                ["parametricRimFresnelPowerFactor"] = GltfBuilder.Num(5),
                ["parametricRimLiftFactor"] = GltfBuilder.Num(0),
            };
            if (tex != null) mtoon["shadeMultiplyTexture"] = new JsonObject { ["index"] = tex.Texture };
            if (pm.SphereIndex >= 0 && pm.SphereMode == 2)
            {
                var sph = LoadTexture(pm.SphereIndex, $"sphere map of '{pm.Name}'");
                if (sph != null)
                {
                    mtoon["matcapTexture"] = new JsonObject { ["index"] = sph.Texture };
                    mtoon["matcapFactor"] = GltfBuilder.Arr(1, 1, 1);
                }
            }
            else if (pm.SphereIndex >= 0 && pm.SphereMode == 1)
                _r.Warnings.Add($"Material '{pm.Name}': multiplicative sphere map skipped (MToon's matcap only adds).");
            if (pm.Flags.HasFlag(PmxMaterialFlags.Edge) && pm.EdgeSize > 0 && pm.EdgeColor.W > 0)
            {
                mtoon["outlineWidthMode"] = "worldCoordinates";
                mtoon["outlineWidthFactor"] = GltfBuilder.Num(0.0012f * pm.EdgeSize * (_o.Scale / 0.08f));
                mtoon["outlineColorFactor"] = GltfBuilder.Arr(pm.EdgeColor.X, pm.EdgeColor.Y, pm.EdgeColor.Z);
                mtoon["outlineLightingMixFactor"] = GltfBuilder.Num(1);
            }
            else mtoon["outlineWidthMode"] = "none";

            var mat = new JsonObject
            {
                ["name"] = pm.Name.Length > 0 ? pm.Name : $"material{i}",
                ["pbrMetallicRoughness"] = pbr,
                ["alphaMode"] = info.AlphaMode,
                ["doubleSided"] = pm.Flags.HasFlag(PmxMaterialFlags.DoubleSided),
                ["extensions"] = new JsonObject { ["VRMC_materials_mtoon"] = mtoon },
            };
            if (info.AlphaMode == "MASK") mat["alphaCutoff"] = GltfBuilder.Num(0.5f);
            _g.Materials.Add(mat);
            info.Gltf = _g.Materials.Count - 1;
            result[i] = info;
        }
        _r.Textures = _g.Images.Count;
        return result;
    }

    /// <summary>
    /// The glTF/MToon base color of an MMD material: diffuse brightened by ambient the way MMD's default light
    /// (0.6) does, clamped to 0-1; alpha = diffuse alpha.
    /// </summary>
    public static Vector4 BaseColorOf(PmxMaterial pm)
    {
        var d = new Vector3(pm.Diffuse.X, pm.Diffuse.Y, pm.Diffuse.Z);
        var lit = Vector3.Clamp(Vector3.Max(d * 0.6f + pm.Ambient, d * 0.8f), Vector3.Zero, Vector3.One);
        return new Vector4(lit, Math.Clamp(pm.Diffuse.W, 0, 1));
    }

    static PmxMaterial CopyMaterial(PmxMaterial m) => new()
    {
        Name = m.Name, NameEn = m.NameEn, Diffuse = m.Diffuse, Specular = m.Specular, SpecularPower = m.SpecularPower,
        Ambient = m.Ambient, Flags = m.Flags, EdgeColor = m.EdgeColor, EdgeSize = m.EdgeSize,
        TextureIndex = m.TextureIndex, SphereIndex = m.SphereIndex, SphereMode = m.SphereMode,
        SharedToon = m.SharedToon, ToonIndex = m.ToonIndex, Memo = m.Memo, IndexCount = m.IndexCount,
    };

    static int CountBlendBefore(MaterialInfo[] done, int i)
    {
        int n = 0;
        for (int k = 0; k < i; k++) if (done[k]?.AlphaMode == "BLEND") n++;
        return n;
    }

    /// <summary>Approximate dark ends of MMD's shared toon01.bmp - toon10.bmp (they are not redistributable, so no exact values).</summary>
    static readonly Vector3[] SharedToonDark =
    {
        new(0.80f), new(0.80f), new(0.76f), new(0.88f), new(0.86f, 0.84f, 0.84f),
        new(0.80f), new(0.88f), new(0.82f), new(0.92f), new(0.88f),
    };

    LoadedTexture? LoadTexture(int index, string usedBy, bool toon = false)
    {
        if (index < 0 || index >= _m.Textures.Count) return null;
        var rel = _m.Textures[index].Trim();
        if (rel.Length == 0) return null;
        string key = (toon ? "toon|" : "") + rel;
        if (_texCache.TryGetValue(key, out var cached)) return cached;
        LoadedTexture? result = null;
        try
        {
            var path = ResolvePath(rel);
            if (path == null)
            {
                if (!(toon && IsSharedToonName(rel)))
                    _r.Warnings.Add($"Texture '{rel}' ({usedBy}) not found.");
                return _texCache[key] = null;
            }
            var bytes = File.ReadAllBytes(path);
            var kind = ImageFormat.Detect(bytes, path);
            RgbaImage? img = null;
            try { img = ImageFormat.Decode(bytes, kind); }
            catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException)
            {
                _r.Warnings.Add($"Texture '{rel}' could not be decoded ({e.Message}).");
                if (kind != ImageKind.Png && kind != ImageKind.Jpeg) return _texCache[key] = null;
            }
            result = new LoadedTexture { Pixels = img };
            if (toon) return _texCache[key] = result; // toon ramps are only sampled, not embedded
            if (img != null) result.Alpha = ImageAnalysis.Alpha(img);
            int image;
            string name = Path.GetFileNameWithoutExtension(rel.Replace('\\', '/'));
            switch (kind)
            {
                case ImageKind.Png: image = _g.AddImage(bytes, "image/png", name); break;
                case ImageKind.Jpeg: image = _g.AddImage(bytes, "image/jpeg", name); break;
                default:
                    if (img == null)
                    {
                        _r.Warnings.Add($"Texture '{rel}' is in an unsupported format ({kind}); skipped.");
                        return _texCache[key] = null;
                    }
                    image = _g.AddImage(Png.Encode(img), "image/png", name);
                    break;
            }
            if (_sampler < 0)
            {
                _g.Samplers.Add(new JsonObject { ["magFilter"] = 9729, ["minFilter"] = 9987, ["wrapS"] = 10497, ["wrapT"] = 10497 });
                _sampler = 0;
            }
            result.Texture = _g.AddTexture(image, _sampler);
            return _texCache[key] = result;
        }
        catch (IOException e)
        {
            _r.Warnings.Add($"Texture '{rel}' could not be read ({e.Message}).");
            return _texCache[key] = null;
        }
    }

    static bool IsSharedToonName(string rel) =>
        System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(rel.Replace('\\', '/')), @"^toon(0[1-9]|10)\.bmp$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Resolves a PMX texture path (backslashes, any case) against the model's folder.</summary>
    string? ResolvePath(string rel)
    {
        var parts = rel.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var direct = Path.Combine(new[] { _dir }.Concat(parts).ToArray());
        if (File.Exists(direct)) return direct;
        string cur = _dir;
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] == ".") continue;
            if (parts[i] == "..") { cur = Path.GetDirectoryName(cur) ?? cur; continue; }
            bool last = i == parts.Length - 1;
            string? hit = null;
            try
            {
                var entries = last ? Directory.GetFiles(cur) : Directory.GetDirectories(cur);
                hit = entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e), parts[i], StringComparison.OrdinalIgnoreCase));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (hit == null) return null;
            cur = hit;
        }
        return File.Exists(cur) ? cur : null;
    }
}
