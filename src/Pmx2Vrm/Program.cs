using System.Globalization;
using Pmx2Vrm.Convert;

namespace Pmx2Vrm;

public static class Program
{
    const string Usage = """
        pmx2vrm - MikuMikuDance (.pmx) to VRM 1.0

        usage:
          pmx2vrm <in.pmx> [out.vrm] [options]
          pmx2vrm <folder> [out-folder] [options]     converts every .pmx below the folder

        options:
          --name <text>                 avatar name (default: the PMX model name)
          --author <text>               author (default: guessed from the PMX comment, else "unknown")
          --license-url <url>           default https://vrm.dev/licenses/1.0/
          --avatar-permission <p>       onlyAuthor (default) | onlySeparatelyLicensedPerson | everyone
          --commercial <c>              personalNonProfit (default) | personalProfit | corporation
          --modification <m>            prohibited (default) | allowModification | allowModificationRedistribution
          --allow-redistribution        set allowRedistribution = true (default false)
          --scale <metres per unit>     default 0.08 (MMD's 8 cm per unit)
          --no-tpose                    keep the arms as they are (MMD's A-pose) instead of a T-pose
          --no-physics                  no spring bones or colliders
          --all-morphs                  export every vertex/group morph as a morph target
          --quiet                       only print errors

        The original model's terms of use still apply to the converted file: the PMX comment and any readme
        in the model's folder are written next to the output as <name>.license.txt.
        """;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var o = new ConvertOptions();
        var positional = new List<string>();
        bool quiet = false;
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
                switch (a)
                {
                    case "-h": case "--help": case "/?": Console.WriteLine(Usage); return 0;
                    case "--name": o.Name = Next(); break;
                    case "--author": o.Author = Next(); break;
                    case "--license-url": o.LicenseUrl = Next(); break;
                    case "--avatar-permission": o.AvatarPermission = Pick(Next(), "onlyAuthor", "onlySeparatelyLicensedPerson", "everyone"); break;
                    case "--commercial": o.CommercialUsage = Pick(Next(), "personalNonProfit", "personalProfit", "corporation"); break;
                    case "--modification": o.Modification = Pick(Next(), "prohibited", "allowModification", "allowModificationRedistribution"); break;
                    case "--allow-redistribution": o.AllowRedistribution = true; break;
                    case "--scale":
                        o.Scale = float.Parse(Next(), CultureInfo.InvariantCulture);
                        if (!(o.Scale > 0) || o.Scale > 100) throw new ArgumentException("--scale must be > 0");
                        break;
                    case "--no-tpose": o.TPose = false; break;
                    case "--no-physics": o.Physics = false; break;
                    case "--all-morphs": o.AllMorphs = true; break;
                    case "--quiet": case "-q": quiet = true; break;
                    default:
                        if (a.StartsWith("--")) throw new ArgumentException($"unknown option {a}");
                        positional.Add(a);
                        break;
                }
            }
            if (positional.Count is < 1 or > 2) throw new ArgumentException("give an input .pmx file or folder (and optionally an output)");
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            Console.Error.WriteLine(Usage);
            return 2;
        }

        string input = positional[0];
        string? output = positional.Count > 1 ? positional[1] : null;
        if (Directory.Exists(input))
        {
            var files = Directory.EnumerateFiles(input, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".pmx", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f).ToList();
            if (files.Count == 0) { Console.Error.WriteLine($"no .pmx files in {input}"); return 1; }
            int failed = 0;
            foreach (var f in files)
            {
                string outPath;
                if (output != null)
                {
                    var rel = Path.GetRelativePath(input, f);
                    outPath = Path.Combine(output, Path.ChangeExtension(rel, ".vrm"));
                }
                else outPath = Path.ChangeExtension(f, ".vrm");
                // A name given on the command line would make every avatar in the folder the same; only use it for single files.
                var fo = o;
                if (o.Name != null) { fo = Clone(o); fo.Name = null; }
                if (!ConvertOne(f, outPath, fo, quiet)) failed++;
            }
            if (!quiet) Console.WriteLine($"{files.Count - failed}/{files.Count} converted");
            return failed == 0 ? 0 : 1;
        }
        if (!File.Exists(input)) { Console.Error.WriteLine($"not found: {input}"); return 1; }
        return ConvertOne(input, output ?? Path.ChangeExtension(input, ".vrm"), o, quiet) ? 0 : 1;
    }

    static ConvertOptions Clone(ConvertOptions o) => new()
    {
        Name = o.Name, Author = o.Author, LicenseUrl = o.LicenseUrl, AvatarPermission = o.AvatarPermission,
        CommercialUsage = o.CommercialUsage, Modification = o.Modification, AllowRedistribution = o.AllowRedistribution,
        Scale = o.Scale, TPose = o.TPose, AllMorphs = o.AllMorphs, Physics = o.Physics,
    };

    static string Pick(string v, params string[] allowed) =>
        allowed.FirstOrDefault(x => x.Equals(v, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"'{v}' is not one of {string.Join(", ", allowed)}");

    public static bool ConvertOne(string input, string output, ConvertOptions o, bool quiet)
    {
        try
        {
            var (glb, report) = Converter.Convert(input, o);
            var dir = Path.GetDirectoryName(Path.GetFullPath(output));
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllBytes(output, glb);
            var licensePath = Path.Combine(dir ?? ".", Path.GetFileNameWithoutExtension(output) + ".license.txt");
            File.WriteAllText(licensePath, Converter.LicenseText(Pmx.PmxReader.Read(input), input), new System.Text.UTF8Encoding(true));
            report.Output = output;
            if (!quiet) Console.WriteLine(report);
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException)
        {
            Console.Error.WriteLine($"{input}: FAILED: {e.Message}");
            return false;
        }
    }
}
