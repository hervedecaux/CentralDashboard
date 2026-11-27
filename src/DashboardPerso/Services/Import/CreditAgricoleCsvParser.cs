using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DashboardPerso.Services.Import;

/// <summary>
/// Lit l'export CSV « Liste des opérations » du Crédit Agricole :
/// encodage Windows-1252, séparateur « ; », une vingtaine de lignes d'en-tête,
/// puis « Date d'opération;Libellé;Débit euros;Crédit euros; ».
/// Le libellé est un champ multi-lignes (type d'opération, puis bénéficiaire, puis références).
/// Les sections « cartes » qui suivent la liste du compte sont ignorées.
/// </summary>
public static partial class CreditAgricoleCsvParser
{
    private static readonly CultureInfo French = new("fr-FR");

    static CreditAgricoleCsvParser()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static List<BankOperation> Parse(byte[] content) => ParseText(Decode(content));

    /// <summary>UTF-8 si le fichier en est valide, sinon Windows-1252 (format habituel de la banque).</summary>
    public static string Decode(byte[] content)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1252).GetString(content);
        }
    }

    public static List<BankOperation> ParseText(string text)
    {
        var rows = CsvReader.Parse(text);

        var header = rows.FindIndex(r => r.Count >= 4
            && r[0].Trim().StartsWith("Date", StringComparison.OrdinalIgnoreCase)
            && r.Any(c => c.Contains("Débit", StringComparison.OrdinalIgnoreCase)));
        if (header < 0)
            throw new FormatException(
                "Ligne d'en-tête « Date d'opération;Libellé;Débit euros;Crédit euros » introuvable. " +
                "Ce fichier ne semble pas être un export Crédit Agricole.");

        var operations = new List<BankOperation>();
        foreach (var row in rows.Skip(header + 1))
        {
            if (row.All(string.IsNullOrWhiteSpace)) continue;          // ligne vide
            if (!DateOnly.TryParseExact(row[0].Trim(), "dd/MM/yyyy", French, DateTimeStyles.None, out var date))
                break;                                                   // fin de la liste du compte (sections cartes)
            if (row.Count < 4) continue;

            var lines = row[1].Split('\n')
                .Select(l => Spaces().Replace(l, " ").Trim())
                .Where(l => l.Length > 0)
                .ToList();

            operations.Add(new BankOperation
            {
                Date = date,
                Type = lines.ElementAtOrDefault(0) ?? "",
                Description = lines.ElementAtOrDefault(1) ?? lines.ElementAtOrDefault(0) ?? "",
                RawLabel = string.Join(" | ", lines),
                Debit = ParseAmount(row[2]),
                Credit = ParseAmount(row[3]),
            });
        }
        return operations;
    }

    /// <summary>« 1 386,53 » → 1386.53 ; vide → null.</summary>
    public static decimal? ParseAmount(string value)
    {
        var cleaned = Spaces().Replace(value, "").Replace("€", "");
        if (cleaned.Length == 0) return null;
        return decimal.Parse(cleaned, NumberStyles.Number, French);
    }

    // Espaces normales, insécables et fines insécables.
    [GeneratedRegex(@"[\s  ]+")]
    private static partial Regex Spaces();
}
