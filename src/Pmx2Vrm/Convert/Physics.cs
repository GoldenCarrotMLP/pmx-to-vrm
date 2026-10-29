using System.Numerics;
using System.Text.Json.Nodes;
using Pmx2Vrm.Gltf;
using Pmx2Vrm.Pmx;

namespace Pmx2Vrm.Convert;

/// <summary>A rigid body in glTF space (metres).</summary>
sealed class Body
{
    public int Index, Bone;
    public PmxRigidShape Shape;
    public PmxPhysicsMode Mode;
    public Vector3 Center, AxisX = Vector3.UnitX, AxisY = Vector3.UnitY, AxisZ = Vector3.UnitZ;
    /// <summary>Scaled: sphere x = radius; box = half extents; capsule x = radius, y = cylinder height.</summary>
    public Vector3 Size;
    public int Group;
    public ushort Mask;
    public float Mass, LinearDamping, AngularDamping;
    public string Name = "";
}

sealed class PhysicsResult
{
    public readonly List<(string Name, int Parent, Vector3 Position)> Tails = new();
    public JsonObject Json = new();
}

public sealed partial class Converter
{
    void SetupBodies()
    {
        var list = new List<Body>();
        for (int i = 0; i < _m.RigidBodies.Count; i++)
        {
            var rb = _m.RigidBodies[i];
            // Euler angles applied Y, X, Z (Bullet in MMD); built in MMD space, then mirrored with Z.
            var rot = Matrix4x4.CreateRotationZ(rb.Rotation.Z) * Matrix4x4.CreateRotationX(rb.Rotation.X) * Matrix4x4.CreateRotationY(rb.Rotation.Y);
            Vector3 Axis(Vector3 a) => Vector3.Normalize(D(Vector3.TransformNormal(a, rot)));
            var size = new Vector3(MathF.Abs(rb.Size.X), MathF.Abs(rb.Size.Y), MathF.Abs(rb.Size.Z)) * _o.Scale;
            list.Add(new Body
            {
                Index = i, Bone = rb.Bone >= 0 && rb.Bone < _nb ? rb.Bone : -1, Shape = rb.Shape, Mode = rb.Mode,
                Center = P(rb.Position), AxisX = Axis(Vector3.UnitX), AxisY = Axis(Vector3.UnitY), AxisZ = Axis(Vector3.UnitZ),
                Size = size, Group = rb.Group & 15, Mask = rb.CollisionMask, Mass = rb.Mass,
                LinearDamping = rb.LinearDamping, AngularDamping = rb.AngularDamping, Name = rb.Name,
            });
            if (rb.Bone < 0 || rb.Bone >= _nb)
                _r.Warnings.Add($"Rigid body '{rb.Name}' has no bone; ignored.");
        }
        _bodies = list.ToArray();
    }

    static bool Collides(Body a, Body b) => (a.Mask & (1 << b.Group)) != 0 && (b.Mask & (1 << a.Group)) != 0;

    /// <summary>Radius of a body for spring joints: the sphere/capsule radius, or the mean of a box's two smaller half extents.</summary>
    static float BodyRadius(Body b)
    {
        switch (b.Shape)
        {
            case PmxRigidShape.Sphere: return b.Size.X;
            case PmxRigidShape.Capsule: return b.Size.X;
            default:
                var s = new[] { b.Size.X, b.Size.Y, b.Size.Z };
                Array.Sort(s);
                return (s[0] + s[1]) / 2;
        }
    }

    int NearestHuman(int bone, Dictionary<int, string> humanOf, out string human)
    {
        for (int b = bone; b >= 0; b = _parent[b])
            if (humanOf.TryGetValue(b, out human!)) return b;
        human = "";
        return -1;
    }

    /// <summary>
    /// Rigid bodies that follow their bone become colliders on that bone; simulated bodies mark their bones
    /// as spring joints, chained along the bone hierarchy (one spring per branch). Joint stiffness comes from
    /// the PMX joint's rotation limits and springs, drag from the bodies' damping, gravity from their mass,
    /// hit radius from their size. Which colliders a spring uses follows the PMX collision groups/masks.
    /// </summary>
    PhysicsResult BuildPhysics()
    {
        var res = new PhysicsResult();
        var humanOf = _human.ToDictionary(kv => kv.Value, kv => kv.Key);
        var bodyOfBone = new Dictionary<int, Body>();
        foreach (var b in _bodies)
            if (b.Bone >= 0 && b.Mode != PmxPhysicsMode.FollowBone) bodyOfBone.TryAdd(b.Bone, b);

        bool[] simBelow = new bool[_nb];
        for (int i = 0; i < _nb; i++)
            if (_simulated[i])
                for (int p = _parent[i]; p >= 0 && !simBelow[p]; p = _parent[p]) simBelow[p] = true;
        bool UnderSimulated(int bone)
        {
            for (int p = _parent[bone]; p >= 0; p = _parent[p]) if (_simulated[p]) return true;
            return false;
        }

        // ---- colliders
        string[] regionOrder = { "Head", "Body", "LeftArm", "RightArm", "LeftLeg", "RightLeg" };
        var colliders = new JsonArray();
        var colliderBodies = new List<(Body Body, string Region)>();
        int skippedAnchors = 0, moved = 0;
        var aliases = Humanoid.Aliases(_m, _human);
        foreach (var b in _bodies)
        {
            if (b.Bone < 0 || b.Mode != PmxPhysicsMode.FollowBone) continue;
            if (_simulated[b.Bone] || UnderSimulated(b.Bone)) continue;
            int node = b.Bone;
            string human;
            if (aliases.TryGetValue(b.Bone, out var alias))
            {
                // A body on 左足 while the humanoid uses 左足D: hang the collider on the bone that actually moves.
                node = _human[alias];
                human = alias;
                moved++;
            }
            else
            {
                int hb = NearestHuman(b.Bone, humanOf, out human);
                // Anchor bodies (on non-human bones that hold physics chains, like a skirt or hair root) are not colliders.
                if (!humanOf.ContainsKey(b.Bone) && simBelow[b.Bone]) { skippedAnchors++; continue; }
                if (hb < 0) human = "";
            }
            var region = human.Length > 0 ? Humanoid.Region(human) : "Body";
            colliders.Add(new JsonObject { ["node"] = node, ["shape"] = ColliderShape(b, node) });
            colliderBodies.Add((b, region));
        }
        if (moved > 0) _r.Warnings.Add($"{moved} collider bodies sat on bones the humanoid doesn't use (e.g. 足 next to 足D); moved onto the human bones.");
        var groups = new JsonArray();
        var groupIndex = new Dictionary<string, int>();
        foreach (var region in regionOrder)
        {
            var members = new JsonArray();
            for (int c = 0; c < colliderBodies.Count; c++) if (colliderBodies[c].Region == region) members.Add(c);
            if (members.Count == 0) continue;
            groupIndex[region] = groups.Count;
            groups.Add(new JsonObject { ["name"] = region, ["colliders"] = members });
        }

        // ---- spring chains
        var chains = new List<List<int>>();
        var starts = new Queue<int>();
        for (int i = 0; i < _nb; i++)
            if (_simulated[i] && (_parent[i] < 0 || !_simulated[_parent[i]])) starts.Enqueue(i);
        var depth = new int[_nb];
        foreach (var b in _order.Reverse())
            if (_simulated[b])
                depth[b] = 1 + _children[b].Where(c => _simulated[c]).Select(c => depth[c]).DefaultIfEmpty(0).Max();
        while (starts.Count > 0)
        {
            int cur = starts.Dequeue();
            var chain = new List<int> { cur };
            while (true)
            {
                var kids = _children[cur].Where(c => _simulated[c]).OrderByDescending(c => depth[c]).ThenBy(c => c).ToList();
                if (kids.Count == 0) break;
                for (int k = 1; k < kids.Count; k++) starts.Enqueue(kids[k]);
                cur = kids[0];
                chain.Add(cur);
            }
            chains.Add(chain);
        }

        var springs = new JsonArray();
        int jointCount = 0;
        foreach (var chain in chains)
        {
            int last = chain[^1];
            int tailNode = TailFor(last, res);
            var jointNodes = new List<int>(chain) { tailNode };
            var joints = new JsonArray();
            JsonObject? lastParams = null;
            var chainBodies = new List<Body>();
            foreach (var bone in chain)
            {
                if (!bodyOfBone.TryGetValue(bone, out var body)) continue;
                chainBodies.Add(body);
            }
            for (int j = 0; j < jointNodes.Count; j++)
            {
                JsonObject p;
                if (j < chain.Count && bodyOfBone.TryGetValue(chain[j], out var body)) p = JointParams(body, bodyOfBone);
                else if (lastParams != null) p = (JsonObject)lastParams.DeepClone();
                else p = DefaultJoint();
                p["node"] = jointNodes[j];
                lastParams = p;
                joints.Add(p);
            }
            jointCount += joints.Count;

            // Collider groups: those holding a collider this chain's bodies collide with (PMX groups/masks).
            var useGroups = new SortedSet<int>();
            foreach (var (cb, region) in colliderBodies)
                if (chainBodies.Any(b => Collides(b, cb))) useGroups.Add(groupIndex[region]);
            var spring = new JsonObject { ["name"] = _nodeNames[chain[0]], ["joints"] = joints };
            if (useGroups.Count > 0) spring["colliderGroups"] = new JsonArray(useGroups.Select(g => (JsonNode)g).ToArray());
            springs.Add(spring);

            int hb = NearestHuman(chain[0], humanOf, out var human);
            var groupNames = useGroups.Select(g => groups[g]!["name"]!.GetValue<string>());
            _r.Springs.Add($"{_nodeNames[chain[0]]}: {joints.Count} joints under {(hb >= 0 ? human : "root")}, colliders: {(useGroups.Count > 0 ? string.Join("+", groupNames) : "none")}");
            CheckPenetration(chain, tailNode, res, colliderBodies.Where(c => useGroups.Contains(groupIndex[c.Region])).Select(c => c.Body).ToList(), joints);
        }

        if (skippedAnchors > 0) _r.Warnings.Add($"{skippedAnchors} kinematic anchor bodies (roots of physics chains on non-human bones) not used as colliders.");
        int unsupported = _bodies.Count(b => b.Mode != PmxPhysicsMode.FollowBone && b.Bone >= 0 && _human.ContainsValue(b.Bone));
        if (unsupported > 0) _r.Warnings.Add($"{unsupported} simulated bodies sit on bones that look humanoid; those bones were kept out of the humanoid.");

        res.Json = new JsonObject
        {
            ["specVersion"] = "1.0",
            ["colliders"] = colliders,
            ["colliderGroups"] = groups,
            ["springs"] = springs,
        };
        _r.Colliders = colliders.Count;
        _r.ColliderGroups = groups.Count;
        _r.SpringJoints = jointCount;
        return res;
    }

    /// <summary>The node after the last simulated bone: a child bone if there is one, else a new leaf node.</summary>
    int TailFor(int last, PhysicsResult res)
    {
        var bone = _m.Bones[last];
        var kids = _children[last];
        if (bone.Flags.HasFlag(PmxBoneFlags.TailIsBone) && kids.Contains(bone.TailBone)) return bone.TailBone;
        if (kids.Count > 0)
            return kids.OrderByDescending(k => (_bonePos[k] - _bonePos[last]).LengthSquared()).First();
        Vector3 off;
        if (bone.Flags.HasFlag(PmxBoneFlags.TailIsBone) && bone.TailBone >= 0 && bone.TailBone < _nb && bone.TailBone != last)
            off = _bonePos[bone.TailBone] - _bonePos[last];
        else
            off = Vector3.Transform(P(bone.TailOffset) * new Vector3(1, 1, 1), _gq[last]);
        if (off.LengthSquared() < 1e-8f)
        {
            var p = _parent[last];
            var dir = p >= 0 ? _bonePos[last] - _bonePos[p] : new Vector3(0, -0.05f, 0);
            off = dir.LengthSquared() > 1e-8f ? dir * 0.7f : new Vector3(0, -0.05f, 0);
            if (off.Length() < 0.01f) off = Vector3.Normalize(off) * 0.03f;
        }
        res.Tails.Add(($"{_nodeNames[last]}_end", last, _bonePos[last] + off));
        return _nb + res.Tails.Count - 1;
    }

    JsonObject DefaultJoint() => new()
    {
        ["hitRadius"] = GltfBuilder.Num(0.02f), ["stiffness"] = GltfBuilder.Num(1f), ["gravityPower"] = GltfBuilder.Num(0.1f),
        ["gravityDir"] = GltfBuilder.Arr(0, -1, 0), ["dragForce"] = GltfBuilder.Num(0.4f),
    };

    JsonObject JointParams(Body body, Dictionary<int, Body> bodyOfBone)
    {
        // The PMX joint holding this body: prefer one where it is body B (A is usually the parent's body).
        PmxJoint? joint = _m.Joints.FirstOrDefault(j => j.BodyB == body.Index) ?? _m.Joints.FirstOrDefault(j => j.BodyA == body.Index);
        float range = 1.0f, spring = 0;
        if (joint != null)
        {
            var span = joint.RotationMax - joint.RotationMin;
            range = (MathF.Abs(span.X) + MathF.Abs(span.Y) + MathF.Abs(span.Z)) / 3;
            if (!float.IsFinite(range)) range = 1;
            range = Math.Clamp(range, 0, MathF.Tau);
            var s = joint.RotationSpring;
            spring = (MathF.Abs(s.X) + MathF.Abs(s.Y) + MathF.Abs(s.Z)) / 3;
            if (!float.IsFinite(spring)) spring = 0;
        }
        float stiffness = Math.Clamp(0.6f / MathF.Max(range, 0.15f) + spring / 40f, 0.4f, 4f);
        float damping = (Clamp01(body.LinearDamping) + Clamp01(body.AngularDamping)) / 2;
        float drag = Math.Clamp(damping * 0.8f, 0.2f, 0.9f);
        float mass = float.IsFinite(body.Mass) ? MathF.Max(0, body.Mass) : 1;
        float gravity = Math.Clamp(0.05f + 0.1f * MathF.Min(mass, 2), 0f, 0.3f);
        float radius = Math.Clamp(BodyRadius(body), 0.005f, 0.12f);
        return new JsonObject
        {
            ["hitRadius"] = GltfBuilder.Num(radius),
            ["stiffness"] = GltfBuilder.Num(stiffness),
            ["gravityPower"] = GltfBuilder.Num(gravity),
            ["gravityDir"] = GltfBuilder.Arr(0, -1, 0),
            ["dragForce"] = GltfBuilder.Num(drag),
        };
    }

    static float Clamp01(float v) => float.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0.5f;

    /// <summary>The collider shape in its bone's local space (the nodes carry no rotation).</summary>
    JsonObject ColliderShape(Body b, int node)
    {
        var origin = _bonePos[node];
        JsonObject Sphere(Vector3 c, float r) => new() { ["sphere"] = new JsonObject { ["offset"] = GltfBuilder.Arr(c - origin), ["radius"] = GltfBuilder.Num(r) } };
        JsonObject Capsule(Vector3 a, Vector3 t, float r) => new() { ["capsule"] = new JsonObject { ["offset"] = GltfBuilder.Arr(a - origin), ["radius"] = GltfBuilder.Num(r), ["tail"] = GltfBuilder.Arr(t - origin) } };
        var (shape, a2, t2, r2) = Shape(b);
        return shape == 0 ? Sphere(a2, r2) : Capsule(a2, t2, r2);
    }

    /// <summary>A body as a sphere (kind 0: center, radius) or capsule (kind 1: end, end, radius) in world space.</summary>
    static (int Kind, Vector3 A, Vector3 B, float R) Shape(Body b)
    {
        switch (b.Shape)
        {
            case PmxRigidShape.Sphere:
                return (0, b.Center, b.Center, b.Size.X);
            case PmxRigidShape.Capsule:
            {
                var half = b.AxisY * (b.Size.Y / 2);
                return half.LengthSquared() < 1e-10f ? (0, b.Center, b.Center, b.Size.X) : (1, b.Center - half, b.Center + half, b.Size.X);
            }
            default:
            {
                // Box: a capsule along its longest axis, as thick as the mean of the other two half extents.
                var ext = new[] { (b.Size.X, b.AxisX), (b.Size.Y, b.AxisY), (b.Size.Z, b.AxisZ) }.OrderByDescending(e => e.Item1).ToArray();
                float r = (ext[1].Item1 + ext[2].Item1) / 2;
                float len = ext[0].Item1 - r;
                if (len < 0.001f) return (0, b.Center, b.Center, (b.Size.X + b.Size.Y + b.Size.Z) / 3);
                var half = ext[0].Item2 * len;
                return (1, b.Center - half, b.Center + half, r);
            }
        }
    }

    void CheckPenetration(List<int> chain, int tailNode, PhysicsResult res, List<Body> colliders, JsonArray joints)
    {
        int inside = 0;
        for (int j = 1; j < joints.Count; j++)
        {
            int node = j < chain.Count ? chain[j] : tailNode;
            var p = node < _nb ? _bonePos[node] : res.Tails[node - _nb].Position;
            float hr = joints[j]!["hitRadius"]!.GetValue<double>() is var d ? (float)d : 0;
            foreach (var c in colliders)
            {
                var (kind, a, b, r) = Shape(c);
                float dist = kind == 0 ? Vector3.Distance(p, a) : DistanceToSegment(p, a, b);
                if (dist < r + hr * 0.5f) { inside++; break; }
            }
        }
        if (inside > 0)
            _r.Warnings.Add($"Spring '{_nodeNames[chain[0]]}': {inside} joints start inside a collider they collide with (may push out at rest).");
    }

    static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        float t = ab.LengthSquared() > 0 ? Math.Clamp(Vector3.Dot(p - a, ab) / ab.LengthSquared(), 0, 1) : 0;
        return Vector3.Distance(p, a + ab * t);
    }
}
