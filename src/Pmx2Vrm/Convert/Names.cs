using System.Text;

namespace Pmx2Vrm.Convert;

public static class Names
{
    /// <summary>
    /// Normalizes a bone/morph name for matching: NFKC (full-width digits and letters to ASCII, half-width
    /// katakana to full-width), lower case, no spaces, underscores, dots or hyphens.
    /// </summary>
    public static string Norm(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var n = s.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var sb = new StringBuilder(n.Length);
        foreach (var c in n)
        {
            if (c == ' ' || c == '_' || c == '.' || c == '-' || c == '\t' || c == '　') continue;
            sb.Append(c);
        }
        return sb.ToString();
    }
}
