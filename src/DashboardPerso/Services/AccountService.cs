using DashboardPerso.Data;
using DashboardPerso.Models;
using Microsoft.EntityFrameworkCore;

namespace DashboardPerso.Services;

public record AccountSummary(Account Account, int ExpenseCount, DateOnly? LastExpense);

/// <summary>
/// Solde théorique : solde initial + recettes − dépenses (transferts compris) postérieures à sa date.
/// Comparé au dernier solde indiqué par la banque, à la même date.
/// </summary>
public record AccountBalance(
    decimal InitialBalance, DateOnly InitialDate,
    decimal Theoretical, DateOnly TheoreticalDate,
    decimal? BankBalance, DateOnly? BankDate, decimal? TheoreticalAtBankDate)
{
    public decimal? Difference => BankBalance - TheoreticalAtBankDate;
}

/// <summary>Que faire des opérations d'un compte ou d'une carte qu'on supprime.</summary>
public enum AccountDeletion
{
    /// <summary>Refuser la suppression si le compte a des opérations.</summary>
    OnlyIfEmpty,
    /// <summary>Supprimer aussi ses opérations (et leurs ventilations).</summary>
    DeleteOperations,
    /// <summary>Rattacher ses opérations à un autre compte ou une autre carte.</summary>
    MoveOperations,
}

public record AccountBalanceSummary(Account Account, AccountBalance? Balance);

public class AccountService(IDbContextFactory<AppDbContext> dbFactory)
{
    /// <summary>Comptes et cartes : le compte courant d'abord, puis par libellé.</summary>
    public async Task<List<Account>> GetAllAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return Order(await db.Accounts.AsNoTracking().ToListAsync());
    }

    public async Task<List<AccountSummary>> GetSummariesAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var stats = await db.Expenses.GroupBy(e => e.AccountId)
            .Select(g => new { g.Key, Count = g.Count(), Last = g.Max(e => e.Date) })
            .ToDictionaryAsync(x => x.Key);
        return Order(await db.Accounts.AsNoTracking().ToListAsync())
            .Select(a => stats.TryGetValue(a.Id, out var s)
                ? new AccountSummary(a, s.Count, s.Last)
                : new AccountSummary(a, 0, null))
            .ToList();
    }

    public async Task<Account?> GetAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id);
    }

    /// <summary>Solde théorique d'un compte à une date (aujourd'hui par défaut), null sans solde initial.</summary>
    public async Task<AccountBalance?> GetBalanceAsync(int accountId = Account.MainAccountId, DateOnly? at = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId);
        if (account?.InitialBalance is not { } initial || account.InitialBalanceDate is not { } from) return null;

        var date = at ?? DateOnly.FromDateTime(DateTime.Today);
        var movements = await db.Expenses
            .Where(e => e.AccountId == accountId && e.Date > from)
            .Select(e => new { e.Date, Signed = e.IsIncome ? e.Amount : -e.Amount })
            .ToListAsync();

        decimal BalanceAt(DateOnly d) => initial + movements.Where(m => m.Date <= d).Sum(m => m.Signed);

        var bankDate = account.BankBalanceDate is { } b && b > from ? b : (DateOnly?)null;
        return new AccountBalance(
            initial, from,
            BalanceAt(date > from ? date : from), date,
            bankDate is null ? null : account.BankBalance, bankDate,
            bankDate is { } bd ? BalanceAt(bd) : null);
    }

    /// <summary>Soldes des comptes bancaires (pas des cartes), le compte courant d'origine d'abord.</summary>
    public async Task<List<AccountBalanceSummary>> GetBankBalancesAsync(DateOnly? at = null)
    {
        var result = new List<AccountBalanceSummary>();
        foreach (var account in (await GetAllAsync()).Where(a => a.IsBankAccount))
            result.Add(new AccountBalanceSummary(account, await GetBalanceAsync(account.Id, at)));
        return result;
    }

    public async Task SetInitialBalanceAsync(int accountId, decimal? amount, DateOnly? date)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var account = await db.Accounts.FirstAsync(a => a.Id == accountId);
        account.InitialBalance = amount is null || date is null ? null : amount;
        account.InitialBalanceDate = amount is null || date is null ? null : date;
        await db.SaveChangesAsync();
    }

    /// <summary>Libellés déjà donnés aux comptes importés, par identifiant d'import.</summary>
    public async Task<Dictionary<string, string>> GetLabelsByImportIdAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Accounts.Where(a => a.ImportId != null).ToDictionaryAsync(a => a.ImportId!, a => a.Label);
    }

    public async Task SaveAsync(Account account)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var number = string.IsNullOrWhiteSpace(account.AccountNumber) ? null
            : new string(account.AccountNumber.Where(char.IsLetterOrDigit).ToArray());
        if (number is not null && await db.Accounts.AnyAsync(a => a.AccountNumber == number && a.Id != account.Id))
            throw new InvalidOperationException($"Le numéro {number} est déjà celui d'un autre compte.");
        var entity = account.Id == 0 ? new Account() : await db.Accounts.FirstAsync(a => a.Id == account.Id);
        entity.Label = account.Label.Trim();
        entity.Kind = account.Kind;
        entity.AccountNumber = number;
        if (account.Id == 0) db.Accounts.Add(entity);   // un compte créé à la main n'a pas d'ImportId
        await db.SaveChangesAsync();
        account.Id = entity.Id;
    }

    /// <summary>
    /// Supprime un compte ou une carte. Par défaut, seulement s'il n'a aucune opération ; sinon ses opérations sont
    /// supprimées avec lui, ou rattachées à <paramref name="moveTo"/>. Le compte courant d'origine ne peut pas être
    /// supprimé (c'est le compte par défaut des saisies). Renvoie le nombre d'opérations supprimées ou déplacées.
    /// </summary>
    public async Task<int> DeleteAsync(int id, AccountDeletion mode = AccountDeletion.OnlyIfEmpty, int? moveTo = null)
    {
        if (id == Account.MainAccountId)
            throw new InvalidOperationException("Le compte courant principal ne peut pas être supprimé (vous pouvez le renommer).");
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!await db.Accounts.AnyAsync(a => a.Id == id))
            throw new InvalidOperationException("Ce compte n'existe plus.");

        await using var tx = await db.Database.BeginTransactionAsync();
        var operations = db.Expenses.Where(e => e.AccountId == id);
        var count = await operations.CountAsync();
        if (count > 0)
        {
            switch (mode)
            {
                case AccountDeletion.OnlyIfEmpty:
                    throw new InvalidOperationException(
                        "Ce compte a des opérations : supprimez-les avec lui ou rattachez-les à un autre compte.");
                case AccountDeletion.DeleteOperations:
                    await db.ExpenseSplits.Where(x => operations.Select(e => e.Id).Contains(x.ExpenseId)).ExecuteDeleteAsync();
                    await operations.ExecuteDeleteAsync();
                    break;
                case AccountDeletion.MoveOperations:
                    if (moveTo is not { } target || target == id || !await db.Accounts.AnyAsync(a => a.Id == target))
                        throw new InvalidOperationException("Choisissez le compte ou la carte qui recevra les opérations.");
                    await operations.ExecuteUpdateAsync(x => x.SetProperty(e => e.AccountId, target));
                    break;
            }
        }
        await db.Accounts.Where(a => a.Id == id).ExecuteDeleteAsync();
        await tx.CommitAsync();
        return count;
    }

    /// <summary>Compte correspondant à une provenance d'import, créé s'il n'existe pas encore.</summary>
    internal static async Task<Account> GetOrCreateForImportAsync(AppDbContext db, string importId, string defaultLabel, bool isCard)
    {
        var account = db.Accounts.Local.FirstOrDefault(a => a.ImportId == importId)
            ?? await db.Accounts.FirstOrDefaultAsync(a => a.ImportId == importId);
        if (account is not null) return account;

        account = new Account
        {
            ImportId = importId,
            Label = defaultLabel,
            Kind = isCard ? AccountKind.DeferredDebitCard : AccountKind.CheckingAccount,
        };
        db.Accounts.Add(account);
        return account;
    }

    private static List<Account> Order(List<Account> accounts) => accounts
        .OrderBy(a => a.Id != Account.MainAccountId)
        .ThenBy(a => a.Kind)
        .ThenBy(a => a.Label, StringComparer.CurrentCulture)
        .ToList();
}
