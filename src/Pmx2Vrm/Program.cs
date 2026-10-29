namespace Pmx2Vrm;

public static class Program
{
    public static int Main(string[] args)
    {
        var m = Pmx.PmxReader.Read(args[0]);
        Console.WriteLine($"{m.Name}: {m.Vertices.Count} vertices, {m.Bones.Count} bones, {m.Morphs.Count} morphs, {m.RigidBodies.Count} rigid bodies, {m.Joints.Count} joints");
        return 0;
    }
}
