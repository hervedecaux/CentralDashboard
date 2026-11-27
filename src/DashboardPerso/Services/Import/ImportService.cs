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
    /// <summary>Crédit (salaire, remboursement…) : pas une dépense, non importé.</summary>
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

    /// <summary>Transfert : importé sans catégorie et hors budget.</summary>
    public bool IsTransfer { get; set; }

    /// <summary>Motif de la règle qui a choisi la catégorie (ou le transfert), pour l'afficher.</summary>
    public string? MatchedRule { get; set; }

    /// <summary>Vrai si l'utilisateur a choisi la catégorie ou le transfert à la main : les règles ne l'écrasent plus.</summary>
    public bool CategoryChosenByUser { get; set; }

    /// <summary>Vrai si la catégorie (ou le transfert) vient de la catégorie par défaut du bénéficiaire.</summary>
    public bool FromPayeeDefault { get; set; }

    /// <summary>Bénéficiaire déduit du libellé.</summary>
    public string Payee => Operation.Payee;

    public string Label { get; set; } = "";
}

public class ImportService(IDbContextFactory<AppDbContext> dbFactory)
{
    public const int MaxFileSize = 5 * 1024 * 1024;

    private sealed record Rule(string Pattern, string Normalized, int? CategoryId, bool MarkAsTransfer);

    private sealed record Defaults(List<Rule> Rules, Dictionary<string, (int? CategoryId, bool IsTransfer)> Payees);

    /// <summary>Lit le fichier et prépare l'aperçu : doublons détectés, catégories et transferts proposés.</summary>
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

        var rules = await LoadDefaultsAsync(db);
        var keys = ComputeKeys(operations);

        return operations.Select((op, i) =>
        {
            var status = !op.IsDebit ? ImportStatus.Credit
                : existingKeys.Contains(keys[i]) ? ImportStatus.AlreadyImported
                : manualExpenses.Contains((op.Date, op.Debit!.Value)) ? ImportStatus.PossibleDuplicate
                : ImportStatus.New;

            var row = new ImportRow
            {
                Operation = op,
                ImportKey = keys[i],
                Status = status,
                Include = status == ImportStatus.New,
                Label = Truncate(op.Description, 120),
            };
            ApplyRule(row, rules);
            return row;
        }).ToList();
    }

    /// <summary>Réapplique les règles (ex. après en avoir créé une) aux lignes non modifiées à la main.</summary>
    public async Task ApplyRulesAsync(IEnumerable<ImportRow> rows)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rules = await LoadDefaultsAsync(db);
        foreach (var row in rows.Where(r => !r.CategoryChosenByUser))
            ApplyRule(row, rules);
    }

    /// <summary>Enregistre les lignes cochées comme dépenses (ou transferts). Renvoie le nombre de lignes créées.</summary>
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
            var account = await AccountService.GetOrCreateForImportAsync(
                db, row.Operation.Source, row.Operation.DefaultSourceLabel, row.Operation.IsCardOperation);
            var payee = row.Operation.IsCardStatementTotal ? null : await PayeeService.GetOrCreateAsync(db, row.Payee);
            db.Expenses.Add(new Expense
            {
                Date = row.Operation.Date,
                Label = Truncate(string.IsNullOrWhiteSpace(row.Label) ? row.Operation.Description : row.Label, 120),
                Amount = row.Operation.Debit!.Value,
                CategoryId = row.IsTransfer ? null : row.CategoryId,
                IsTransfer = row.IsTransfer,
                Account = account,
                Payee = payee,
                Notes = Truncate(row.Operation.RawLabel, 500),
                ImportKey = row.ImportKey,
            });
            added++;
        }
        await db.SaveChangesAsync();
        return added;
    }

    /// <summary>
    /// Empreinte stable d'une opération : date + montants + libellé complet normalisé (+ carte d'origine).
    /// Deux opérations strictement identiques le même jour sont distinguées par leur rang.
    /// </summary>
    public static List<string> ComputeKeys(IReadOnlyList<BankOperation> operations)
    {
        var seen = new Dictionary<string, int>();
        var result = new List<string>(operations.Count);
        foreach (var op in operations)
        {
            var parts = new List<string>
            {
                op.Date.ToString("yyyy-MM-dd"),
                op.Debit?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) ?? "",
                op.Credit?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) ?? "",
                TextNormalizer.Normalize(op.RawLabel),
            };
            // Les opérations du compte gardent la même empreinte qu'avant l'ajout des cartes.
            if (op.IsCardOperation) parts.Add(TextNormalizer.Normalize(op.Source));

            var baseKey = string.Join('|', parts);
            var occurrence = seen[baseKey] = seen.GetValueOrDefault(baseKey) + 1;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{baseKey}#{occurrence}"));
            result.Add(Convert.ToHexString(hash));
        }
        return result;
    }

    /// <summary>
    /// Catégorie proposée : relevé de carte → transfert ; sinon catégorie par défaut du bénéficiaire ;
    /// sinon première règle qui correspond.
    /// </summary>
    private static void ApplyRule(ImportRow row, Defaults defaults)
    {
        row.MatchedRule = null;
        row.FromPayeeDefault = false;
        if (row.Operation.IsCardStatementTotal)
        {
            row.IsTransfer = true;
            row.CategoryId = null;
            return;
        }
        if (defaults.Payees.TryGetValue(TextNormalizer.Normalize(row.Payee), out var d))
        {
            row.IsTransfer = d.IsTransfer;
            row.CategoryId = d.IsTransfer ? null : d.CategoryId;
            row.FromPayeeDefault = true;
            return;
        }
        var rule = FindRule(defaults.Rules, row.Operation);
        row.IsTransfer = rule?.MarkAsTransfer ?? false;
        row.CategoryId = rule is { MarkAsTransfer: false } ? rule.CategoryId : null;
        row.MatchedRule = rule?.Pattern;
    }

    private static async Task<List<Rule>> LoadRulesAsync(AppDbContext db) =>
        (await db.CategoryRules.AsNoTracking().Where(r => r.CategoryId != null || r.MarkAsTransfer).ToListAsync())
            .Select(r => new Rule(r.Pattern, TextNormalizer.Normalize(r.Pattern), r.CategoryId, r.MarkAsTransfer))
            .Where(r => r.Normalized.Length > 0)
            // La règle la plus précise (motif le plus long) l'emporte.
            .OrderByDescending(r => r.Normalized.Length)
            .ToList();

    /// <summary>Règles + catégories par défaut des bénéficiaires.</summary>
    private static async Task<Defaults> LoadDefaultsAsync(AppDbContext db)
    {
        var payees = await db.Payees.AsNoTracking()
            .Where(p => p.DefaultCategoryId != null || p.DefaultIsTransfer)
            .ToDictionaryAsync(p => p.Key, p => (p.DefaultCategoryId, p.DefaultIsTransfer));
        return new Defaults(await LoadRulesAsync(db), payees);
    }

    private static Rule? FindRule(List<Rule> rules, BankOperation op)
    {
        var label = TextNormalizer.Normalize(op.RawLabel);
        return rules.FirstOrDefault(r => label.Contains(r.Normalized, StringComparison.Ordinal));
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
