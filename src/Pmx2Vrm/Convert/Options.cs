namespace Pmx2Vrm.Convert;

public sealed class ConvertOptions
{
    public string? Name;
    public string? Author;
    public string LicenseUrl = "https://vrm.dev/licenses/1.0/";
    /// <summary>onlyAuthor | onlySeparatelyLicensedPerson | everyone</summary>
    public string AvatarPermission = "onlyAuthor";
    /// <summary>personalNonProfit | personalProfit | corporation</summary>
    public string CommercialUsage = "personalNonProfit";
    /// <summary>prohibited | allowModification | allowModificationRedistribution</summary>
    public string Modification = "prohibited";
    public bool AllowRedistribution;
    /// <summary>Metres per MMD unit (MMD's own convention is 8 cm).</summary>
    public float Scale = 0.08f;
    /// <summary>Straighten the arms into a T-pose (VRM's recommended rest pose).</summary>
    public bool TPose = true;
    /// <summary>Export every vertex/group morph as a morph target, not only the ones the expressions use.</summary>
    public bool AllMorphs;
    /// <summary>Convert rigid bodies and joints to spring bones and colliders.</summary>
    public bool Physics = true;
}

public sealed class ConvertReport
{
    public string Source = "", Output = "", ModelName = "";
    public int Vertices, Faces, Materials, Textures, Bones;
    public float HeightMetres;
    public readonly List<string> HumanBones = new();
    public readonly List<string> MissingRequired = new();
    public readonly List<string> Expressions = new();
    public int MorphTargets;
    public readonly List<string> Springs = new();
    public int SpringJoints, Colliders, ColliderGroups;
    public readonly List<string> Warnings = new();

    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{Source} -> {Output}");
        sb.AppendLine($"  model '{ModelName}': {Vertices} vertices, {Faces} faces, {Materials} materials, {Textures} textures, {Bones} bones, height {HeightMetres:0.00} m");
        sb.AppendLine($"  humanoid: {HumanBones.Count} bones mapped" + (MissingRequired.Count > 0 ? $", MISSING required: {string.Join(", ", MissingRequired)}" : " (all required present)"));
        sb.AppendLine($"  expressions ({Expressions.Count}): {string.Join(", ", Expressions)}; {MorphTargets} morph targets");
        sb.AppendLine($"  physics: {Springs.Count} spring chains, {SpringJoints} joints, {Colliders} colliders in {ColliderGroups} groups");
        foreach (var s in Springs) sb.AppendLine($"    {s}");
        if (Warnings.Count > 0)
        {
            sb.AppendLine($"  warnings ({Warnings.Count}):");
            foreach (var w in Warnings.Take(40)) sb.AppendLine($"    - {w}");
            if (Warnings.Count > 40) sb.AppendLine($"    ... {Warnings.Count - 40} more");
        }
        return sb.ToString();
    }
}
