using DashboardPerso.Data;
using DashboardPerso.Models;
using Microsoft.EntityFrameworkCore;

namespace DashboardPerso.Services;

public record AccountSummary(Account Account, int ExpenseCount, DateOnly? LastExpense);

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

    /// <summary>Libellés déjà donnés aux comptes importés, par identifiant d'import.</summary>
    public async Task<Dictionary<string, string>> GetLabelsByImportIdAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Accounts.Where(a => a.ImportId != null).ToDictionaryAsync(a => a.ImportId!, a => a.Label);
    }

    public async Task SaveAsync(Account account)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var entity = account.Id == 0 ? new Account() : await db.Accounts.FirstAsync(a => a.Id == account.Id);
        entity.Label = account.Label.Trim();
        entity.Kind = account.Kind;
        if (account.Id == 0) db.Accounts.Add(entity);   // un compte créé à la main n'a pas d'ImportId
        await db.SaveChangesAsync();
        account.Id = entity.Id;
    }

    /// <summary>Supprime un compte sans dépense. Le compte courant ne peut pas être supprimé.</summary>
    public async Task DeleteAsync(int id)
    {
        if (id == Account.MainAccountId)
            throw new InvalidOperationException("Le compte courant ne peut pas être supprimé.");
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.Expenses.AnyAsync(e => e.AccountId == id))
            throw new InvalidOperationException("Ce compte a des dépenses : supprimez-les ou rattachez-les à un autre compte d'abord.");
        await db.Accounts.Where(a => a.Id == id).ExecuteDeleteAsync();
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
