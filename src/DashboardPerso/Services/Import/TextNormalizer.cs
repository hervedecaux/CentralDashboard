using System.Globalization;
using System.Text;

namespace DashboardPerso.Services.Import;

public static class TextNormalizer
{
    /// <summary>Majuscules, sans accents, espaces réduits : « Prélèvement  EDF » → « PRELEVEMENT EDF ».</summary>
    public static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var lastWasSpace = false;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace && sb.Length > 0) sb.Append(' ');
                lastWasSpace = true;
                continue;
            }
            sb.Append(char.ToUpperInvariant(c));
            lastWasSpace = false;
        }
        return sb.ToString().TrimEnd();
    }
}
