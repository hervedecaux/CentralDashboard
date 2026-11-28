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

/// <summary>Opérations importées à effacer : toutes, ou celles d'un compte / d'une carte et/ou d'une période (bornes incluses).</summary>
public record ImportedFilter(int? AccountId = null, DateOnly? From = null, DateOnly? To = null)
{
    public bool IsEverything => AccountId is null && From is null && To is null;
}

/// <summary>Ce que l'effacement toucherait.</summary>
public record ImportedSummary(int Count, int Income, int Transfers, decimal Debits, decimal Credits, DateOnly? First, DateOnly? Last);

/// <summary>
/// Compte auquel les opérations « compte » d'un relevé sont rattachées (les cartes gardent leur propre compte).
/// <see cref="AccountId"/> est null pour un compte à créer à l'import.
/// </summary>
public record ImportTarget(int? AccountId, string ImportId, string Label, string? AccountNumber)
{
    public bool IsNew => AccountId is null;
}

/// <summary>Aperçu d'un relevé : lignes, solde indiqué par la banque et compte de destination.</summary>
public record ImportPreview(List<ImportRow> Rows, decimal? BankBalance, DateOnly? BankBalanceDate, ImportTarget? Target = null)
{
    /// <summary>Numéro de compte lu en tête du fichier (null s'il n'y figure pas).</summary>
    public string? AccountNumber => Target?.AccountNumber;
}

public class ImportService(IDbContextFactory<AppDbContext> dbFactory)
{
    public const int MaxFileSize = 5 * 1024 * 1024;

    /// <summary>Valeur de <c>targetAccountId</c> pour importer le relevé dans un nouveau compte.</summary>
    public const int NewAccount = 0;

    private sealed record Rule(string Pattern, string Normalized, int? CategoryId, bool MarkAsTransfer);

    private sealed record Defaults(
        List<Rule> Rules,
        Dictionary<string, (int? CategoryId, bool IsTransfer)> Payees,
        HashSet<int> IncomeCategoryIds)
    {
        /// <summary>
        /// Vrai si la catégorie convient au sens de l'opération : une dépense ne va que dans une catégorie de dépenses ;
        /// un crédit va dans une catégorie de recettes, ou dans une catégorie de dépenses (remboursement).
        /// </summary>
        public bool Fits(int? categoryId, bool isIncome) =>
            categoryId is { } id && (isIncome || !IncomeCategoryIds.Contains(id));

        public bool IsIncomeCategory(int? categoryId) => categoryId is { } id && IncomeCategoryIds.Contains(id);
    }

    public async Task<List<ImportRow>> PreviewAsync(byte[] content, int? targetAccountId = null) =>
        (await PreviewStatementAsync(content, targetAccountId)).Rows;

    /// <summary>
    /// Lit le fichier et prépare l'aperçu : doublons détectés, catégories et transferts proposés.
    /// <paramref name="targetAccountId"/> : compte qui reçoit les opérations du compte (pas celles des cartes) ;
    /// null pour le choisir d'après le numéro de compte du fichier, <see cref="NewAccount"/> pour en créer un.
    /// </summary>
    public async Task<ImportPreview> PreviewStatementAsync(byte[] content, int? targetAccountId = null)
    {
        var statement = CreditAgricoleCsvParser.ParseStatement(content);
        var operations = statement.Operations;

        await using var db = await dbFactory.CreateDbContextAsync();
        var target = await ResolveTargetAsync(db, statement.AccountNumber, targetAccountId);
        if (operations.Count == 0) return new ImportPreview([], statement.Balance, statement.BalanceDate, target);
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
        var keys = ComputeKeys(operations, KeySalt(target));

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
        return new ImportPreview(rows, statement.Balance, statement.BalanceDate, target);
    }

    /// <summary>
    /// Compte de destination : celui qui porte le numéro du fichier ; sinon le compte courant d'origine s'il n'a pas
    /// encore de numéro (premier import, ou base créée avant la gestion de plusieurs comptes) ; sinon un nouveau compte.
    /// </summary>
    private static async Task<ImportTarget> ResolveTargetAsync(AppDbContext db, string? number, int? requested)
    {
        var accounts = await db.Accounts.AsNoTracking().ToListAsync();
        var account = requested switch
        {
            NewAccount => null,
            { } id => accounts.FirstOrDefault(a => a.Id == id && a.IsBankAccount)
                      ?? throw new InvalidOperationException("Ce compte n'existe pas (ou c'est une carte)."),
            null => (number is null ? null : accounts.FirstOrDefault(a => a.AccountNumber == number))
                    ?? accounts.FirstOrDefault(a => a.Id == Account.MainAccountId && a.AccountNumber is null)
                    ?? (number is null ? accounts.First(a => a.Id == Account.MainAccountId) : null),
        };

        if (account is not null)
        {
            var importId = account.Id == Account.MainAccountId ? BankOperation.AccountSource
                : account.ImportId ?? FreeImportId(accounts, number);
            return new ImportTarget(account.Id, importId, account.Label, number);
        }
        var label = number is null ? "Nouveau compte" : $"Compte ••{number[^Math.Min(4, number.Length)..]}";
        return new ImportTarget(null, FreeImportId(accounts, number), label, number);
    }

    /// <summary>Identifiant d'import d'un compte autre que le compte courant d'origine : « Compte 12345678901 ».</summary>
    private static string FreeImportId(List<Account> accounts, string? number)
    {
        var id = number is null ? $"Compte {Guid.NewGuid():N}"[..16] : $"Compte {number}";
        return accounts.Any(a => a.ImportId == id) ? $"{id} {Guid.NewGuid():N}"[..Math.Min(80, id.Length + 9)] : id;
    }

    /// <summary>
    /// Les opérations du compte courant d'origine gardent leur empreinte historique ; celles d'un autre compte
    /// incluent son identifiant, pour que deux comptes aux opérations identiques ne se masquent pas.
    /// </summary>
    private static string? KeySalt(ImportTarget? target) =>
        target is null || target.ImportId == BankOperation.AccountSource ? null : target.ImportId;

    /// <summary>Réapplique les règles (ex. après en avoir créé une) aux lignes non modifiées à la main.</summary>
    public async Task ApplyRulesAsync(IEnumerable<ImportRow> rows)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rules = await LoadDefaultsAsync(db);
        foreach (var row in rows.Where(r => !r.CategoryChosenByUser))
            ApplyRule(row, rules);
    }

    /// <summary>Enregistre les lignes cochées comme dépenses (ou transferts). Renvoie le nombre de lignes créées.</summary>
    public Task<int> ImportAsync(IEnumerable<ImportRow> rows, decimal? bankBalance = null, DateOnly? bankBalanceDate = null) =>
        ImportAsync(rows, null, bankBalance, bankBalanceDate);

    public Task<int> ImportAsync(ImportPreview preview) =>
        ImportAsync(preview.Rows, preview.Target, preview.BankBalance, preview.BankBalanceDate);

    /// <summary>
    /// Enregistre les lignes cochées. Les opérations du compte vont dans <paramref name="target"/> (créé au besoin,
    /// le compte courant d'origine si null), celles des cartes dans le compte de chaque carte.
    /// </summary>
    public async Task<int> ImportAsync(IEnumerable<ImportRow> rows, ImportTarget? target, decimal? bankBalance, DateOnly? bankBalanceDate)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var selected = rows.Where(r => r.Include && r.Operation.Amount > 0).ToList();

        var account = await GetOrCreateTargetAsync(db, target);

        // Solde indiqué par la banque : conservé s'il est plus récent, pour le comparer au solde théorique.
        if (bankBalance is not null && bankBalanceDate is not null)
        {
            if (account.BankBalanceDate is null || account.BankBalanceDate <= bankBalanceDate)
            {
                account.BankBalance = bankBalance;
                account.BankBalanceDate = bankBalanceDate;
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
            var source = row.Operation.IsCardOperation
                ? await AccountService.GetOrCreateForImportAsync(
                    db, row.Operation.Source, row.Operation.DefaultSourceLabel, isCard: true)
                : account;
            var payee = row.Operation.IsCardStatementTotal ? null : await PayeeService.GetOrCreateAsync(db, row.Payee);
            db.Expenses.Add(new Expense
            {
                Date = row.Operation.Date,
                Label = Truncate(string.IsNullOrWhiteSpace(row.Label) ? row.Operation.Description : row.Label, 120),
                Amount = row.Operation.Amount,
                                // Une dépense ne garde pas une catégorie de recettes ; un crédit accepte les deux (remboursement).
                CategoryId = row.IsTransfer || row.CategoryId is not { } cat || (!row.IsIncome && incomeCategories.Contains(cat))
                    ? null : row.CategoryId,
                IsTransfer = row.IsTransfer,
                IsIncome = row.IsIncome,
                Account = source,
                Payee = payee,
                Notes = Truncate(row.Operation.RawLabel, 500),
                ImportKey = row.ImportKey,
            });
            added++;
        }
        await db.SaveChangesAsync();
        return added;
    }

    /// <summary>Compte de destination, créé s'il est nouveau ; son numéro et son identifiant d'import sont mémorisés.</summary>
    private static async Task<Account> GetOrCreateTargetAsync(AppDbContext db, ImportTarget? target)
    {
        if (target is null) return await db.Accounts.FirstAsync(a => a.Id == Account.MainAccountId);

        Account account;
        if (target.AccountId is { } id)
            account = await db.Accounts.FirstAsync(a => a.Id == id);
        else
        {
            // Double validation : le compte a pu être créé entre-temps.
            account = await db.Accounts.FirstOrDefaultAsync(a => a.ImportId == target.ImportId)
                ?? db.Accounts.Add(new Account { Label = target.Label, Kind = AccountKind.CheckingAccount }).Entity;
        }
        if (account.Id != Account.MainAccountId) account.ImportId ??= target.ImportId;
        if (target.AccountNumber is { } number && account.AccountNumber is null
            && !await db.Accounts.AnyAsync(a => a.AccountNumber == number && a.Id != account.Id))
            account.AccountNumber = number;
        return account;
    }

    /// <summary>Résumé des opérations importées correspondant au filtre (les saisies manuelles ne sont jamais comptées).</summary>
    public async Task<ImportedSummary> GetImportedSummaryAsync(ImportedFilter filter)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var list = await Imported(db, filter)
            .Select(e => new { e.Date, e.Amount, e.IsIncome, e.IsTransfer })
            .ToListAsync();
        return new ImportedSummary(
            list.Count,
            list.Count(e => e.IsIncome),
            list.Count(e => e.IsTransfer),
            list.Where(e => !e.IsIncome).Sum(e => e.Amount),
            list.Where(e => e.IsIncome).Sum(e => e.Amount),
            list.Count > 0 ? list.Min(e => e.Date) : null,
            list.Count > 0 ? list.Max(e => e.Date) : null);
    }

    /// <summary>
    /// Efface les opérations importées correspondant au filtre (et leurs ventilations), pour pouvoir les réimporter.
    /// Catégories, budgets, règles, bénéficiaires, comptes, solde initial et saisies manuelles sont conservés.
    /// Quand toutes les opérations importées d'un compte sont effacées, son solde banque mémorisé l'est aussi.
    /// Renvoie le nombre d'opérations effacées.
    /// </summary>
    public async Task<int> DeleteImportedAsync(ImportedFilter filter)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var ids = Imported(db, filter).Select(e => e.Id);
        await db.ExpenseSplits.Where(x => ids.Contains(x.ExpenseId)).ExecuteDeleteAsync();
        var deleted = await Imported(db, filter).ExecuteDeleteAsync();

        // Toutes les opérations importées d'un compte effacées : son solde banque mémorisé ne correspond plus à rien.
        if (filter.From is null && filter.To is null)
            await db.Accounts.Where(a => filter.AccountId == null || a.Id == filter.AccountId)
                .ExecuteUpdateAsync(x => x.SetProperty(a => a.BankBalance, (decimal?)null)
                                          .SetProperty(a => a.BankBalanceDate, (DateOnly?)null));
        await tx.CommitAsync();
        return deleted;
    }

    private static IQueryable<Expense> Imported(AppDbContext db, ImportedFilter f)
    {
        var q = db.Expenses.Where(e => e.ImportKey != null);
        if (f.AccountId is { } a) q = q.Where(e => e.AccountId == a);
        if (f.From is { } from) q = q.Where(e => e.Date >= from);
        if (f.To is { } to) q = q.Where(e => e.Date <= to);
        return q;
    }

    /// <summary>
    /// Empreinte stable d'une opération : date + montants + libellé complet normalisé (+ carte d'origine).
    /// Deux opérations strictement identiques le même jour sont distinguées par leur rang.
    /// </summary>
    public static List<string> ComputeKeys(IReadOnlyList<BankOperation> operations, string? accountSalt = null)
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
            else if (accountSalt is not null) parts.Add(TextNormalizer.Normalize(accountSalt));

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
        var candidates = defaults.Rules.Where(r => r.MarkAsTransfer || defaults.Fits(r.CategoryId, row.IsIncome));
        // À longueur de motif égale, un crédit préfère une catégorie de recettes à un remboursement.
        if (row.IsIncome)
            candidates = candidates.OrderByDescending(r => r.Normalized.Length)
                .ThenByDescending(r => defaults.IsIncomeCategory(r.CategoryId));
        var rule = FindRule(candidates, row.Operation);
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
