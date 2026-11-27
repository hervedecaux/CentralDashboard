using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DashboardPerso.Services.Import;

/// <summary>
/// Lit l'export CSV « Liste des opérations » du Crédit Agricole :
/// encodage Windows-1252, séparateur « ; », une vingtaine de lignes d'en-tête,
/// puis « Date d'opération;Libellé;Débit euros;Crédit euros; ».
/// Le libellé est un champ multi-lignes (type d'opération, puis bénéficiaire, puis références).
/// Les sections « cartes » qui suivent la liste du compte (cartes à débit différé) sont lues aussi :
/// chaque opération porte la carte d'origine, et la ligne du compte qui débite le total
/// de la carte est repérée comme un transfert.
/// </summary>
public static partial class CreditAgricoleCsvParser
{
    private static readonly CultureInfo French = new("fr-FR");

    static CreditAgricoleCsvParser()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static List<BankOperation> Parse(byte[] content) => ParseText(Decode(content));

    /// <summary>Opérations + solde du compte indiqué en tête de relevé (« Solde au 27/11/2026 3 235,79 € »).</summary>
    public static BankStatement ParseStatement(byte[] content)
    {
        var text = Decode(content);
        var operations = ParseText(text);
        var m = BalanceLine().Match(text);
        return m.Success
            ? new BankStatement(operations,
                ParseAmount(m.Groups[2].Value),
                DateOnly.ParseExact(m.Groups[1].Value, "dd/MM/yyyy", French))
            : new BankStatement(operations, null, null);
    }

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
        string source = BankOperation.AccountSource;
        string? holder = null, cardHolder = null;
        var cardSums = new Dictionary<string, decimal>();

        foreach (var row in rows.Skip(header + 1))
        {
            if (row.All(string.IsNullOrWhiteSpace)) continue;          // ligne vide
            var first = row[0].Trim();

            if (!DateOnly.TryParseExact(first, "dd/MM/yyyy", French, DateTimeStyles.None, out var date))
            {
                // Sections cartes : « … - Porteur », puis « carte n° 513780******5524 », puis les opérations.
                if (first.EndsWith("- Porteur", StringComparison.OrdinalIgnoreCase))
                    holder = TitleCase(first[..first.LastIndexOf('-')].Trim());
                else if (CardNumber().Match(string.Join(' ', row)) is { Success: true } m)
                {
                    source = $"Carte ••{m.Groups[1].Value}";
                    cardHolder = holder;
                    holder = null;
                }
                continue;                                                // en-têtes, titulaires, « Pas d'opérations… »
            }
            if (row.Count < 4) continue;

            var lines = row[1].Split('\n')
                .Select(l => Spaces().Replace(l, " ").Trim())
                .Where(l => l.Length > 0)
                .ToList();

            var op = new BankOperation
            {
                Date = date,
                Type = lines.ElementAtOrDefault(0) ?? "",
                Description = lines.ElementAtOrDefault(1) ?? lines.ElementAtOrDefault(0) ?? "",
                RawLabel = string.Join(" | ", lines),
                Debit = ParseAmount(row[2]),
                Credit = ParseAmount(row[3]),
                Source = source,
                CardHolder = cardHolder,
            };
            operations.Add(op);
            if (op.IsCardOperation)
                cardSums[source] = cardSums.GetValueOrDefault(source) + (op.Debit ?? 0) - (op.Credit ?? 0);
        }

        return operations.Select(op => IsCardStatementTotal(op, cardSums.Values)
            ? op with { IsCardStatementTotal = true }
            : op).ToList();
    }

    /// <summary>
    /// Débit du compte correspondant au total d'une carte à débit différé :
    /// libellé mentionnant une carte, et montant égal au total d'une section carte du fichier
    /// (ou libellé explicite de relevé différé).
    /// </summary>
    private static bool IsCardStatementTotal(BankOperation op, IEnumerable<decimal> cardTotals)
    {
        if (op.IsCardOperation || !op.IsDebit) return false;
        var label = TextNormalizer.Normalize(op.RawLabel);
        if (!label.Contains("CARTE")) return false;
        return cardTotals.Any(t => Math.Abs(t - op.Debit!.Value) < 0.01m)
            || label.Contains("RELEVE") || label.Contains("DIFFERE") || label.Contains("TOTAL");
    }

    private static string TitleCase(string value) =>
        French.TextInfo.ToTitleCase(Spaces().Replace(value, " ").ToLower(French));

    /// <summary>« 1 386,53 » → 1386.53 ; vide → null.</summary>
    public static decimal? ParseAmount(string value)
    {
        var cleaned = Spaces().Replace(value, "").Replace("€", "");
        if (cleaned.Length == 0) return null;
        return decimal.Parse(cleaned, NumberStyles.Number, French);
    }

    // « Solde au 27/11/2026 3 235,79 € » (montant éventuellement négatif).
    [GeneratedRegex(@"Solde au (\d{2}/\d{2}/\d{4})\s+(-?[\d\s\u00A0\u202F]+,\d{2})")]
    private static partial Regex BalanceLine();

    [GeneratedRegex(@"carte\s+n\S*\s+\S*(\d{4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex CardNumber();

    // Espaces normales, insécables et fines insécables.
    [GeneratedRegex(@"[\s  ]+")]
    private static partial Regex Spaces();
}
