using System.Numerics;

namespace Pmx2Vrm.Pmx;

/// <summary>A PMX 2.0/2.1 model as stored in the file (MMD units, left-handed, model facing -Z).</summary>
public sealed class PmxModel
{
    public float Version = 2.0f;
    /// <summary>0 = UTF-16LE, 1 = UTF-8.</summary>
    public byte Encoding;
    public byte AdditionalUvCount;
    public byte VertexIndexSize = 4, TextureIndexSize = 4, MaterialIndexSize = 4,
        BoneIndexSize = 4, MorphIndexSize = 4, RigidBodyIndexSize = 4;

    public string Name = "", NameEn = "", Comment = "", CommentEn = "";

    public List<PmxVertex> Vertices = new();
    /// <summary>Triangle list, three vertex indices per face (as stored: MMD/DirectX winding).</summary>
    public List<int> Indices = new();
    public List<string> Textures = new();
    public List<PmxMaterial> Materials = new();
    public List<PmxBone> Bones = new();
    public List<PmxMorph> Morphs = new();
    public List<PmxDisplayFrame> DisplayFrames = new();
    public List<PmxRigidBody> RigidBodies = new();
    public List<PmxJoint> Joints = new();
    /// <summary>PMX 2.1 soft bodies are not read; this counts them when present.</summary>
    public int SoftBodyCount;
}

public enum PmxWeightType : byte { Bdef1 = 0, Bdef2 = 1, Bdef4 = 2, Sdef = 3, Qdef = 4 }

public sealed class PmxVertex
{
    public Vector3 Position, Normal;
    public Vector2 Uv;
    public Vector4[] ExtraUv = Array.Empty<Vector4>();
    public PmxWeightType WeightType;
    /// <summary>Up to four bone indices (-1 = unused).</summary>
    public int[] Bones = { -1, -1, -1, -1 };
    public float[] Weights = new float[4];
    public Vector3 SdefC, SdefR0, SdefR1;
    public float EdgeScale = 1;
}

[Flags]
public enum PmxMaterialFlags : byte
{
    None = 0, DoubleSided = 1, GroundShadow = 2, CastShadow = 4, ReceiveShadow = 8,
    Edge = 16, VertexColor = 32, PointDraw = 64, LineDraw = 128,
}

public sealed class PmxMaterial
{
    public string Name = "", NameEn = "";
    public Vector4 Diffuse = Vector4.One;
    public Vector3 Specular;
    public float SpecularPower;
    public Vector3 Ambient;
    public PmxMaterialFlags Flags;
    public Vector4 EdgeColor = new(0, 0, 0, 1);
    public float EdgeSize = 1;
    public int TextureIndex = -1, SphereIndex = -1;
    /// <summary>0 = off, 1 = multiply (.sph), 2 = add (.spa), 3 = sub-texture.</summary>
    public byte SphereMode;
    /// <summary>True: <see cref="ToonIndex"/> is 0-9 for MMD's shared toon01-10.bmp; false: a texture index.</summary>
    public bool SharedToon;
    public int ToonIndex = -1;
    public string Memo = "";
    /// <summary>Number of indices (3 per face) this material draws, in file order.</summary>
    public int IndexCount;
}

[Flags]
public enum PmxBoneFlags : ushort
{
    None = 0,
    TailIsBone = 0x0001, Rotatable = 0x0002, Translatable = 0x0004, Visible = 0x0008,
    Enabled = 0x0010, Ik = 0x0020, InheritRotation = 0x0100, InheritTranslation = 0x0200,
    FixedAxis = 0x0400, LocalAxis = 0x0800, PhysicsAfterDeform = 0x1000, ExternalParent = 0x2000,
}

public sealed class PmxBone
{
    public string Name = "", NameEn = "";
    public Vector3 Position;
    public int Parent = -1;
    public int Layer;
    public PmxBoneFlags Flags = PmxBoneFlags.Rotatable | PmxBoneFlags.Visible | PmxBoneFlags.Enabled;
    public int TailBone = -1;
    public Vector3 TailOffset;
    public int InheritParent = -1;
    public float InheritWeight;
    public Vector3 FixedAxis;
    public Vector3 LocalX, LocalZ;
    public int ExternalKey;
    public PmxIk? Ik;
    public override string ToString() => Name;
}

public sealed class PmxIk
{
    public int Target = -1;
    public int Loops;
    public float LimitAngle;
    public List<PmxIkLink> Links = new();
}

public sealed class PmxIkLink
{
    public int Bone = -1;
    public bool HasLimits;
    public Vector3 Min, Max;
}

public enum PmxMorphType : byte
{
    Group = 0, Vertex = 1, Bone = 2, Uv = 3, Uv1 = 4, Uv2 = 5, Uv3 = 6, Uv4 = 7,
    Material = 8, Flip = 9, Impulse = 10,
}

public sealed class PmxMorph
{
    public string Name = "", NameEn = "";
    /// <summary>1 = eyebrow, 2 = eye, 3 = mouth, 4 = other (0 = system).</summary>
    public byte Panel;
    public PmxMorphType Type;
    /// <summary>Group and flip morphs.</summary>
    public List<(int Morph, float Weight)> Group = new();
    public List<(int Vertex, Vector3 Offset)> Vertex = new();
    public List<(int Bone, Vector3 Translation, Quaternion Rotation)> Bone = new();
    public List<(int Vertex, Vector4 Offset)> Uv = new();
    public List<PmxMaterialOffset> Material = new();
    public List<(int Body, bool Local, Vector3 Velocity, Vector3 Torque)> Impulse = new();
    public override string ToString() => Name;
}

public sealed class PmxMaterialOffset
{
    /// <summary>-1 = every material.</summary>
    public int Material = -1;
    /// <summary>0 = multiply, 1 = add.</summary>
    public byte Operation;
    public Vector4 Diffuse;
    public Vector3 Specular;
    public float SpecularPower;
    public Vector3 Ambient;
    public Vector4 EdgeColor;
    public float EdgeSize;
    public Vector4 TextureTint, SphereTint, ToonTint;
}

public sealed class PmxDisplayFrame
{
    public string Name = "", NameEn = "";
    public bool Special;
    /// <summary>(0 = bone / 1 = morph, index)</summary>
    public List<(byte Kind, int Index)> Items = new();
}

public enum PmxRigidShape : byte { Sphere = 0, Box = 1, Capsule = 2 }

/// <summary>0 = follows its bone (kinematic), 1 = physics, 2 = physics with the bone's position.</summary>
public enum PmxPhysicsMode : byte { FollowBone = 0, Physics = 1, PhysicsWithBone = 2 }

public sealed class PmxRigidBody
{
    public string Name = "", NameEn = "";
    public int Bone = -1;
    public byte Group;
    /// <summary>Bit i set = collides with group i (PMX Editor shows the cleared bits as "no collision").</summary>
    public ushort CollisionMask = 0xFFFF;
    public PmxRigidShape Shape;
    /// <summary>Sphere: x = radius. Box: half extents. Capsule: x = radius, y = height of the cylinder part.</summary>
    public Vector3 Size;
    public Vector3 Position;
    /// <summary>Euler angles in radians (applied Y, X, Z).</summary>
    public Vector3 Rotation;
    public float Mass = 1, LinearDamping, AngularDamping, Restitution, Friction;
    public PmxPhysicsMode Mode;
    public override string ToString() => Name;
}

public sealed class PmxJoint
{
    public string Name = "", NameEn = "";
    /// <summary>0 = spring 6DOF (the only type in PMX 2.0).</summary>
    public byte Type;
    public int BodyA = -1, BodyB = -1;
    public Vector3 Position, Rotation;
    public Vector3 PositionMin, PositionMax, RotationMin, RotationMax;
    public Vector3 PositionSpring, RotationSpring;
    public override string ToString() => Name;
}
