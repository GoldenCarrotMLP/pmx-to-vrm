using System.Numerics;
using Pmx2Vrm.Pmx;

namespace Pmx2Vrm.Tests;

/// <summary>
/// A small MMD-style humanoid built in code: standard Japanese bone names, A-pose arms, a D-bone left leg,
/// a three-bone hair chain with physics, a few morphs, two materials and a BMP texture.
/// </summary>
public static class SyntheticModel
{
    public const int Hair1 = 27, Hair2 = 28, Hair3 = 29;

    public static PmxModel Build()
    {
        var m = new PmxModel
        {
            Version = 2.0f, Encoding = 0, AdditionalUvCount = 1,
            VertexIndexSize = 2, TextureIndexSize = 1, MaterialIndexSize = 1, BoneIndexSize = 2, MorphIndexSize = 1, RigidBodyIndexSize = 1,
            Name = "テストモデル", NameEn = "Test Model",
            Comment = "モデル製作：テスト作者\r\n改変OK", CommentEn = "Model: Test Author",
        };

        int B(string name, float x, float y, float z, int parent, string en = "")
        {
            m.Bones.Add(new PmxBone { Name = name, NameEn = en, Position = new Vector3(x, y, z), Parent = parent });
            return m.Bones.Count - 1;
        }
        // A-pose: arms 35 degrees below horizontal.
        float c = MathF.Cos(35 * MathF.PI / 180), s = MathF.Sin(35 * MathF.PI / 180);
        B("全ての親", 0, 0, 0, -1, "master");           // 0
        B("センター", 0, 8, 0, 0, "center");            // 1
        B("上半身", 0, 11, 0, 1, "upper body");         // 2
        B("上半身2", 0, 13, 0, 2, "upper body2");       // 3
        B("首", 0, 16, 0, 3, "neck");                   // 4
        B("頭", 0, 17, 0, 4, "head");                   // 5
        B("左目", 0.6f, 18, -0.8f, 5, "eye_L");         // 6
        B("右目", -0.6f, 18, -0.8f, 5, "eye_R");        // 7
        for (int side = 0; side < 2; side++)
        {
            float sx = side == 0 ? 1 : -1;
            string p = side == 0 ? "左" : "右";
            int sh = B(p + "肩", 0.4f * sx, 15.5f, 0, 3);                             // 8 / 13
            int arm = B(p + "腕", 1.5f * sx, 15.3f, 0, sh);                           // 9 / 14
            int elbow = B(p + "ひじ", (1.5f + 3 * c) * sx, 15.3f - 3 * s, 0, arm);    // 10 / 15
            int wrist = B(p + "手首", (1.5f + 5.5f * c) * sx, 15.3f - 5.5f * s, 0, elbow); // 11 / 16
            B(p + "人指１", (1.5f + 6.5f * c) * sx, 15.3f - 6.5f * s, 0, wrist);      // 12 / 17
        }
        B("下半身", 0, 11, 0, 1, "lower body");          // 18
        for (int side = 0; side < 2; side++)
        {
            float sx = side == 0 ? 1 : -1;
            string p = side == 0 ? "左" : "右";
            int leg = B(p + "足", 0.9f * sx, 10, 0, 18);     // 19 / 23
            int knee = B(p + "ひざ", 0.9f * sx, 5.5f, 0, leg); // 20 / 24
            int ankle = B(p + "足首", 0.9f * sx, 1.2f, 0, knee); // 21 / 25
            B(p + "つま先", 0.9f * sx, 0, -1.2f, ankle);     // 22 / 26
        }
        B("髪1", 0, 18, 1.2f, 5);       // 27
        B("髪2", 0, 16, 1.6f, Hair1);   // 28
        B("髪3", 0, 14, 1.8f, Hair2);   // 29
        // D-bones for the left leg only (they carry the weights; the plain leg chain carries none).
        int legD = B("左足D", 0.9f, 10, 0, 18);          // 30
        int kneeD = B("左ひざD", 0.9f, 5.5f, 0, legD);   // 31
        int ankleD = B("左足首D", 0.9f, 1.2f, 0, kneeD); // 32
        var ikBone = new PmxBone { Name = "左足ＩＫ", Position = new Vector3(0.9f, 1.2f, 0), Parent = 0, TailOffset = new Vector3(0, 0, 1) };
        ikBone.Flags |= PmxBoneFlags.Ik | PmxBoneFlags.Translatable;
        ikBone.Ik = new PmxIk { Target = 21, Loops = 40, LimitAngle = 2, Links = { new PmxIkLink { Bone = 20, HasLimits = true, Min = new Vector3(-3.14f, 0, 0), Max = new Vector3(-0.01f, 0, 0) }, new PmxIkLink { Bone = 19 } } };
        m.Bones.Add(ikBone);                                // 33
        // Tail by bone index, inherit rotation, fixed and local axes: exercise the optional bone fields.
        m.Bones[2].Flags |= PmxBoneFlags.TailIsBone; m.Bones[2].TailBone = 3;
        m.Bones[30].Flags |= PmxBoneFlags.InheritRotation; m.Bones[30].InheritParent = 19; m.Bones[30].InheritWeight = 1;
        m.Bones[27].Flags |= PmxBoneFlags.FixedAxis; m.Bones[27].FixedAxis = new Vector3(0, -1, 0);
        m.Bones[9].Flags |= PmxBoneFlags.LocalAxis; m.Bones[9].LocalX = new Vector3(1, 0, 0); m.Bones[9].LocalZ = new Vector3(0, 0, 1);
        m.Bones[29].TailOffset = new Vector3(0, -2, 0.2f);

        // Geometry: a thin box along each weighted segment (bone head to child head).
        var bodyIdx = new List<int>();
        var hairIdx = new List<int>();
        void Seg(int bone, int child, List<int> into, int bone2 = -1)
        {
            var a = m.Bones[bone].Position;
            var b = child >= 0 ? m.Bones[child].Position : a + new Vector3(0, -1, 0);
            Box(m, into, a, b, 0.35f, bone, bone2);
        }
        Seg(2, 3, bodyIdx); Seg(3, 4, bodyIdx); Seg(4, 5, bodyIdx);
        Box(m, bodyIdx, new Vector3(0, 16.8f, 0), new Vector3(0, 19.5f, 0), 1.4f, 5, -1); // head
        for (int side = 0; side < 2; side++)
        {
            int o = side * 5;
            Seg(8 + o, 9 + o, bodyIdx); Seg(9 + o, 10 + o, bodyIdx, 10 + o); Seg(10 + o, 11 + o, bodyIdx); Seg(11 + o, 12 + o, bodyIdx);
        }
        Seg(18, 19, bodyIdx);
        Seg(30, 31, bodyIdx); Seg(31, 32, bodyIdx); Seg(32, 22, bodyIdx);     // left leg on D-bones
        Seg(23, 24, bodyIdx); Seg(24, 25, bodyIdx); Seg(25, 26, bodyIdx);     // right leg plain
        Seg(Hair1, Hair2, hairIdx); Seg(Hair2, Hair3, hairIdx); Seg(Hair3, -1, hairIdx);
        // One SDEF and one BDEF4 vertex to cover the other weight types.
        m.Vertices[0].WeightType = PmxWeightType.Sdef; m.Vertices[0].Bones[1] = 3; m.Vertices[0].Weights[0] = 0.7f; m.Vertices[0].Weights[1] = 0.3f;
        m.Vertices[1].WeightType = PmxWeightType.Bdef4; m.Vertices[1].Bones = new[] { 2, 3, 2, -1 }; m.Vertices[1].Weights = new[] { 0.5f, 0.3f, 0.2f, 0 };
        m.Indices.AddRange(bodyIdx);
        m.Indices.AddRange(hairIdx);

        m.Textures.Add("tex\\body.bmp");
        m.Textures.Add("toon_body.bmp");
        m.Materials.Add(new PmxMaterial
        {
            Name = "体", NameEn = "body", Diffuse = new Vector4(1, 1, 1, 1), Ambient = new Vector3(0.5f), Specular = new Vector3(0.1f), SpecularPower = 5,
            Flags = PmxMaterialFlags.Edge | PmxMaterialFlags.CastShadow, EdgeColor = new Vector4(0.2f, 0.1f, 0.1f, 1), EdgeSize = 1,
            TextureIndex = 0, ToonIndex = 1, SharedToon = false, IndexCount = bodyIdx.Count,
        });
        m.Materials.Add(new PmxMaterial
        {
            Name = "髪", NameEn = "hair", Diffuse = new Vector4(0.2f, 0.4f, 0.9f, 0.9f), Ambient = new Vector3(0.1f, 0.2f, 0.45f),
            Flags = PmxMaterialFlags.DoubleSided, SharedToon = true, ToonIndex = 2, IndexCount = hairIdx.Count,
        });

        // Morphs. Head box vertices: x > 0 is the model's left (+X in MMD, model facing -Z).
        var headVerts = Enumerable.Range(0, m.Vertices.Count).Where(i => m.Vertices[i].Bones[0] == 5).ToList();
        PmxMorph VMorph(string name, string en, byte panel, Func<Vector3, bool> pick, Vector3 off)
        {
            var mo = new PmxMorph { Name = name, NameEn = en, Panel = panel, Type = PmxMorphType.Vertex };
            foreach (var i in headVerts) if (pick(m.Vertices[i].Position)) mo.Vertex.Add((i, off));
            m.Morphs.Add(mo);
            return mo;
        }
        VMorph("あ", "a", 3, p => p.Y < 17.5f, new Vector3(0, -0.3f, 0));                      // 0
        VMorph("まばたき", "blink", 2, p => p.Y > 18.5f, new Vector3(0, -0.2f, 0));             // 1
        VMorph("ウィンク２", "wink2", 2, p => p.Y > 18.5f && p.X > 0, new Vector3(0, -0.2f, 0)); // 2 (model's left)
        VMorph("ｳｨﾝｸ２右", "wink2_R", 2, p => p.Y > 18.5f && p.X < 0, new Vector3(0, -0.2f, 0)); // 3 half-width name on purpose
        m.Morphs.Add(new PmxMorph
        {
            Name = "照れ", NameEn = "blush", Panel = 4, Type = PmxMorphType.Material,
            Material = { new PmxMaterialOffset { Material = 0, Operation = 0, Diffuse = new Vector4(1, 0.8f, 0.8f, 1), TextureTint = Vector4.One, SphereTint = Vector4.One, ToonTint = Vector4.One, EdgeColor = Vector4.One, EdgeSize = 1, Specular = Vector3.One, Ambient = Vector3.One, SpecularPower = 1 } },
        }); // 4
        m.Morphs.Add(new PmxMorph { Name = "笑い", NameEn = "smile", Panel = 2, Type = PmxMorphType.Group, Group = { (1, 0.5f), (0, 0.3f), (4, 1f) } }); // 5
        m.Morphs.Add(new PmxMorph { Name = "首かしげ", Panel = 4, Type = PmxMorphType.Bone, Bone = { (4, Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.2f)) } }); // 6
        m.Morphs.Add(new PmxMorph { Name = "UVスクロール", Panel = 4, Type = PmxMorphType.Uv, Uv = { (0, new Vector4(0.1f, 0, 0, 0)) } }); // 7
        m.Morphs.Add(new PmxMorph { Name = "怒り", Panel = 1, Type = PmxMorphType.Group, Group = { (6, 1f), (0, 0.2f) } }); // 8: has a bone part

        m.DisplayFrames.Add(new PmxDisplayFrame { Name = "Root", NameEn = "Root", Special = true, Items = { (0, 0) } });
        m.DisplayFrames.Add(new PmxDisplayFrame { Name = "表情", NameEn = "Exp", Special = true, Items = { (1, 0), (1, 1) } });

        // Physics: head sphere and leg capsules follow their bones; the hair chain is simulated.
        m.RigidBodies.Add(new PmxRigidBody { Name = "頭", Bone = 5, Group = 0, CollisionMask = 0xFFFF, Shape = PmxRigidShape.Sphere, Size = new Vector3(1.6f, 0, 0), Position = new Vector3(0, 18, 0), Mass = 1, LinearDamping = 0.5f, AngularDamping = 0.5f, Mode = PmxPhysicsMode.FollowBone });
        m.RigidBodies.Add(new PmxRigidBody { Name = "左足", Bone = 30, Group = 1, CollisionMask = 0xFFFF, Shape = PmxRigidShape.Capsule, Size = new Vector3(0.6f, 3.5f, 0), Position = new Vector3(0.9f, 7.75f, 0), Mass = 1, Mode = PmxPhysicsMode.FollowBone });
        m.RigidBodies.Add(new PmxRigidBody { Name = "右足", Bone = 23, Group = 1, CollisionMask = 0xFFFF, Shape = PmxRigidShape.Capsule, Size = new Vector3(0.6f, 3.5f, 0), Position = new Vector3(-0.9f, 7.75f, 0), Mass = 1, Mode = PmxPhysicsMode.FollowBone });
        ushort hairMask = unchecked((ushort)~(1 << 2) & ~(1 << 1)); // not with itself, not with the legs
        m.RigidBodies.Add(new PmxRigidBody { Name = "髪1", Bone = Hair1, Group = 2, CollisionMask = hairMask, Shape = PmxRigidShape.Capsule, Size = new Vector3(0.3f, 1.5f, 0), Position = new Vector3(0, 17, 1.4f), Rotation = new Vector3(0.2f, 0, 0), Mass = 0.5f, LinearDamping = 0.5f, AngularDamping = 0.5f, Mode = PmxPhysicsMode.Physics });
        m.RigidBodies.Add(new PmxRigidBody { Name = "髪2", Bone = Hair2, Group = 2, CollisionMask = hairMask, Shape = PmxRigidShape.Sphere, Size = new Vector3(0.4f, 0, 0), Position = new Vector3(0, 15, 1.7f), Mass = 0.5f, LinearDamping = 0.5f, AngularDamping = 0.5f, Mode = PmxPhysicsMode.PhysicsWithBone });
        m.RigidBodies.Add(new PmxRigidBody { Name = "上半身", Bone = 2, Group = 0, CollisionMask = 0xFFFF, Shape = PmxRigidShape.Box, Size = new Vector3(1.2f, 1.5f, 0.8f), Position = new Vector3(0, 12.5f, 0), Mass = 1, Mode = PmxPhysicsMode.FollowBone });
        m.Joints.Add(new PmxJoint { Name = "髪1", BodyA = 0, BodyB = 3, Position = new Vector3(0, 18, 1.2f), RotationMin = new Vector3(-0.5f, -0.2f, -0.3f), RotationMax = new Vector3(0.5f, 0.2f, 0.3f), RotationSpring = new Vector3(10, 10, 10) });
        m.Joints.Add(new PmxJoint { Name = "髪2", BodyA = 3, BodyB = 4, Position = new Vector3(0, 16, 1.6f), RotationMin = new Vector3(-0.8f, -0.4f, -0.4f), RotationMax = new Vector3(0.8f, 0.4f, 0.4f) });
        return m;
    }

    static void Box(PmxModel m, List<int> idx, Vector3 a, Vector3 b, float half, int bone, int bone2)
    {
        var axis = b - a;
        float len = axis.Length();
        var d = len > 1e-5f ? axis / len : Vector3.UnitY;
        var up = MathF.Abs(d.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY;
        var u = Vector3.Normalize(Vector3.Cross(d, up)) * half;
        var v = Vector3.Normalize(Vector3.Cross(d, u)) * half;
        int start = m.Vertices.Count;
        for (int e = 0; e < 2; e++)
        {
            var p0 = e == 0 ? a : b;
            var corners = new[] { p0 + u + v, p0 - u + v, p0 - u - v, p0 + u - v };
            for (int k = 0; k < 4; k++)
            {
                var vert = new PmxVertex
                {
                    Position = corners[k], Normal = Vector3.Normalize(corners[k] - p0 + d * 0.001f),
                    Uv = new Vector2(k / 3f, e), ExtraUv = new[] { new Vector4(1, 2, 3, 4) },
                    WeightType = PmxWeightType.Bdef1, EdgeScale = 1,
                };
                vert.Bones[0] = bone; vert.Weights[0] = 1;
                if (bone2 >= 0 && e == 1)
                {
                    vert.WeightType = PmxWeightType.Bdef2; vert.Bones[1] = bone2; vert.Weights[0] = 0.5f; vert.Weights[1] = 0.5f;
                }
                m.Vertices.Add(vert);
            }
        }
        // Sides (quads as two triangles), MMD winding (clockwise seen from outside).
        for (int k = 0; k < 4; k++)
        {
            int k2 = (k + 1) % 4;
            int a0 = start + k, a1 = start + k2, b0 = start + 4 + k, b1 = start + 4 + k2;
            idx.AddRange(new[] { a0, b0, a1, a1, b0, b1 });
        }
        idx.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
        idx.AddRange(new[] { start + 4, start + 6, start + 5, start + 4, start + 7, start + 6 });
    }

    /// <summary>A 4x2 24-bit BMP (bottom-up) with known colors.</summary>
    public static byte[] Bmp24()
    {
        int w = 4, h = 2, stride = 12; // 4 px * 3 bytes, already a multiple of 4
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write((byte)'B'); bw.Write((byte)'M'); bw.Write(54 + stride * h); bw.Write(0); bw.Write(54);
        bw.Write(40); bw.Write(w); bw.Write(h); bw.Write((short)1); bw.Write((short)24); bw.Write(0); bw.Write(stride * h); bw.Write(2835); bw.Write(2835); bw.Write(0); bw.Write(0);
        // Bottom row first: dark grey; top row: red, green, blue, white.
        for (int x = 0; x < w; x++) { bw.Write((byte)60); bw.Write((byte)50); bw.Write((byte)40); }
        bw.Write(new byte[] { 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255 });
        return ms.ToArray();
    }
}
