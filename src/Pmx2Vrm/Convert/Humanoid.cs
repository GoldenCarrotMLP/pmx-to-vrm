using Pmx2Vrm.Pmx;

namespace Pmx2Vrm.Convert;

/// <summary>Maps MMD's standard bone names (Japanese, plus common English names) to VRM 1.0 human bones.</summary>
public static class Humanoid
{
    public static readonly string[] Required =
    {
        "hips", "spine", "head",
        "leftUpperArm", "leftLowerArm", "leftHand", "rightUpperArm", "rightLowerArm", "rightHand",
        "leftUpperLeg", "leftLowerLeg", "leftFoot", "rightUpperLeg", "rightLowerLeg", "rightFoot",
    };

    static readonly (string Bone, string[] Jp, string[] En)[] Center =
    {
        ("spine", new[] { "上半身" }, new[] { "upperbody", "spine" }),
        ("chest", new[] { "上半身2" }, new[] { "upperbody2", "chest" }),
        ("upperChest", new[] { "上半身3" }, new[] { "upperbody3", "upperchest" }),
        ("neck", new[] { "首" }, new[] { "neck" }),
        ("head", new[] { "頭" }, new[] { "head" }),
    };

    // Side-specific: Japanese names get 左/右 in front; English names get l/r or left/right around them.
    // D-bones (足D, ひざD, 足首D) come first: when present they are the ones that carry the weights.
    static readonly (string Bone, string[] Jp, string[] En)[] Sided =
    {
        ("Eye", new[] { "目" }, new[] { "eye" }),
        ("Shoulder", new[] { "肩" }, new[] { "shoulder" }),
        ("UpperArm", new[] { "腕" }, new[] { "arm", "upperarm" }),
        ("LowerArm", new[] { "ひじ", "肘" }, new[] { "elbow", "forearm", "lowerarm" }),
        ("Hand", new[] { "手首" }, new[] { "wrist", "hand" }),
        ("UpperLeg", new[] { "足d", "足" }, new[] { "legd", "leg", "upperleg", "thigh" }),
        ("LowerLeg", new[] { "ひざd", "膝d", "ひざ", "膝" }, new[] { "kneed", "knee", "lowerleg", "calf" }),
        ("Foot", new[] { "足首d", "足首" }, new[] { "ankled", "ankle", "foot" }),
        ("Toes", new[] { "足先ex", "つま先" }, new[] { "toeex", "toe", "toes" }),
        ("ThumbMetacarpal", new[] { "親指0" }, new[] { "thumb0" }),
        ("ThumbProximal", new[] { "親指1" }, new[] { "thumb1" }),
        ("ThumbDistal", new[] { "親指2" }, new[] { "thumb2" }),
        ("IndexProximal", new[] { "人指1", "人差指1" }, new[] { "index1", "indexfinger1", "fore1" }),
        ("IndexIntermediate", new[] { "人指2", "人差指2" }, new[] { "index2", "indexfinger2", "fore2" }),
        ("IndexDistal", new[] { "人指3", "人差指3" }, new[] { "index3", "indexfinger3", "fore3" }),
        ("MiddleProximal", new[] { "中指1" }, new[] { "middle1", "middlefinger1" }),
        ("MiddleIntermediate", new[] { "中指2" }, new[] { "middle2", "middlefinger2" }),
        ("MiddleDistal", new[] { "中指3" }, new[] { "middle3", "middlefinger3" }),
        ("RingProximal", new[] { "薬指1" }, new[] { "ring1", "ringfinger1", "third1" }),
        ("RingIntermediate", new[] { "薬指2" }, new[] { "ring2", "ringfinger2", "third2" }),
        ("RingDistal", new[] { "薬指3" }, new[] { "ring3", "ringfinger3", "third3" }),
        ("LittleProximal", new[] { "小指1" }, new[] { "little1", "littlefinger1", "pinky1" }),
        ("LittleIntermediate", new[] { "小指2" }, new[] { "little2", "littlefinger2", "pinky2" }),
        ("LittleDistal", new[] { "小指3" }, new[] { "little3", "littlefinger3", "pinky3" }),
    };

    /// <summary>For each human bone, the human bones that may be its nearest humanoid parent, in order of preference.</summary>
    static readonly Dictionary<string, string[]> ParentOf = BuildParents();

    static Dictionary<string, string[]> BuildParents()
    {
        var p = new Dictionary<string, string[]>
        {
            ["spine"] = new[] { "hips" },
            ["chest"] = new[] { "spine" },
            ["upperChest"] = new[] { "chest" },
            ["neck"] = new[] { "upperChest", "chest", "spine" },
            ["head"] = new[] { "neck", "upperChest", "chest", "spine" },
        };
        foreach (var s in new[] { "left", "right" })
        {
            p[s + "Eye"] = new[] { "head" };
            p[s + "Shoulder"] = new[] { "upperChest", "chest", "spine" };
            p[s + "UpperArm"] = new[] { s + "Shoulder", "upperChest", "chest", "spine" };
            p[s + "LowerArm"] = new[] { s + "UpperArm" };
            p[s + "Hand"] = new[] { s + "LowerArm" };
            p[s + "UpperLeg"] = new[] { "hips" };
            p[s + "LowerLeg"] = new[] { s + "UpperLeg" };
            p[s + "Foot"] = new[] { s + "LowerLeg" };
            p[s + "Toes"] = new[] { s + "Foot" };
            p[s + "ThumbMetacarpal"] = new[] { s + "Hand" };
            p[s + "ThumbProximal"] = new[] { s + "ThumbMetacarpal" };
            p[s + "ThumbDistal"] = new[] { s + "ThumbProximal" };
            foreach (var f in new[] { "Index", "Middle", "Ring", "Little" })
            {
                p[s + f + "Proximal"] = new[] { s + "Hand" };
                p[s + f + "Intermediate"] = new[] { s + f + "Proximal" };
                p[s + f + "Distal"] = new[] { s + f + "Intermediate" };
            }
        }
        return p;
    }

    /// <summary>The order in which bones are checked (parents before children).</summary>
    public static IEnumerable<string> Order()
    {
        yield return "hips";
        foreach (var c in Center) yield return c.Bone;
        foreach (var s in new[] { "left", "right" })
            foreach (var b in Sided) yield return s + b.Bone;
    }

    /// <param name="excluded">Bones that may not be human bones (simulated physics bones).</param>
    public static Dictionary<string, int> Map(PmxModel m, int[] parent, bool[] excluded, List<string> warnings)
    {
        var jp = new Dictionary<string, int>();
        var en = new Dictionary<string, int>();
        for (int i = 0; i < m.Bones.Count; i++)
        {
            if (excluded[i]) continue;
            jp.TryAdd(Names.Norm(m.Bones[i].Name), i);
            var e = Names.Norm(m.Bones[i].NameEn);
            if (e.Length > 0) en.TryAdd(e, i);
            // Some models only fill the local name with an English name.
            en.TryAdd(Names.Norm(m.Bones[i].Name), i);
        }

        var map = new Dictionary<string, int>();
        var used = new HashSet<int>();
        int Find(IEnumerable<string> jpNames, IEnumerable<string> enNames)
        {
            foreach (var n in jpNames) if (jp.TryGetValue(n, out var i) && !used.Contains(i)) return i;
            foreach (var n in enNames) if (en.TryGetValue(n, out var i) && !used.Contains(i)) return i;
            return -1;
        }
        void Put(string bone, int i) { if (i >= 0) { map[bone] = i; used.Add(i); } }

        foreach (var c in Center) Put(c.Bone, Find(c.Jp.Select(Names.Norm), c.En));
        foreach (var (side, jpSide, l, longName) in new[] { ("left", "左", "l", "left"), ("right", "右", "r", "right") })
            foreach (var b in Sided)
            {
                var enNames = b.En.SelectMany(e => new[] { e + l, longName + e, l + e, e + longName });
                Put(side + b.Bone, Find(b.Jp.Select(j => Names.Norm(jpSide + j)), enNames));
            }

        // Older models have no 親指０: their 親指１/２ are the first two thumb bones.
        foreach (var s in new[] { "left", "right" })
        {
            if (!map.ContainsKey(s + "ThumbMetacarpal") && map.TryGetValue(s + "ThumbProximal", out var p1))
            {
                map[s + "ThumbMetacarpal"] = p1;
                map.Remove(s + "ThumbProximal");
                if (map.TryGetValue(s + "ThumbDistal", out var p2)) { map[s + "ThumbProximal"] = p2; map.Remove(s + "ThumbDistal"); }
            }
        }

        // Hips: the deepest common ancestor of the spine and both upper legs (センター, グルーブ or 腰).
        if (map.TryGetValue("spine", out var spine) && map.TryGetValue("leftUpperLeg", out var ll) && map.TryGetValue("rightUpperLeg", out var rl))
        {
            int hips = CommonAncestor(parent, new[] { spine, ll, rl });
            if (hips >= 0 && !used.Contains(hips) && !excluded[hips]) Put("hips", hips);
        }
        if (!map.ContainsKey("hips"))
        {
            var cand = Find(new[] { Names.Norm("下半身"), Names.Norm("腰"), Names.Norm("センター") }, new[] { "hips", "lowerbody", "center", "waist" });
            Put("hips", cand);
        }

        // Every human bone must sit below its humanoid parent; drop the ones that don't.
        foreach (var bone in Order())
        {
            if (!map.TryGetValue(bone, out var idx) || bone == "hips") continue;
            if (!ParentOf.TryGetValue(bone, out var parents)) continue;
            string? expected = parents.FirstOrDefault(map.ContainsKey);
            if (expected == null || !IsDescendant(parent, idx, map[expected]))
            {
                warnings.Add($"Bone '{m.Bones[idx].Name}' looks like {bone} but is not below {expected ?? string.Join("/", parents)}; left out of the humanoid.");
                map.Remove(bone);
            }
        }
        foreach (var r in Required)
            if (!map.ContainsKey(r)) warnings.Add($"Required human bone '{r}' not found: the VRM will not load as a humanoid.");
        return map;
    }

    /// <summary>
    /// Bones that stand for a mapped human bone without being it: the plain 足 / ひざ / 足首 / つま先 chain when the
    /// D-bones were mapped (MMD drives it by IK; in VRM it never moves), and other name variants. Rigid bodies on
    /// them belong on the human bone.
    /// </summary>
    public static Dictionary<int, string> Aliases(PmxModel m, Dictionary<string, int> map)
    {
        var byName = new Dictionary<string, int>();
        for (int i = 0; i < m.Bones.Count; i++) byName.TryAdd(Names.Norm(m.Bones[i].Name), i);
        var mapped = new HashSet<int>(map.Values);
        var result = new Dictionary<int, string>();
        foreach (var (side, jpSide) in new[] { ("left", "左"), ("right", "右") })
            foreach (var b in Sided)
            {
                if (!map.ContainsKey(side + b.Bone)) continue;
                foreach (var jp in b.Jp)
                    if (byName.TryGetValue(Names.Norm(jpSide + jp), out var i) && !mapped.Contains(i))
                        result.TryAdd(i, side + b.Bone);
            }
        return result;
    }

    public static bool IsDescendant(int[] parent, int bone, int ancestor)
    {
        int guard = 0;
        for (int p = parent[bone]; p >= 0 && guard++ < 100000; p = parent[p])
            if (p == ancestor) return true;
        return false;
    }

    static int CommonAncestor(int[] parent, int[] bones)
    {
        HashSet<int>? common = null;
        foreach (var b in bones)
        {
            var set = new HashSet<int>();
            for (int p = parent[b]; p >= 0 && set.Add(p); p = parent[p]) { }
            if (common == null) common = set; else common.IntersectWith(set);
        }
        if (common == null || common.Count == 0) return -1;
        // The deepest one: walk up from the first bone and return the first that is common.
        for (int p = parent[bones[0]]; p >= 0; p = parent[p]) if (common.Contains(p)) return p;
        return -1;
    }

    /// <summary>Collider / spring region of a human bone.</summary>
    public static string Region(string humanBone)
    {
        if (humanBone is "head" or "neck" or "leftEye" or "rightEye" or "jaw") return "Head";
        if (humanBone.StartsWith("left") && (humanBone.Contains("Leg") || humanBone.Contains("Foot") || humanBone.Contains("Toes"))) return "LeftLeg";
        if (humanBone.StartsWith("right") && (humanBone.Contains("Leg") || humanBone.Contains("Foot") || humanBone.Contains("Toes"))) return "RightLeg";
        if (humanBone.StartsWith("left") && !humanBone.Contains("Shoulder")) return "LeftArm";
        if (humanBone.StartsWith("right") && !humanBone.Contains("Shoulder")) return "RightArm";
        return "Body";
    }
}
