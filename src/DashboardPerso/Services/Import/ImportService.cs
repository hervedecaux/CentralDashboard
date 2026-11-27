using System.Security.Cryptography;
using System.Text;
using DashboardPerso.Data;
using DashboardPerso.Models;
using Microsoft.EntityFrameworkCore;

namespace DashboardPerso.Services.Import;

public enum ImportStatus
{
    /// <summary>Opération jamais importée : cochée par défaut.</summary>
    New,
    /// <summary>Même opération déjà importée (même empreinte) : ignorée.</summary>
    AlreadyImported,
    /// <summary>Une opération saisie à la main a la même date et le même montant : décochée par défaut.</summary>
    PossibleDuplicate,
}

/// <summary>Une ligne de l'aperçu d'import, modifiable par l'utilisateur avant validation.</summary>
public class ImportRow
{
    public required BankOperation Operation { get; init; }
    public required string ImportKey { get; init; }
    public required ImportStatus Status { get; init; }
    public bool Include { get; set; }
    public int? CategoryId { get; set; }

    /// <summary>Crédit (salaire, remboursement…) : importé comme recette, dans une catégorie de recettes.</summary>
    public bool IsIncome => !Operation.IsDebit;

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

/// <summary>Aperçu d'un relevé : lignes et solde indiqué par la banque.</summary>
public record ImportPreview(List<ImportRow> Rows, decimal? BankBalance, DateOnly? BankBalanceDate);

public class ImportService(IDbContextFactory<AppDbContext> dbFactory)
{
    public const int MaxFileSize = 5 * 1024 * 1024;

    private sealed record Rule(string Pattern, string Normalized, int? CategoryId, bool MarkAsTransfer);

    private sealed record Defaults(
        List<Rule> Rules,
        Dictionary<string, (int? CategoryId, bool IsTransfer)> Payees,
        HashSet<int> IncomeCategoryIds)
    {
        /// <summary>Vrai si la catégorie convient au sens de l'opération (recette ↔ catégorie de recettes).</summary>
        public bool Fits(int? categoryId, bool isIncome) =>
            categoryId is { } id && IncomeCategoryIds.Contains(id) == isIncome;
    }

    public async Task<List<ImportRow>> PreviewAsync(byte[] content) => (await PreviewStatementAsync(content)).Rows;

    /// <summary>Lit le fichier et prépare l'aperçu : doublons détectés, catégories et transferts proposés.</summary>
    public async Task<ImportPreview> PreviewStatementAsync(byte[] content)
    {
        var statement = CreditAgricoleCsvParser.ParseStatement(content);
        var operations = statement.Operations;
        if (operations.Count == 0) return new ImportPreview([], statement.Balance, statement.BalanceDate);

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
                .Select(e => new { e.Date, e.Amount, e.IsIncome })
                .ToListAsync())
            .Select(e => (e.Date, e.Amount, e.IsIncome))
            .ToHashSet();

        var rules = await LoadDefaultsAsync(db);
        var keys = ComputeKeys(operations);

        var rows = operations.Select((op, i) =>
        {
            var status = existingKeys.Contains(keys[i]) ? ImportStatus.AlreadyImported
                : manualExpenses.Contains((op.Date, op.Amount, !op.IsDebit)) ? ImportStatus.PossibleDuplicate
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
        return new ImportPreview(rows, statement.Balance, statement.BalanceDate);
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
    public async Task<int> ImportAsync(IEnumerable<ImportRow> rows, decimal? bankBalance = null, DateOnly? bankBalanceDate = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var selected = rows.Where(r => r.Include && r.Operation.Amount > 0).ToList();

        // Solde indiqué par la banque : conservé s'il est plus récent, pour le comparer au solde théorique.
        if (bankBalance is not null && bankBalanceDate is not null)
        {
            var main = await db.Accounts.FirstAsync(a => a.Id == Account.MainAccountId);
            if (main.BankBalanceDate is null || main.BankBalanceDate <= bankBalanceDate)
            {
                main.BankBalance = bankBalance;
                main.BankBalanceDate = bankBalanceDate;
            }
        }

        // Revérifie au moment de l'enregistrement (double clic, import en parallèle…).
        var keys = selected.Select(r => r.ImportKey).ToList();
        var already = (await db.Expenses.Where(e => keys.Contains(e.ImportKey!)).Select(e => e.ImportKey!).ToListAsync())
            .ToHashSet();

        var incomeCategories = (await db.BudgetCategories.Where(c => c.Kind == CategoryKind.Income)
            .Select(c => c.Id).ToListAsync()).ToHashSet();

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
                Amount = row.Operation.Amount,
                // Catégorie gardée seulement si elle correspond au sens de l'opération.
                CategoryId = row.IsTransfer || row.CategoryId is not { } cat || incomeCategories.Contains(cat) != row.IsIncome
                    ? null : row.CategoryId,
                IsTransfer = row.IsTransfer,
                IsIncome = row.IsIncome,
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
        // Une recette ne prend qu'une catégorie de recettes, une dépense qu'une catégorie de dépenses.
        if (defaults.Payees.TryGetValue(TextNormalizer.Normalize(row.Payee), out var d)
            && (d.IsTransfer || defaults.Fits(d.CategoryId, row.IsIncome)))
        {
            row.IsTransfer = d.IsTransfer;
            row.CategoryId = d.IsTransfer ? null : d.CategoryId;
            row.FromPayeeDefault = true;
            return;
        }
        var rule = FindRule(defaults.Rules.Where(r => r.MarkAsTransfer || defaults.Fits(r.CategoryId, row.IsIncome)),
            row.Operation);
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
        var income = (await db.BudgetCategories.Where(c => c.Kind == CategoryKind.Income).Select(c => c.Id).ToListAsync())
            .ToHashSet();
        return new Defaults(await LoadRulesAsync(db), payees, income);
    }

    private static Rule? FindRule(IEnumerable<Rule> rules, BankOperation op)
    {
        var label = TextNormalizer.Normalize(op.RawLabel);
        return rules.FirstOrDefault(r => label.Contains(r.Normalized, StringComparison.Ordinal));
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
