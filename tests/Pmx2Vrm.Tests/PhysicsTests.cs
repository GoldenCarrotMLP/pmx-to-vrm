using System.Numerics;
using System.Text.Json.Nodes;
using Pmx2Vrm.Convert;
using Pmx2Vrm.Pmx;

namespace Pmx2Vrm.Tests;

public static class PhysicsTests
{
    /// <summary>Adds a skirt: an anchor body on スカート親, then a simulated chain that branches at its third bone.</summary>
    static PmxModel WithSkirt()
    {
        var m = SyntheticModel.Build();
        int B(string n, Vector3 p, int parent) { m.Bones.Add(new PmxBone { Name = n, Position = p, Parent = parent }); return m.Bones.Count - 1; }
        int root = B("スカート親", new Vector3(0, 10.5f, 0), 18);
        int s1 = B("スカート1", new Vector3(0, 10, -1.2f), root);
        int s2 = B("スカート2", new Vector3(0, 8.5f, -1.5f), s1);
        int s3a = B("スカート3a", new Vector3(0.3f, 7, -1.8f), s2);
        int s3b = B("スカート3b", new Vector3(-0.3f, 7, -1.8f), s2);
        B("スカート4a", new Vector3(0.4f, 6, -1.9f), s3a);
        ushort skirtMask = unchecked((ushort)~(1 << 3));
        void Body(string n, int bone, PmxPhysicsMode mode, Vector3 at) => m.RigidBodies.Add(new PmxRigidBody
        {
            Name = n, Bone = bone, Group = 3, CollisionMask = skirtMask, Shape = PmxRigidShape.Box, Size = new Vector3(0.8f, 0.7f, 0.1f),
            Position = at, Mass = 1.5f, LinearDamping = 0.9f, AngularDamping = 0.99f, Mode = mode,
        });
        Body("スカート親", root, PmxPhysicsMode.FollowBone, new Vector3(0, 10.5f, 0));
        Body("スカート1", s1, PmxPhysicsMode.Physics, new Vector3(0, 9.3f, -1.3f));
        Body("スカート2", s2, PmxPhysicsMode.Physics, new Vector3(0, 7.8f, -1.6f));
        Body("スカート3a", s3a, PmxPhysicsMode.Physics, new Vector3(0.3f, 6.5f, -1.8f));
        Body("スカート3b", s3b, PmxPhysicsMode.Physics, new Vector3(-0.3f, 6.5f, -1.8f));
        int first = m.RigidBodies.Count - 5;
        m.Joints.Add(new PmxJoint { Name = "j1", BodyA = first, BodyB = first + 1, RotationMin = new Vector3(-0.1f), RotationMax = new Vector3(0.1f), RotationSpring = new Vector3(100) });
        return m;
    }

    public static void TestBranchingSkirtCollidesWithLegs()
    {
        var m = WithSkirt();
        var dir = Path.Combine(Path.GetTempPath(), "pmx2vrm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var (bytes, rep) = Converter.Convert(m, dir, new ConvertOptions());
        var g = Glb.Parse(bytes);
        g.CheckStructure();
        var sb = g.Json["extensions"]!["VRMC_springBone"]!;
        var springs = (JsonArray)sb["springs"]!;
        Assert.Equal(3, springs.Count, "hair + skirt main branch + skirt side branch");
        var names = g.Arr("nodes").Select(n => n!["name"]!.GetValue<string>()).ToList();
        string[] Joints(int s) => ((JsonArray)springs[s]!["joints"]!).Select(j => names[j!["node"]!.GetValue<int>()]).ToArray();
        var all = Enumerable.Range(0, springs.Count).Select(Joints).ToList();
        Assert.True(all.Any(j => j.SequenceEqual(new[] { "スカート1", "スカート2", "スカート3a", "スカート4a" })), "main skirt chain follows the deeper branch to its tail bone: " + string.Join(" | ", all.Select(a => string.Join(",", a))));
        Assert.True(all.Any(j => j.Length == 2 && j[0] == "スカート3b" && j[1] == "スカート3b_end"), "side branch gets its own spring with a synthetic tail");
        var allNodes = all.SelectMany(a => a).ToList();
        Assert.Equal(allNodes.Count, allNodes.Distinct().Count(), "no joint in two springs");
        var groups = ((JsonArray)sb["colliderGroups"]!).Select(x => x!["name"]!.GetValue<string>()).ToList();
        int skirt = all.FindIndex(j => j[0] == "スカート1");
        var used = ((JsonArray)springs[skirt]!["colliderGroups"]!).Select(x => groups[x!.GetValue<int>()]).ToList();
        Assert.True(used.Contains("LeftLeg") && used.Contains("RightLeg"), "skirt collides with the legs: " + string.Join(",", used));
        Assert.Equal(4, ((JsonArray)sb["colliders"]!).Count, "the skirt anchor body is not a collider");
        var j0 = springs[skirt]!["joints"]![0]!;
        Assert.True(j0["stiffness"]!.GetValue<float>() > 2, "tight limits + strong spring = stiff");
        Assert.True(j0["dragForce"]!.GetValue<float>() > 0.7f, "high damping = high drag");
    }
}
