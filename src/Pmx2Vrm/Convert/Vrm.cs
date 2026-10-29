using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pmx2Vrm.Gltf;

namespace Pmx2Vrm.Convert;

public sealed partial class Converter
{
    JsonObject BuildVrm(ExpressionPlan plan, List<MorphTarget> targets, MaterialInfo[] mats, int meshNode)
    {
        var human = new JsonObject();
        foreach (var bone in Humanoid.Order())
            if (_human.TryGetValue(bone, out var idx)) human[bone] = new JsonObject { ["node"] = idx };

        var author = _o.Author ?? GuessAuthor(_m.Comment) ?? GuessAuthor(_m.CommentEn) ?? "unknown";
        var meta = new JsonObject
        {
            ["name"] = _r.ModelName.Length > 0 ? _r.ModelName : "model",
            ["version"] = "converted by pmx2vrm",
            ["authors"] = new JsonArray(author),
            ["licenseUrl"] = _o.LicenseUrl,
            ["avatarPermission"] = _o.AvatarPermission,
            ["allowExcessivelyViolentUsage"] = false,
            ["allowExcessivelySexualUsage"] = false,
            ["commercialUsage"] = _o.CommercialUsage,
            ["allowPoliticalOrReligiousUsage"] = false,
            ["allowAntisocialOrHateUsage"] = false,
            ["creditNotation"] = "required",
            ["allowRedistribution"] = _o.AllowRedistribution,
            ["modification"] = _o.Modification,
        };
        var vrm = new JsonObject
        {
            ["specVersion"] = "1.0",
            ["meta"] = meta,
            ["humanoid"] = new JsonObject { ["humanBones"] = human },
            ["firstPerson"] = new JsonObject
            {
                ["meshAnnotations"] = new JsonArray(new JsonObject { ["node"] = meshNode, ["type"] = "auto" }),
            },
            ["expressions"] = BuildExpressions(plan, targets, mats, meshNode),
        };

        // Look-at: bone type, from the eye bones; the offset is the point between the eyes, from the head.
        if (_human.TryGetValue("head", out var head))
        {
            var lookAt = new JsonObject { ["type"] = "bone" };
            System.Numerics.Vector3 eyes;
            bool haveL = _human.TryGetValue("leftEye", out var le), haveR = _human.TryGetValue("rightEye", out var re);
            if (haveL && haveR) eyes = (_bonePos[le] + _bonePos[re]) / 2;
            else if (haveL) eyes = _bonePos[le];
            else if (haveR) eyes = _bonePos[re];
            else eyes = _bonePos[head] + new System.Numerics.Vector3(0, 0.06f, 0.03f);
            lookAt["offsetFromHeadBone"] = GltfBuilder.Arr(eyes - _bonePos[head]);
            JsonObject Range(float max, float scale) => new() { ["inputMaxValue"] = GltfBuilder.Num(max), ["outputScale"] = GltfBuilder.Num(scale) };
            lookAt["rangeMapHorizontalInner"] = Range(90, 10);
            lookAt["rangeMapHorizontalOuter"] = Range(90, 10);
            lookAt["rangeMapVerticalDown"] = Range(90, 10);
            lookAt["rangeMapVerticalUp"] = Range(90, 10);
            vrm["lookAt"] = lookAt;
        }
        return vrm;
    }

    /// <summary>Looks for "モデル製作：name" / "Model: name" style credit lines in a PMX comment.</summary>
    public static string? GuessAuthor(string comment)
    {
        if (string.IsNullOrWhiteSpace(comment)) return null;
        var keys = new[] { "モデル製作", "モデル制作", "モデリング", "モデル作成", "制作", "製作", "作者", "改変", "model by", "modeled by", "modelling", "modeling", "model", "author", "created by", "made by" };
        foreach (var raw in comment.Split('\n'))
        {
            var line = raw.Trim().TrimStart('・', '*', '-', '■', '□', '●', '○', '◆', '◇', '【', '[').Trim();
            foreach (var k in keys)
            {
                var m = Regex.Match(line, "^" + Regex.Escape(k) + @"\s*(?:者)?\s*[：:　\s]\s*(?<n>.+)$", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                var name = m.Groups["n"].Value.Trim().Trim('】', ']', '　').Trim();
                if (name.Length is > 0 and <= 80 && !name.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return name;
            }
        }
        return null;
    }

    /// <summary>The text written next to the output: the PMX comments and any readme/terms files in the model's folder.</summary>
    public static string LicenseText(Pmx.PmxModel m, string pmxPath)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Converted from: {Path.GetFileName(pmxPath)}");
        sb.AppendLine($"Model name: {m.Name}" + (m.NameEn.Length > 0 && m.NameEn != m.Name ? $" / {m.NameEn}" : ""));
        sb.AppendLine("This VRM is a conversion of the model above. The original model's terms of use still apply;");
        sb.AppendLine("read them (below and in the model's own readme) before using or sharing this file.");
        sb.AppendLine();
        if (m.Comment.Trim().Length > 0) { sb.AppendLine("---- PMX comment ----"); sb.AppendLine(m.Comment.Replace("\r\n", "\n").Replace("\n", Environment.NewLine)); sb.AppendLine(); }
        if (m.CommentEn.Trim().Length > 0 && m.CommentEn != m.Comment) { sb.AppendLine("---- PMX comment (English) ----"); sb.AppendLine(m.CommentEn.Replace("\r\n", "\n").Replace("\n", Environment.NewLine)); sb.AppendLine(); }
        var dir = Path.GetDirectoryName(Path.GetFullPath(pmxPath));
        if (dir != null)
        {
            var pattern = new Regex("(readme|read_me|license|licence|terms|kiyaku|規約|りどみ|リードミー|readme|説明|利用|はじめに|必読)", RegexOptions.IgnoreCase);
            IEnumerable<string> files;
            try { files = Directory.GetFiles(dir, "*.txt").Concat(Directory.GetFiles(dir, "*.md")).Where(f => pattern.IsMatch(Path.GetFileName(f))).Take(8).ToList(); }
            catch (IOException) { files = Array.Empty<string>(); }
            foreach (var f in files)
            {
                try
                {
                    var bytes = File.ReadAllBytes(f);
                    if (bytes.Length > 512 * 1024) continue;
                    sb.AppendLine($"---- {Path.GetFileName(f)} ----");
                    sb.AppendLine(DecodeText(bytes));
                    sb.AppendLine();
                }
                catch (IOException) { }
            }
        }
        return sb.ToString();
    }

    /// <summary>UTF-8 (with or without BOM), UTF-16 with BOM, else Shift-JIS (most MMD readmes).</summary>
    static string DecodeText(byte[] b)
    {
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return System.Text.Encoding.Unicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return System.Text.Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
        try { return new System.Text.UTF8Encoding(false, true).GetString(b).TrimStart('﻿'); }
        catch (System.Text.DecoderFallbackException) { }
        try
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            return System.Text.Encoding.GetEncoding(932).GetString(b);
        }
        catch (Exception) { return System.Text.Encoding.Latin1.GetString(b); }
    }
}
