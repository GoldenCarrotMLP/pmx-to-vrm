using System.Numerics;
using System.Text;

namespace Pmx2Vrm.Pmx;

/// <summary>Reads PMX 2.0 / 2.1 files (written from the public PMX 2.0/2.1 format description).</summary>
public static class PmxReader
{
    public static PmxModel Read(string path)
    {
        using var fs = File.OpenRead(path);
        return Read(fs);
    }

    public static PmxModel Read(byte[] data)
    {
        using var ms = new MemoryStream(data, false);
        return Read(ms);
    }

    public static PmxModel Read(Stream stream)
    {
        var r = new Reader(new BinaryReader(stream, Encoding.UTF8, leaveOpen: true));
        var m = new PmxModel();
        var magic = r.B.ReadBytes(4);
        if (magic.Length < 4 || magic[0] != 'P' || magic[1] != 'M' || magic[2] != 'X')
            throw new InvalidDataException("Not a PMX file (missing 'PMX ' signature). PMD (the older format) is not supported.");
        m.Version = r.B.ReadSingle();
        if (m.Version < 2.0f - 1e-4 || m.Version > 2.1f + 1e-4)
            throw new InvalidDataException($"Unsupported PMX version {m.Version}.");
        int globals = r.B.ReadByte();
        var g = r.B.ReadBytes(globals);
        if (g.Length < 8) throw new InvalidDataException("PMX header too short.");
        m.Encoding = g[0];
        m.AdditionalUvCount = g[1];
        m.VertexIndexSize = g[2]; m.TextureIndexSize = g[3]; m.MaterialIndexSize = g[4];
        m.BoneIndexSize = g[5]; m.MorphIndexSize = g[6]; m.RigidBodyIndexSize = g[7];
        if (m.AdditionalUvCount > 4) throw new InvalidDataException($"Invalid additional UV count {m.AdditionalUvCount}.");
        r.Utf8 = m.Encoding == 1;
        r.M = m;

        m.Name = r.Text(); m.NameEn = r.Text(); m.Comment = r.Text(); m.CommentEn = r.Text();

        int vc = r.Count("vertex");
        m.Vertices.Capacity = vc;
        for (int i = 0; i < vc; i++)
        {
            var v = new PmxVertex
            {
                Position = r.V3(), Normal = r.V3(), Uv = r.V2(),
            };
            if (m.AdditionalUvCount > 0)
            {
                v.ExtraUv = new Vector4[m.AdditionalUvCount];
                for (int k = 0; k < m.AdditionalUvCount; k++) v.ExtraUv[k] = r.V4();
            }
            v.WeightType = (PmxWeightType)r.B.ReadByte();
            switch (v.WeightType)
            {
                case PmxWeightType.Bdef1:
                    v.Bones[0] = r.Bone(); v.Weights[0] = 1;
                    break;
                case PmxWeightType.Bdef2:
                    v.Bones[0] = r.Bone(); v.Bones[1] = r.Bone();
                    v.Weights[0] = r.B.ReadSingle(); v.Weights[1] = 1 - v.Weights[0];
                    break;
                case PmxWeightType.Bdef4:
                case PmxWeightType.Qdef:
                    for (int k = 0; k < 4; k++) v.Bones[k] = r.Bone();
                    for (int k = 0; k < 4; k++) v.Weights[k] = r.B.ReadSingle();
                    break;
                case PmxWeightType.Sdef:
                    v.Bones[0] = r.Bone(); v.Bones[1] = r.Bone();
                    v.Weights[0] = r.B.ReadSingle(); v.Weights[1] = 1 - v.Weights[0];
                    v.SdefC = r.V3(); v.SdefR0 = r.V3(); v.SdefR1 = r.V3();
                    break;
                default:
                    throw new InvalidDataException($"Vertex {i}: unknown weight type {(int)v.WeightType}.");
            }
            v.EdgeScale = r.B.ReadSingle();
            m.Vertices.Add(v);
        }

        int ic = r.Count("index");
        m.Indices.Capacity = ic;
        for (int i = 0; i < ic; i++) m.Indices.Add(r.VertexIndex());

        int tc = r.Count("texture");
        for (int i = 0; i < tc; i++) m.Textures.Add(r.Text());

        int mc = r.Count("material");
        for (int i = 0; i < mc; i++)
        {
            var mat = new PmxMaterial
            {
                Name = r.Text(), NameEn = r.Text(),
                Diffuse = r.V4(), Specular = r.V3(), SpecularPower = r.B.ReadSingle(), Ambient = r.V3(),
                Flags = (PmxMaterialFlags)r.B.ReadByte(),
                EdgeColor = r.V4(), EdgeSize = r.B.ReadSingle(),
                TextureIndex = r.Index(m.TextureIndexSize), SphereIndex = r.Index(m.TextureIndexSize),
                SphereMode = r.B.ReadByte(),
            };
            mat.SharedToon = r.B.ReadByte() == 1;
            mat.ToonIndex = mat.SharedToon ? r.B.ReadByte() : r.Index(m.TextureIndexSize);
            mat.Memo = r.Text();
            mat.IndexCount = r.B.ReadInt32();
            m.Materials.Add(mat);
        }

        int bc = r.Count("bone");
        for (int i = 0; i < bc; i++)
        {
            var b = new PmxBone { Name = r.Text(), NameEn = r.Text(), Position = r.V3() };
            b.Parent = r.Bone();
            b.Layer = r.B.ReadInt32();
            b.Flags = (PmxBoneFlags)r.B.ReadUInt16();
            if (b.Flags.HasFlag(PmxBoneFlags.TailIsBone)) b.TailBone = r.Bone();
            else b.TailOffset = r.V3();
            if ((b.Flags & (PmxBoneFlags.InheritRotation | PmxBoneFlags.InheritTranslation)) != 0)
            {
                b.InheritParent = r.Bone();
                b.InheritWeight = r.B.ReadSingle();
            }
            if (b.Flags.HasFlag(PmxBoneFlags.FixedAxis)) b.FixedAxis = r.V3();
            if (b.Flags.HasFlag(PmxBoneFlags.LocalAxis)) { b.LocalX = r.V3(); b.LocalZ = r.V3(); }
            if (b.Flags.HasFlag(PmxBoneFlags.ExternalParent)) b.ExternalKey = r.B.ReadInt32();
            if (b.Flags.HasFlag(PmxBoneFlags.Ik))
            {
                var ik = new PmxIk { Target = r.Bone(), Loops = r.B.ReadInt32(), LimitAngle = r.B.ReadSingle() };
                int lc = r.B.ReadInt32();
                if (lc < 0 || lc > 4096) throw new InvalidDataException($"Bone {i}: bad IK link count {lc}.");
                for (int k = 0; k < lc; k++)
                {
                    var link = new PmxIkLink { Bone = r.Bone(), HasLimits = r.B.ReadByte() != 0 };
                    if (link.HasLimits) { link.Min = r.V3(); link.Max = r.V3(); }
                    ik.Links.Add(link);
                }
                b.Ik = ik;
            }
            m.Bones.Add(b);
        }

        int morphCount = r.Count("morph");
        for (int i = 0; i < morphCount; i++)
        {
            var mo = new PmxMorph { Name = r.Text(), NameEn = r.Text(), Panel = r.B.ReadByte(), Type = (PmxMorphType)r.B.ReadByte() };
            int oc = r.Count("morph offset");
            for (int k = 0; k < oc; k++)
            {
                switch (mo.Type)
                {
                    case PmxMorphType.Group:
                    case PmxMorphType.Flip:
                        mo.Group.Add((r.Index(m.MorphIndexSize), r.B.ReadSingle()));
                        break;
                    case PmxMorphType.Vertex:
                        mo.Vertex.Add((r.VertexIndex(), r.V3()));
                        break;
                    case PmxMorphType.Bone:
                        mo.Bone.Add((r.Bone(), r.V3(), r.Quat()));
                        break;
                    case PmxMorphType.Uv:
                    case PmxMorphType.Uv1:
                    case PmxMorphType.Uv2:
                    case PmxMorphType.Uv3:
                    case PmxMorphType.Uv4:
                        mo.Uv.Add((r.VertexIndex(), r.V4()));
                        break;
                    case PmxMorphType.Material:
                        mo.Material.Add(new PmxMaterialOffset
                        {
                            Material = r.Index(m.MaterialIndexSize), Operation = r.B.ReadByte(),
                            Diffuse = r.V4(), Specular = r.V3(), SpecularPower = r.B.ReadSingle(), Ambient = r.V3(),
                            EdgeColor = r.V4(), EdgeSize = r.B.ReadSingle(),
                            TextureTint = r.V4(), SphereTint = r.V4(), ToonTint = r.V4(),
                        });
                        break;
                    case PmxMorphType.Impulse:
                        mo.Impulse.Add((r.Index(m.RigidBodyIndexSize), r.B.ReadByte() != 0, r.V3(), r.V3()));
                        break;
                    default:
                        throw new InvalidDataException($"Morph {i} ({mo.Name}): unknown type {(int)mo.Type}.");
                }
            }
            m.Morphs.Add(mo);
        }

        int fc = r.Count("display frame");
        for (int i = 0; i < fc; i++)
        {
            var f = new PmxDisplayFrame { Name = r.Text(), NameEn = r.Text(), Special = r.B.ReadByte() != 0 };
            int n = r.Count("display frame item");
            for (int k = 0; k < n; k++)
            {
                byte kind = r.B.ReadByte();
                f.Items.Add((kind, kind == 0 ? r.Bone() : r.Index(m.MorphIndexSize)));
            }
            m.DisplayFrames.Add(f);
        }

        int rc = r.Count("rigid body");
        for (int i = 0; i < rc; i++)
        {
            m.RigidBodies.Add(new PmxRigidBody
            {
                Name = r.Text(), NameEn = r.Text(), Bone = r.Bone(), Group = r.B.ReadByte(),
                CollisionMask = r.B.ReadUInt16(), Shape = (PmxRigidShape)r.B.ReadByte(),
                Size = r.V3(), Position = r.V3(), Rotation = r.V3(),
                Mass = r.B.ReadSingle(), LinearDamping = r.B.ReadSingle(), AngularDamping = r.B.ReadSingle(),
                Restitution = r.B.ReadSingle(), Friction = r.B.ReadSingle(), Mode = (PmxPhysicsMode)r.B.ReadByte(),
            });
        }

        int jc = r.Count("joint");
        for (int i = 0; i < jc; i++)
        {
            m.Joints.Add(new PmxJoint
            {
                Name = r.Text(), NameEn = r.Text(), Type = r.B.ReadByte(),
                BodyA = r.Index(m.RigidBodyIndexSize), BodyB = r.Index(m.RigidBodyIndexSize),
                Position = r.V3(), Rotation = r.V3(),
                PositionMin = r.V3(), PositionMax = r.V3(), RotationMin = r.V3(), RotationMax = r.V3(),
                PositionSpring = r.V3(), RotationSpring = r.V3(),
            });
        }

        // PMX 2.1 soft bodies follow; we only count them (not converted).
        if (m.Version > 2.05f && r.B.BaseStream.Position + 4 <= r.B.BaseStream.Length)
            m.SoftBodyCount = r.B.ReadInt32();

        return m;
    }

    sealed class Reader
    {
        public readonly BinaryReader B;
        public bool Utf8;
        public PmxModel M = null!;
        public Reader(BinaryReader b) { B = b; }

        public string Text()
        {
            int len = B.ReadInt32();
            if (len < 0 || len > 1 << 24) throw new InvalidDataException($"Bad text length {len} at offset {B.BaseStream.Position - 4}.");
            if (len == 0) return "";
            var bytes = B.ReadBytes(len);
            if (bytes.Length != len) throw new EndOfStreamException();
            return (Utf8 ? Encoding.UTF8 : Encoding.Unicode).GetString(bytes).TrimEnd('\0');
        }

        public int Count(string what)
        {
            int n = B.ReadInt32();
            if (n < 0 || n > 50_000_000) throw new InvalidDataException($"Bad {what} count {n}.");
            return n;
        }

        public Vector2 V2() => new(B.ReadSingle(), B.ReadSingle());
        public Vector3 V3() => new(B.ReadSingle(), B.ReadSingle(), B.ReadSingle());
        public Vector4 V4() => new(B.ReadSingle(), B.ReadSingle(), B.ReadSingle(), B.ReadSingle());
        public Quaternion Quat() => new(B.ReadSingle(), B.ReadSingle(), B.ReadSingle(), B.ReadSingle());

        /// <summary>Vertex indices are unsigned for sizes 1 and 2.</summary>
        public int VertexIndex() => M.VertexIndexSize switch
        {
            1 => B.ReadByte(),
            2 => B.ReadUInt16(),
            4 => B.ReadInt32(),
            _ => throw new InvalidDataException($"Bad vertex index size {M.VertexIndexSize}."),
        };

        /// <summary>Other indices are signed; -1 = none.</summary>
        public int Index(int size) => size switch
        {
            1 => B.ReadSByte(),
            2 => B.ReadInt16(),
            4 => B.ReadInt32(),
            _ => throw new InvalidDataException($"Bad index size {size}."),
        };

        public int Bone() => Index(M.BoneIndexSize);
    }
}
