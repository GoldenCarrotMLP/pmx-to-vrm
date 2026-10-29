using System.Numerics;
using System.Text;
using Pmx2Vrm.Pmx;

namespace Pmx2Vrm.Tests;

/// <summary>Writes PMX files. Test-only: lets the tests build synthetic models in memory.</summary>
public static class PmxWriter
{
    public static byte[] Write(PmxModel m)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        var enc = m.Encoding == 1 ? Encoding.UTF8 : Encoding.Unicode;
        void Text(string s) { var b = enc.GetBytes(s); w.Write(b.Length); w.Write(b); }
        void V2(Vector2 v) { w.Write(v.X); w.Write(v.Y); }
        void V3(Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
        void V4(Vector4 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); w.Write(v.W); }
        void Idx(int size, int value)
        {
            switch (size)
            {
                case 1: w.Write((sbyte)value); break;
                case 2: w.Write((short)value); break;
                default: w.Write(value); break;
            }
        }
        void VIdx(int value)
        {
            switch (m.VertexIndexSize)
            {
                case 1: w.Write((byte)value); break;
                case 2: w.Write((ushort)value); break;
                default: w.Write(value); break;
            }
        }
        void Bone(int i) => Idx(m.BoneIndexSize, i);

        w.Write(Encoding.ASCII.GetBytes("PMX "));
        w.Write(m.Version);
        w.Write((byte)8);
        w.Write(new[] { m.Encoding, m.AdditionalUvCount, m.VertexIndexSize, m.TextureIndexSize, m.MaterialIndexSize, m.BoneIndexSize, m.MorphIndexSize, m.RigidBodyIndexSize });
        Text(m.Name); Text(m.NameEn); Text(m.Comment); Text(m.CommentEn);

        w.Write(m.Vertices.Count);
        foreach (var v in m.Vertices)
        {
            V3(v.Position); V3(v.Normal); V2(v.Uv);
            for (int k = 0; k < m.AdditionalUvCount; k++) V4(k < v.ExtraUv.Length ? v.ExtraUv[k] : default);
            w.Write((byte)v.WeightType);
            switch (v.WeightType)
            {
                case PmxWeightType.Bdef1: Bone(v.Bones[0]); break;
                case PmxWeightType.Bdef2: Bone(v.Bones[0]); Bone(v.Bones[1]); w.Write(v.Weights[0]); break;
                case PmxWeightType.Bdef4:
                case PmxWeightType.Qdef:
                    for (int k = 0; k < 4; k++) Bone(v.Bones[k]);
                    for (int k = 0; k < 4; k++) w.Write(v.Weights[k]);
                    break;
                case PmxWeightType.Sdef:
                    Bone(v.Bones[0]); Bone(v.Bones[1]); w.Write(v.Weights[0]);
                    V3(v.SdefC); V3(v.SdefR0); V3(v.SdefR1);
                    break;
            }
            w.Write(v.EdgeScale);
        }

        w.Write(m.Indices.Count);
        foreach (var i in m.Indices) VIdx(i);

        w.Write(m.Textures.Count);
        foreach (var t in m.Textures) Text(t);

        w.Write(m.Materials.Count);
        foreach (var mat in m.Materials)
        {
            Text(mat.Name); Text(mat.NameEn); V4(mat.Diffuse); V3(mat.Specular); w.Write(mat.SpecularPower); V3(mat.Ambient);
            w.Write((byte)mat.Flags); V4(mat.EdgeColor); w.Write(mat.EdgeSize);
            Idx(m.TextureIndexSize, mat.TextureIndex); Idx(m.TextureIndexSize, mat.SphereIndex); w.Write(mat.SphereMode);
            w.Write((byte)(mat.SharedToon ? 1 : 0));
            if (mat.SharedToon) w.Write((byte)mat.ToonIndex); else Idx(m.TextureIndexSize, mat.ToonIndex);
            Text(mat.Memo); w.Write(mat.IndexCount);
        }

        w.Write(m.Bones.Count);
        foreach (var b in m.Bones)
        {
            Text(b.Name); Text(b.NameEn); V3(b.Position); Bone(b.Parent); w.Write(b.Layer); w.Write((ushort)b.Flags);
            if (b.Flags.HasFlag(PmxBoneFlags.TailIsBone)) Bone(b.TailBone); else V3(b.TailOffset);
            if ((b.Flags & (PmxBoneFlags.InheritRotation | PmxBoneFlags.InheritTranslation)) != 0) { Bone(b.InheritParent); w.Write(b.InheritWeight); }
            if (b.Flags.HasFlag(PmxBoneFlags.FixedAxis)) V3(b.FixedAxis);
            if (b.Flags.HasFlag(PmxBoneFlags.LocalAxis)) { V3(b.LocalX); V3(b.LocalZ); }
            if (b.Flags.HasFlag(PmxBoneFlags.ExternalParent)) w.Write(b.ExternalKey);
            if (b.Flags.HasFlag(PmxBoneFlags.Ik))
            {
                var ik = b.Ik!;
                Bone(ik.Target); w.Write(ik.Loops); w.Write(ik.LimitAngle); w.Write(ik.Links.Count);
                foreach (var l in ik.Links)
                {
                    Bone(l.Bone); w.Write((byte)(l.HasLimits ? 1 : 0));
                    if (l.HasLimits) { V3(l.Min); V3(l.Max); }
                }
            }
        }

        w.Write(m.Morphs.Count);
        foreach (var mo in m.Morphs)
        {
            Text(mo.Name); Text(mo.NameEn); w.Write(mo.Panel); w.Write((byte)mo.Type);
            switch (mo.Type)
            {
                case PmxMorphType.Group:
                case PmxMorphType.Flip:
                    w.Write(mo.Group.Count);
                    foreach (var (i, wt) in mo.Group) { Idx(m.MorphIndexSize, i); w.Write(wt); }
                    break;
                case PmxMorphType.Vertex:
                    w.Write(mo.Vertex.Count);
                    foreach (var (i, o) in mo.Vertex) { VIdx(i); V3(o); }
                    break;
                case PmxMorphType.Bone:
                    w.Write(mo.Bone.Count);
                    foreach (var (i, t, q) in mo.Bone) { Bone(i); V3(t); w.Write(q.X); w.Write(q.Y); w.Write(q.Z); w.Write(q.W); }
                    break;
                case PmxMorphType.Material:
                    w.Write(mo.Material.Count);
                    foreach (var o in mo.Material)
                    {
                        Idx(m.MaterialIndexSize, o.Material); w.Write(o.Operation); V4(o.Diffuse); V3(o.Specular); w.Write(o.SpecularPower);
                        V3(o.Ambient); V4(o.EdgeColor); w.Write(o.EdgeSize); V4(o.TextureTint); V4(o.SphereTint); V4(o.ToonTint);
                    }
                    break;
                case PmxMorphType.Impulse:
                    w.Write(mo.Impulse.Count);
                    foreach (var o in mo.Impulse) { Idx(m.RigidBodyIndexSize, o.Body); w.Write((byte)(o.Local ? 1 : 0)); V3(o.Velocity); V3(o.Torque); }
                    break;
                default:
                    w.Write(mo.Uv.Count);
                    foreach (var (i, o) in mo.Uv) { VIdx(i); V4(o); }
                    break;
            }
        }

        w.Write(m.DisplayFrames.Count);
        foreach (var f in m.DisplayFrames)
        {
            Text(f.Name); Text(f.NameEn); w.Write((byte)(f.Special ? 1 : 0)); w.Write(f.Items.Count);
            foreach (var (k, i) in f.Items) { w.Write(k); if (k == 0) Bone(i); else Idx(m.MorphIndexSize, i); }
        }

        w.Write(m.RigidBodies.Count);
        foreach (var rb in m.RigidBodies)
        {
            Text(rb.Name); Text(rb.NameEn); Bone(rb.Bone); w.Write(rb.Group); w.Write(rb.CollisionMask); w.Write((byte)rb.Shape);
            V3(rb.Size); V3(rb.Position); V3(rb.Rotation);
            w.Write(rb.Mass); w.Write(rb.LinearDamping); w.Write(rb.AngularDamping); w.Write(rb.Restitution); w.Write(rb.Friction);
            w.Write((byte)rb.Mode);
        }

        w.Write(m.Joints.Count);
        foreach (var j in m.Joints)
        {
            Text(j.Name); Text(j.NameEn); w.Write(j.Type);
            Idx(m.RigidBodyIndexSize, j.BodyA); Idx(m.RigidBodyIndexSize, j.BodyB);
            V3(j.Position); V3(j.Rotation); V3(j.PositionMin); V3(j.PositionMax); V3(j.RotationMin); V3(j.RotationMax);
            V3(j.PositionSpring); V3(j.RotationSpring);
        }
        if (m.Version > 2.05f) w.Write(0); // no soft bodies
        w.Flush();
        return ms.ToArray();
    }
}
