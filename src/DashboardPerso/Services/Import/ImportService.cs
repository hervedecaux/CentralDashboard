using System.Security.Cryptography;
using System.Text;
using DashboardPerso.Data;
using DashboardPerso.Models;
using Microsoft.EntityFrameworkCore;

namespace DashboardPerso.Services.Import;

public enum ImportStatus
{
    /// <summary>Débit jamais importé : coché par défaut.</summary>
    New,
    /// <summary>Même opération déjà importée (même empreinte) : ignorée.</summary>
    AlreadyImported,
    /// <summary>Une dépense saisie à la main a la même date et le même montant : décoché par défaut.</summary>
    PossibleDuplicate,
    /// <summary>Crédit (salaire, remboursement…) : pas une dépense, décoché par défaut.</summary>
    Credit
}

/// <summary>Une ligne de l'aperçu d'import, modifiable par l'utilisateur avant validation.</summary>
public class ImportRow
{
    public required BankOperation Operation { get; init; }
    public required string ImportKey { get; init; }
    public required ImportStatus Status { get; init; }
    public bool Include { get; set; }
    public int? CategoryId { get; set; }
    /// <summary>Motif de la règle qui a choisi la catégorie, pour l'afficher.</summary>
    public string? MatchedRule { get; set; }
    /// <summary>Vrai si l'utilisateur a choisi la catégorie à la main : les règles ne l'écrasent plus.</summary>
    public bool CategoryChosenByUser { get; set; }
    public string Label { get; set; } = "";
}

public class ImportService(IDbContextFactory<AppDbContext> dbFactory)
{
    public const int MaxFileSize = 5 * 1024 * 1024;

    /// <summary>Lit le fichier et prépare l'aperçu : doublons détectés, catégories proposées.</summary>
    public async Task<List<ImportRow>> PreviewAsync(byte[] content)
    {
        var operations = CreditAgricoleCsvParser.Parse(content);
        if (operations.Count == 0) return [];

        await using var db = await dbFactory.CreateDbContextAsync();
        var from = operations.Min(o => o.Date);
        var to = operations.Max(o => o.Date);

        var existingKeys = (await db.Expenses
                .Where(e => e.ImportKey != null && e.Date >= from && e.Date <= to)
                .Select(e => e.ImportKey!)
                .ToListAsync())
            .ToHashSet();

        var manualExpenses = (await db.Expenses
                .Where(e => e.ImportKey == null && e.Date >= from && e.Date <= to)
                .Select(e => new { e.Date, e.Amount })
                .ToListAsync())
            .Select(e => (e.Date, e.Amount))
            .ToHashSet();

        var rules = await LoadRulesAsync(db);
        var keys = ComputeKeys(operations);

        return operations.Select((op, i) =>
        {
            var status = !op.IsDebit ? ImportStatus.Credit
                : existingKeys.Contains(keys[i]) ? ImportStatus.AlreadyImported
                : manualExpenses.Contains((op.Date, op.Debit!.Value)) ? ImportStatus.PossibleDuplicate
                : ImportStatus.New;

            var rule = FindRule(rules, op);
            return new ImportRow
            {
                Operation = op,
                ImportKey = keys[i],
                Status = status,
                Include = status == ImportStatus.New,
                CategoryId = rule?.CategoryId,
                MatchedRule = rule?.Pattern,
                Label = Truncate(op.Description, 120),
            };
        }).ToList();
    }

    /// <summary>Réapplique les règles (ex. après en avoir créé une) aux lignes non catégorisées à la main.</summary>
    public async Task ApplyRulesAsync(IEnumerable<ImportRow> rows)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rules = await LoadRulesAsync(db);
        foreach (var row in rows.Where(r => !r.CategoryChosenByUser))
        {
            var rule = FindRule(rules, row.Operation);
            row.CategoryId = rule?.CategoryId;
            row.MatchedRule = rule?.Pattern;
        }
    }

    /// <summary>Enregistre les lignes cochées comme dépenses. Renvoie le nombre de dépenses créées.</summary>
    public async Task<int> ImportAsync(IEnumerable<ImportRow> rows)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var selected = rows.Where(r => r.Include && r.Operation.IsDebit).ToList();

        // Revérifie au moment de l'enregistrement (double clic, import en parallèle…).
        var keys = selected.Select(r => r.ImportKey).ToList();
        var already = (await db.Expenses.Where(e => keys.Contains(e.ImportKey!)).Select(e => e.ImportKey!).ToListAsync())
            .ToHashSet();

        var added = 0;
        foreach (var row in selected.Where(r => !already.Contains(r.ImportKey)))
        {
            db.Expenses.Add(new Expense
            {
                Date = row.Operation.Date,
                Label = Truncate(string.IsNullOrWhiteSpace(row.Label) ? row.Operation.Description : row.Label, 120),
                Amount = row.Operation.Debit!.Value,
                CategoryId = row.CategoryId,
                Notes = Truncate(row.Operation.RawLabel, 500),
                ImportKey = row.ImportKey,
            });
            added++;
        }
        await db.SaveChangesAsync();
        return added;
    }

    /// <summary>
    /// Empreinte stable d'une opération : date + montants + libellé complet normalisé.
    /// Deux opérations strictement identiques le même jour sont distinguées par leur rang.
    /// </summary>
    public static List<string> ComputeKeys(IReadOnlyList<BankOperation> operations)
    {
        var seen = new Dictionary<string, int>();
        var result = new List<string>(operations.Count);
        foreach (var op in operations)
        {
            var baseKey = string.Join('|',
                op.Date.ToString("yyyy-MM-dd"),
                op.Debit?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) ?? "",
                op.Credit?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) ?? "",
                TextNormalizer.Normalize(op.RawLabel));
            var occurrence = seen[baseKey] = seen.GetValueOrDefault(baseKey) + 1;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{baseKey}#{occurrence}"));
            result.Add(Convert.ToHexString(hash));
        }
        return result;
    }

    private static async Task<List<(string Pattern, string Normalized, int CategoryId)>> LoadRulesAsync(AppDbContext db) =>
        (await db.CategoryRules.AsNoTracking().Where(r => r.CategoryId != null).ToListAsync())
            .Select(r => (r.Pattern, TextNormalizer.Normalize(r.Pattern), r.CategoryId!.Value))
            .Where(r => r.Item2.Length > 0)
            // La règle la plus précise (motif le plus long) l'emporte.
            .OrderByDescending(r => r.Item2.Length)
            .ToList();

    private static (string Pattern, string Normalized, int CategoryId)? FindRule(
        List<(string Pattern, string Normalized, int CategoryId)> rules, BankOperation op)
    {
        var label = TextNormalizer.Normalize(op.RawLabel);
        foreach (var rule in rules)
            if (label.Contains(rule.Normalized, StringComparison.Ordinal))
                return rule;
        return null;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
