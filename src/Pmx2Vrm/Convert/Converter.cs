using System.Numerics;
using System.Text.Json.Nodes;
using Pmx2Vrm.Gltf;
using Pmx2Vrm.Pmx;

namespace Pmx2Vrm.Convert;

/// <summary>
/// PMX to VRM 1.0. Everything is first moved into glTF space (metres, right-handed, Y up, facing +Z,
/// feet on y = 0), the arms are straightened into a T-pose, and then the mesh, skin, materials,
/// humanoid, expressions and spring bones are written.
/// </summary>
public sealed partial class Converter
{
    readonly PmxModel _m;
    readonly ConvertOptions _o;
    readonly ConvertReport _r;
    readonly string _dir;
    readonly GltfBuilder _g = new();

    int _nb;
    Vector3[] _bonePos = null!;
    int[] _parent = null!;
    List<int>[] _children = null!;
    int[] _order = null!;
    string[] _nodeNames = null!;

    Vector3[] _pos = null!, _nrm = null!;
    Vector2[] _uv = null!;
    ushort[] _joints = null!;
    float[] _weights = null!;

    Dictionary<string, int> _human = new();
    bool[] _simulated = null!;
    Quaternion[] _gq = null!;
    Vector3[] _gt = null!;
    Body[] _bodies = null!;

    Converter(PmxModel m, string dir, ConvertOptions o)
    {
        _m = m; _dir = dir; _o = o;
        _r = new ConvertReport();
    }

    public static (byte[] Glb, ConvertReport Report) Convert(string pmxPath, ConvertOptions o)
    {
        var m = PmxReader.Read(pmxPath);
        var (glb, rep) = Convert(m, Path.GetDirectoryName(Path.GetFullPath(pmxPath)) ?? ".", o);
        rep.Source = pmxPath;
        return (glb, rep);
    }

    public static (byte[] Glb, ConvertReport Report) Convert(PmxModel m, string baseDir, ConvertOptions o)
    {
        var c = new Converter(m, baseDir, o);
        var glb = c.Run();
        return (glb, c._r);
    }

    Vector3 P(Vector3 v) => new(v.X * _o.Scale, v.Y * _o.Scale, -v.Z * _o.Scale);
    static Vector3 D(Vector3 v) => new(v.X, v.Y, -v.Z);

    byte[] Run()
    {
        if (_m.SoftBodyCount > 0) _r.Warnings.Add($"{_m.SoftBodyCount} PMX 2.1 soft bodies ignored.");
        _r.ModelName = _o.Name ?? (_m.Name.Length > 0 ? _m.Name : _m.NameEn);
        SetupSkeleton();
        SetupGeometry();
        SetupBodies();
        _simulated = new bool[_nb];
        if (_o.Physics)
            foreach (var b in _bodies)
                if (b.Mode != PmxPhysicsMode.FollowBone && b.Bone >= 0) _simulated[b.Bone] = true;
        _human = Humanoid.Map(_m, _parent, _simulated, _r.Warnings);
        CheckFacing();
        Bake();
        PlaceHips();
        Ground();

        var exprs = AnalyzeExpressions();
        var mats = BuildMaterials(exprs);
        var physics = _o.Physics ? BuildPhysics() : null;
        int meshNode = _nb + (physics?.Tails.Count ?? 0);
        var targets = BuildTargets(exprs);
        BuildMesh(mats, targets);
        BuildNodes(physics, meshNode);

        var root = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "pmx2vrm 0.1" },
            ["scene"] = 0,
        };
        var sceneNodes = new JsonArray();
        for (int i = 0; i < _nb; i++) if (_parent[i] < 0) sceneNodes.Add(i);
        sceneNodes.Add(meshNode);
        root["scenes"] = new JsonArray(new JsonObject { ["nodes"] = sceneNodes });

        var used = new JsonArray("VRMC_vrm", "VRMC_materials_mtoon");
        var ext = new JsonObject { ["VRMC_vrm"] = BuildVrm(exprs, targets, mats, meshNode) };
        if (physics != null && (physics.Json["springs"] as JsonArray)?.Count + (physics.Json["colliders"] as JsonArray)?.Count > 0)
        {
            ext["VRMC_springBone"] = physics.Json;
            used.Add("VRMC_springBone");
        }
        root["extensionsUsed"] = used;
        root["extensions"] = ext;

        _r.Vertices = _pos.Length;
        _r.Faces = _m.Indices.Count / 3;
        _r.Materials = _m.Materials.Count;
        _r.Bones = _nb;
        _r.HumanBones.AddRange(Humanoid.Order().Where(_human.ContainsKey));
        _r.MissingRequired.AddRange(Humanoid.Required.Where(b => !_human.ContainsKey(b)));
        return _g.ToGlb(root);
    }

    // ---------------------------------------------------------------- skeleton

    void SetupSkeleton()
    {
        _nb = _m.Bones.Count;
        if (_nb == 0)
        {
            // A model without bones still gets one, so the skin and humanoid code have something to hold.
            _m.Bones.Add(new PmxBone { Name = "root" });
            _nb = 1;
            _r.Warnings.Add("Model has no bones; added a root bone.");
        }
        _parent = new int[_nb];
        _bonePos = new Vector3[_nb];
        for (int i = 0; i < _nb; i++)
        {
            int p = _m.Bones[i].Parent;
            _parent[i] = p >= 0 && p < _nb && p != i ? p : -1;
            _bonePos[i] = P(_m.Bones[i].Position);
        }
        // Break parent cycles.
        for (int i = 0; i < _nb; i++)
        {
            var seen = new HashSet<int> { i };
            for (int p = _parent[i]; p >= 0; p = _parent[p])
            {
                if (!seen.Add(p))
                {
                    _r.Warnings.Add($"Bone parent loop at '{_m.Bones[i].Name}'; cut.");
                    _parent[i] = -1;
                    break;
                }
            }
        }
        _children = new List<int>[_nb];
        for (int i = 0; i < _nb; i++) _children[i] = new List<int>();
        for (int i = 0; i < _nb; i++) if (_parent[i] >= 0) _children[_parent[i]].Add(i);
        var order = new List<int>(_nb);
        var queue = new Queue<int>();
        for (int i = 0; i < _nb; i++) if (_parent[i] < 0) queue.Enqueue(i);
        while (queue.Count > 0)
        {
            int b = queue.Dequeue();
            order.Add(b);
            foreach (var c in _children[b]) queue.Enqueue(c);
        }
        _order = order.ToArray();

        _nodeNames = new string[_nb];
        var names = new HashSet<string>();
        for (int i = 0; i < _nb; i++)
        {
            var n = _m.Bones[i].Name.Length > 0 ? _m.Bones[i].Name : (_m.Bones[i].NameEn.Length > 0 ? _m.Bones[i].NameEn : $"bone{i}");
            var u = n;
            for (int k = 2; !names.Add(u); k++) u = $"{n}_{k}";
            _nodeNames[i] = u;
        }
    }

    void SetupGeometry()
    {
        int n = _m.Vertices.Count;
        _pos = new Vector3[n]; _nrm = new Vector3[n]; _uv = new Vector2[n];
        _joints = new ushort[n * 4]; _weights = new float[n * 4];
        int unweighted = 0;
        var tmpB = new int[4]; var tmpW = new float[4];
        for (int i = 0; i < n; i++)
        {
            var v = _m.Vertices[i];
            _pos[i] = P(v.Position);
            var nn = D(v.Normal);
            _nrm[i] = nn.LengthSquared() > 1e-12f ? Vector3.Normalize(nn) : Vector3.UnitY;
            _uv[i] = new Vector2(float.IsFinite(v.Uv.X) ? v.Uv.X : 0, float.IsFinite(v.Uv.Y) ? v.Uv.Y : 0);
            int k = 0;
            for (int s = 0; s < 4; s++)
            {
                int b = v.Bones[s]; float w = v.Weights[s];
                if (b < 0 || b >= _nb || !(w > 0)) continue;
                int at = Array.IndexOf(tmpB, b, 0, k);
                if (at >= 0) { tmpW[at] += w; continue; }
                tmpB[k] = b; tmpW[k] = w; k++;
            }
            float sum = 0;
            for (int s = 0; s < k; s++) sum += tmpW[s];
            if (k == 0 || sum <= 0) { unweighted++; tmpB[0] = 0; tmpW[0] = 1; k = 1; sum = 1; }
            for (int s = 0; s < 4; s++)
            {
                _joints[i * 4 + s] = s < k ? (ushort)tmpB[s] : (ushort)0;
                _weights[i * 4 + s] = s < k ? tmpW[s] / sum : 0;
            }
        }
        if (unweighted > 0) _r.Warnings.Add($"{unweighted} vertices had no bone weights; bound to the first bone.");
        int sdef = _m.Vertices.Count(v => v.WeightType == PmxWeightType.Sdef);
        if (sdef > 0) _r.Warnings.Add($"{sdef} SDEF vertices converted as BDEF2 (linear skinning).");
    }

    /// <summary>The left arm should be on +X (model facing -Z in MMD, +Z after conversion).</summary>
    void CheckFacing()
    {
        if (_human.TryGetValue("leftUpperArm", out var l) && _human.TryGetValue("rightUpperArm", out var r) && _bonePos[l].X < _bonePos[r].X)
            _r.Warnings.Add("The left arm is on the -X side: this model may face +Z in MMD (unusual); it will face backwards.");
    }

    // ---------------------------------------------------------------- rest pose

    Vector3 G(int bone, Vector3 p) => bone < 0 ? p : Vector3.Transform(p, _gq[bone]) + _gt[bone];

    /// <summary>
    /// Straightens the arms into a T-pose: each arm segment rotates about its own joint so it points
    /// along +X (left) / -X (right); every descendant bone, vertex, morph and rigid body follows.
    /// </summary>
    void Bake()
    {
        _gq = new Quaternion[_nb];
        _gt = new Vector3[_nb];
        for (int i = 0; i < _nb; i++) _gq[i] = Quaternion.Identity;
        if (!_o.TPose) return;

        var straighten = new Dictionary<int, (int Child, Vector3 Target)>();
        foreach (var (side, sign) in new[] { ("left", 1f), ("right", -1f) })
        {
            var target = new Vector3(sign, 0, 0);
            void Seg(string a, params string[] bs)
            {
                if (!_human.TryGetValue(side + a, out var ia)) return;
                foreach (var b in bs)
                    if (_human.TryGetValue(side + b, out var ib)) { straighten[ia] = (ib, target); return; }
            }
            Seg("UpperArm", "LowerArm");
            Seg("LowerArm", "Hand");
            Seg("Hand", "MiddleProximal", "IndexProximal", "RingProximal");
        }
        if (straighten.Count == 0) return;

        var newPos = new Vector3[_nb];
        int rotated = 0;
        float maxAngle = 0;
        foreach (var b in _order)
        {
            int p = _parent[b];
            var qp = p >= 0 ? _gq[p] : Quaternion.Identity;
            var tp = p >= 0 ? _gt[p] : Vector3.Zero;
            var pivot = Vector3.Transform(_bonePos[b], qp) + tp;
            newPos[b] = pivot;
            if (straighten.TryGetValue(b, out var s))
            {
                var childPos = Vector3.Transform(_bonePos[s.Child], qp) + tp;
                var dir = childPos - pivot;
                if (dir.LengthSquared() > 1e-10f)
                {
                    var R = FromTo(Vector3.Normalize(dir), s.Target);
                    float angle = 2 * MathF.Acos(Math.Clamp(MathF.Abs(R.W), 0, 1)) * 180 / MathF.PI;
                    if (angle > 0.3f)
                    {
                        _gq[b] = Quaternion.Normalize(Quaternion.Concatenate(qp, R));
                        _gt[b] = Vector3.Transform(tp - pivot, R) + pivot;
                        rotated++;
                        maxAngle = MathF.Max(maxAngle, angle);
                        continue;
                    }
                }
            }
            _gq[b] = qp; _gt[b] = tp;
        }
        if (rotated == 0) return;
        _bonePos = newPos;
        for (int i = 0; i < _pos.Length; i++)
        {
            Vector3 p = Vector3.Zero, n = Vector3.Zero;
            for (int s = 0; s < 4; s++)
            {
                float w = _weights[i * 4 + s];
                if (w <= 0) continue;
                int b = _joints[i * 4 + s];
                p += w * (Vector3.Transform(_pos[i], _gq[b]) + _gt[b]);
                n += w * Vector3.Transform(_nrm[i], _gq[b]);
            }
            _pos[i] = p;
            _nrm[i] = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY;
        }
        foreach (var body in _bodies)
        {
            if (body.Bone < 0) continue;
            body.Center = Vector3.Transform(body.Center, _gq[body.Bone]) + _gt[body.Bone];
            body.AxisX = Vector3.Transform(body.AxisX, _gq[body.Bone]);
            body.AxisY = Vector3.Transform(body.AxisY, _gq[body.Bone]);
            body.AxisZ = Vector3.Transform(body.AxisZ, _gq[body.Bone]);
        }
        _r.Warnings.Add($"Rest pose: {rotated} arm segments straightened into a T-pose (largest turn {maxAngle:0} degrees).");
    }

    /// <summary>Rotates a morph offset the way the rest-pose bake rotated vertex <paramref name="v"/>.</summary>
    Vector3 BakeDelta(int v, Vector3 d)
    {
        Vector3 o = Vector3.Zero;
        for (int s = 0; s < 4; s++)
        {
            float w = _weights[v * 4 + s];
            if (w > 0) o += w * Vector3.Transform(d, _gq[_joints[v * 4 + s]]);
        }
        return o;
    }

    static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        float d = Vector3.Dot(a, b);
        if (d > 0.999999f) return Quaternion.Identity;
        if (d < -0.999999f)
        {
            var axis = Vector3.Cross(Vector3.UnitY, a);
            if (axis.LengthSquared() < 1e-6f) axis = Vector3.Cross(Vector3.UnitZ, a);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        var c = Vector3.Cross(a, b);
        return Quaternion.Normalize(new Quaternion(c.X, c.Y, c.Z, 1 + d));
    }

    /// <summary>
    /// MMD's センター usually sits near the knees; VRM/Unity expect the hips between the legs. The node is
    /// moved (only its pivot: the skin follows because the bind matrices use the same position).
    /// </summary>
    void PlaceHips()
    {
        if (!_human.TryGetValue("hips", out var hips) || !_human.TryGetValue("spine", out var spine)
            || !_human.TryGetValue("leftUpperLeg", out var ll) || !_human.TryGetValue("rightUpperLeg", out var rl)) return;
        var legs = (_bonePos[ll] + _bonePos[rl]) / 2;
        float top = MathF.Max(_bonePos[spine].Y, legs.Y);
        var hp = _bonePos[hips];
        if (hp.Y >= legs.Y - 1e-4f && hp.Y <= top + 1e-4f) return;
        var np = new Vector3(legs.X, (legs.Y + top) / 2, legs.Z);
        _r.Warnings.Add($"Hips node '{_m.Bones[hips].Name}' moved from y={hp.Y:0.000} to y={np.Y:0.000} m (between the legs).");
        _bonePos[hips] = np;
    }

    void Ground()
    {
        if (_pos.Length == 0) return;
        float min = float.MaxValue, max = float.MinValue;
        foreach (var p in _pos) { min = MathF.Min(min, p.Y); max = MathF.Max(max, p.Y); }
        _r.HeightMetres = max - min;
        if (MathF.Abs(min) < 1e-4f) return;
        var off = new Vector3(0, -min, 0);
        for (int i = 0; i < _pos.Length; i++) _pos[i] += off;
        for (int i = 0; i < _nb; i++) _bonePos[i] += off;
        foreach (var b in _bodies) b.Center += off;
        _r.Warnings.Add($"Moved the model by {-min:0.000} m so the feet stand on y=0.");
    }

    // ---------------------------------------------------------------- mesh and nodes

    void BuildMesh(MaterialInfo[] mats, List<MorphTarget> targets)
    {
        int posAcc = _g.AddVec3(_pos, minMax: true);
        int nrmAcc = _g.AddVec3(_nrm, minMax: false);
        int uvAcc = _g.AddVec2(_uv);
        int jAcc = _g.AddJoints(_joints);
        var w4 = new Vector4[_pos.Length];
        for (int i = 0; i < w4.Length; i++) w4[i] = new Vector4(_weights[i * 4], _weights[i * 4 + 1], _weights[i * 4 + 2], _weights[i * 4 + 3]);
        int wAcc = _g.AddVec4(w4);
        var targetAcc = targets.Select(t => _g.AddVec3(t.Delta, minMax: true)).ToList();
        var targetNames = new JsonArray(targets.Select(t => (JsonNode)JsonValue.Create(t.Name)!).ToArray());

        var prims = new JsonArray();
        int offset = 0;
        int nv = _pos.Length;
        for (int mi = 0; mi < _m.Materials.Count; mi++)
        {
            int count = _m.Materials[mi].IndexCount;
            int start = offset;
            offset += Math.Max(0, count);
            if (count <= 0) continue;
            var idx = new List<uint>(count);
            int bad = 0;
            for (int f = start; f + 2 < start + count && f + 2 < _m.Indices.Count; f += 3)
            {
                int a = _m.Indices[f], b = _m.Indices[f + 1], c = _m.Indices[f + 2];
                if (a < 0 || b < 0 || c < 0 || a >= nv || b >= nv || c >= nv) { bad++; continue; }
                if (a == b || b == c || a == c) continue; // degenerate
                // MMD front faces are clockwise; mirroring Z keeps the image, so glTF (counter-clockwise) needs them reversed.
                idx.Add((uint)a); idx.Add((uint)c); idx.Add((uint)b);
            }
            if (bad > 0) _r.Warnings.Add($"Material '{_m.Materials[mi].Name}': {bad} faces with bad vertex indices dropped.");
            if (idx.Count == 0) continue;
            var attrs = new JsonObject
            {
                ["POSITION"] = posAcc, ["NORMAL"] = nrmAcc, ["TEXCOORD_0"] = uvAcc, ["JOINTS_0"] = jAcc, ["WEIGHTS_0"] = wAcc,
            };
            var prim = new JsonObject
            {
                ["attributes"] = attrs,
                ["indices"] = _g.AddIndices(idx.ToArray(), nv),
                ["material"] = mats[mi].Gltf,
                ["mode"] = 4,
            };
            if (targetAcc.Count > 0)
            {
                prim["targets"] = new JsonArray(targetAcc.Select(a => (JsonNode)new JsonObject { ["POSITION"] = a }).ToArray());
                prim["extras"] = new JsonObject { ["targetNames"] = targetNames.DeepClone() };
            }
            prims.Add(prim);
        }
        if (offset > _m.Indices.Count) _r.Warnings.Add("Material face counts add up to more faces than the model has.");
        if (prims.Count == 0) throw new InvalidDataException("The model has no drawable faces.");
        var mesh = new JsonObject { ["name"] = "Body", ["primitives"] = prims };
        if (targets.Count > 0)
        {
            mesh["weights"] = new JsonArray(targets.Select(_ => (JsonNode)JsonValue.Create(0)!).ToArray());
            mesh["extras"] = new JsonObject { ["targetNames"] = targetNames };
        }
        _g.Meshes.Add(mesh);
        _r.MorphTargets = targets.Count;
    }

    void BuildNodes(PhysicsResult? physics, int meshNode)
    {
        for (int i = 0; i < _nb; i++)
        {
            var t = _parent[i] >= 0 ? _bonePos[i] - _bonePos[_parent[i]] : _bonePos[i];
            var node = new JsonObject { ["name"] = _nodeNames[i], ["translation"] = GltfBuilder.Arr(t) };
            var kids = new JsonArray();
            foreach (var c in _children[i]) kids.Add(c);
            if (physics != null)
                for (int k = 0; k < physics.Tails.Count; k++)
                    if (physics.Tails[k].Parent == i) kids.Add(_nb + k);
            if (kids.Count > 0) node["children"] = kids;
            _g.Nodes.Add(node);
        }
        if (physics != null)
            foreach (var tail in physics.Tails)
                _g.Nodes.Add(new JsonObject { ["name"] = tail.Name, ["translation"] = GltfBuilder.Arr(tail.Position - _bonePos[tail.Parent]) });
        _g.Nodes.Add(new JsonObject { ["name"] = "Body", ["mesh"] = 0, ["skin"] = 0 });

        var ibm = new float[_nb * 16];
        for (int i = 0; i < _nb; i++)
        {
            int o = i * 16;
            ibm[o] = 1; ibm[o + 5] = 1; ibm[o + 10] = 1; ibm[o + 15] = 1;
            ibm[o + 12] = -_bonePos[i].X; ibm[o + 13] = -_bonePos[i].Y; ibm[o + 14] = -_bonePos[i].Z;
        }
        var joints = new JsonArray();
        for (int i = 0; i < _nb; i++) joints.Add(i);
        var skin = new JsonObject { ["name"] = "Armature", ["inverseBindMatrices"] = _g.AddMat4(ibm), ["joints"] = joints };
        var roots = Enumerable.Range(0, _nb).Where(i => _parent[i] < 0).ToList();
        if (roots.Count == 1) skin["skeleton"] = roots[0];
        _g.Skins.Add(skin);
    }
}
