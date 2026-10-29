using System.Numerics;
using Pmx2Vrm.Pmx;

namespace Pmx2Vrm.Convert;

/// <summary>
/// Applies PMX morphs permanently to the model before conversion: vertex offsets into the positions, UV
/// offsets into the UVs, material offsets into the material values (multiply: value x lerp(1, offset, w);
/// add: value + offset x w, as MMD does). Group morphs are expanded with their influences.
/// </summary>
public static class MorphBaker
{
    /// <summary>Tda-style AutoLuminous models: the readme says to set these to 1 when AutoLuminous is not used.</summary>
    public static readonly string[] AutoLuminousMorphs = { "AL未使用", "LightOff" };

    public static int Find(PmxModel m, string name)
    {
        var n = Names.Norm(name);
        for (int i = 0; i < m.Morphs.Count; i++) if (Names.Norm(m.Morphs[i].Name) == n) return i;
        for (int i = 0; i < m.Morphs.Count; i++) if (n.Length > 0 && Names.Norm(m.Morphs[i].NameEn) == n) return i;
        return -1;
    }

    /// <returns>A one-line summary of what was applied.</returns>
    public static string Apply(PmxModel m, int morph, float weight, List<string> warnings)
    {
        int verts = 0, uvs = 0, mats = 0, bones = 0, other = 0;
        void Do(int mi, float w, int depth)
        {
            if (mi < 0 || mi >= m.Morphs.Count || depth > 8 || w == 0) return;
            var mo = m.Morphs[mi];
            switch (mo.Type)
            {
                case PmxMorphType.Group:
                    foreach (var (sub, sw) in mo.Group) if (sub != mi) Do(sub, w * sw, depth + 1);
                    break;
                case PmxMorphType.Vertex:
                    foreach (var (v, off) in mo.Vertex)
                        if (v >= 0 && v < m.Vertices.Count) { m.Vertices[v].Position += off * w; verts++; }
                    break;
                case PmxMorphType.Uv:
                    foreach (var (v, off) in mo.Uv)
                        if (v >= 0 && v < m.Vertices.Count) { m.Vertices[v].Uv += new Vector2(off.X, off.Y) * w; uvs++; }
                    break;
                case PmxMorphType.Material:
                    foreach (var o in mo.Material)
                    {
                        var targets = o.Material < 0 ? Enumerable.Range(0, m.Materials.Count) : new[] { o.Material };
                        foreach (var k in targets)
                        {
                            if (k < 0 || k >= m.Materials.Count) continue;
                            ApplyMaterial(m.Materials[k], o, w);
                            mats++;
                        }
                    }
                    break;
                case PmxMorphType.Bone: bones++; break;
                default: other++; break;
            }
        }
        Do(morph, weight, 0);
        var name = m.Morphs[morph].Name;
        if (bones > 0) warnings.Add($"Baked morph '{name}': its bone parts were not applied.");
        if (other > 0) warnings.Add($"Baked morph '{name}': its extra-UV / flip / impulse parts were not applied.");
        return $"'{name}' x{weight:0.##}: {verts} vertex, {uvs} UV, {mats} material offsets";
    }

    public static void ApplyMaterial(PmxMaterial mat, PmxMaterialOffset o, float w)
    {
        if (o.Operation == 0)
        {
            Vector4 M4(Vector4 v) => Vector4.Lerp(Vector4.One, v, w);
            Vector3 M3(Vector3 v) => Vector3.Lerp(Vector3.One, v, w);
            mat.Diffuse *= M4(o.Diffuse);
            // The texture factor multiplies the texture; the base color is texture x diffuse, so it folds in.
            if (o.TextureTint != Vector4.Zero) mat.Diffuse *= new Vector4(M3(new Vector3(o.TextureTint.X, o.TextureTint.Y, o.TextureTint.Z)), 1);
            mat.Specular *= M3(o.Specular);
            mat.SpecularPower *= 1 + (o.SpecularPower - 1) * w;
            mat.Ambient *= M3(o.Ambient);
            mat.EdgeColor *= M4(o.EdgeColor);
            mat.EdgeSize *= 1 + (o.EdgeSize - 1) * w;
        }
        else
        {
            mat.Diffuse += o.Diffuse * w;
            mat.Specular += o.Specular * w;
            mat.SpecularPower += o.SpecularPower * w;
            mat.Ambient += o.Ambient * w;
            mat.EdgeColor += o.EdgeColor * w;
            mat.EdgeSize += o.EdgeSize * w;
        }
        mat.Diffuse = Vector4.Clamp(mat.Diffuse, Vector4.Zero, new Vector4(float.MaxValue));
        mat.Ambient = Vector3.Max(mat.Ambient, Vector3.Zero);
        mat.Specular = Vector3.Max(mat.Specular, Vector3.Zero);
        mat.EdgeColor = Vector4.Clamp(mat.EdgeColor, Vector4.Zero, Vector4.One);
        mat.EdgeSize = MathF.Max(0, mat.EdgeSize);
    }
}
