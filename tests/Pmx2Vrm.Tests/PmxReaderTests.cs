using Pmx2Vrm.Pmx;

namespace Pmx2Vrm.Tests;

public static class PmxReaderTests
{
    public static void TestRoundTripUtf16()
    {
        var m = SyntheticModel.Build();
        Check(m, PmxReader.Read(PmxWriter.Write(m)));
    }

    public static void TestRoundTripUtf8WideIndices21()
    {
        var m = SyntheticModel.Build();
        m.Encoding = 1; m.Version = 2.1f;
        m.VertexIndexSize = 4; m.TextureIndexSize = 2; m.MaterialIndexSize = 4; m.BoneIndexSize = 4; m.MorphIndexSize = 2; m.RigidBodyIndexSize = 4;
        Check(m, PmxReader.Read(PmxWriter.Write(m)));
    }

    public static void TestByteIndices()
    {
        var m = SyntheticModel.Build();
        m.BoneIndexSize = 1; m.VertexIndexSize = 2;
        var r = PmxReader.Read(PmxWriter.Write(m));
        Assert.Equal(-1, r.Bones[0].Parent, "signed byte bone index -1");
        Check(m, r);
    }

    public static void TestRejectsGarbage()
    {
        bool threw = false;
        try { PmxReader.Read(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }); } catch (InvalidDataException) { threw = true; }
        Assert.True(threw, "non-PMX data must be rejected");
    }

    static void Check(PmxModel a, PmxModel b)
    {
        Assert.Equal(a.Name, b.Name, "name");
        Assert.Equal(a.NameEn, b.NameEn, "english name");
        Assert.Equal(a.Comment, b.Comment, "comment");
        Assert.Equal(a.Vertices.Count, b.Vertices.Count, "vertex count");
        for (int i = 0; i < a.Vertices.Count; i++)
        {
            var va = a.Vertices[i]; var vb = b.Vertices[i];
            Assert.True(va.Position == vb.Position && va.Normal == vb.Normal && va.Uv == vb.Uv, $"vertex {i} data");
            Assert.Equal(va.WeightType, vb.WeightType, $"vertex {i} weight type");
            Assert.Equal(va.Bones[0], vb.Bones[0], $"vertex {i} bone 0");
            Assert.Near(va.Weights[0], vb.Weights[0], 1e-6, $"vertex {i} weight 0");
            Assert.True(vb.ExtraUv.Length == a.AdditionalUvCount && (a.AdditionalUvCount == 0 || vb.ExtraUv[0] == va.ExtraUv[0]), $"vertex {i} extra uv");
        }
        Assert.True(a.Indices.SequenceEqual(b.Indices), "indices");
        Assert.True(a.Textures.SequenceEqual(b.Textures), "textures");
        Assert.Equal(a.Materials.Count, b.Materials.Count, "material count");
        for (int i = 0; i < a.Materials.Count; i++)
        {
            var ma = a.Materials[i]; var mb = b.Materials[i];
            Assert.True(ma.Name == mb.Name && ma.Diffuse == mb.Diffuse && ma.Flags == mb.Flags && ma.TextureIndex == mb.TextureIndex
                && ma.SharedToon == mb.SharedToon && ma.ToonIndex == mb.ToonIndex && ma.IndexCount == mb.IndexCount && ma.EdgeColor == mb.EdgeColor, $"material {i}");
        }
        Assert.Equal(a.Bones.Count, b.Bones.Count, "bone count");
        for (int i = 0; i < a.Bones.Count; i++)
        {
            var ba = a.Bones[i]; var bb = b.Bones[i];
            Assert.True(ba.Name == bb.Name && ba.NameEn == bb.NameEn && ba.Position == bb.Position && ba.Parent == bb.Parent && ba.Flags == bb.Flags
                && ba.TailBone == bb.TailBone && ba.TailOffset == bb.TailOffset && ba.InheritParent == bb.InheritParent && ba.FixedAxis == bb.FixedAxis
                && ba.LocalX == bb.LocalX, $"bone {i} ({ba.Name})");
            Assert.Equal(ba.Ik is null, bb.Ik is null, $"bone {i} IK presence");
            if (ba.Ik != null)
                Assert.True(ba.Ik.Target == bb.Ik!.Target && ba.Ik.Links.Count == bb.Ik.Links.Count && ba.Ik.Links[0].Max == bb.Ik.Links[0].Max, $"bone {i} IK");
        }
        Assert.Equal(a.Morphs.Count, b.Morphs.Count, "morph count");
        for (int i = 0; i < a.Morphs.Count; i++)
        {
            var ma = a.Morphs[i]; var mb = b.Morphs[i];
            Assert.True(ma.Name == mb.Name && ma.Type == mb.Type && ma.Vertex.SequenceEqual(mb.Vertex) && ma.Group.SequenceEqual(mb.Group)
                && ma.Bone.Count == mb.Bone.Count && ma.Uv.Count == mb.Uv.Count && ma.Material.Count == mb.Material.Count, $"morph {i} ({ma.Name})");
        }
        Assert.Equal(a.DisplayFrames.Count, b.DisplayFrames.Count, "display frames");
        Assert.True(a.DisplayFrames[1].Items.SequenceEqual(b.DisplayFrames[1].Items), "display frame items");
        Assert.Equal(a.RigidBodies.Count, b.RigidBodies.Count, "rigid bodies");
        for (int i = 0; i < a.RigidBodies.Count; i++)
        {
            var ra = a.RigidBodies[i]; var rb = b.RigidBodies[i];
            Assert.True(ra.Name == rb.Name && ra.Bone == rb.Bone && ra.Shape == rb.Shape && ra.Size == rb.Size && ra.Mode == rb.Mode
                && ra.CollisionMask == rb.CollisionMask && ra.Rotation == rb.Rotation && ra.Mass == rb.Mass, $"rigid body {i}");
        }
        Assert.Equal(a.Joints.Count, b.Joints.Count, "joints");
        for (int i = 0; i < a.Joints.Count; i++)
            Assert.True(a.Joints[i].BodyA == b.Joints[i].BodyA && a.Joints[i].BodyB == b.Joints[i].BodyB && a.Joints[i].RotationMax == b.Joints[i].RotationMax
                && a.Joints[i].RotationSpring == b.Joints[i].RotationSpring, $"joint {i}");
    }
}
