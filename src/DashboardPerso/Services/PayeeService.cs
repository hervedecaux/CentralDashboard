using DashboardPerso.Data;
using DashboardPerso.Models;
using DashboardPerso.Services.Import;
using Microsoft.EntityFrameworkCore;

namespace DashboardPerso.Services;

public record PayeeSummary(Payee Payee, int ExpenseCount, decimal Total, DateOnly? LastExpense, int Uncategorized);

public class PayeeService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<List<PayeeSummary>> GetSummariesAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var stats = await db.Expenses.Where(e => e.PayeeId != null)
            .GroupBy(e => e.PayeeId!.Value)
            .Select(g => new
            {
                g.Key,
                Count = g.Count(),
                Total = g.Sum(e => e.IsIncome ? -e.Amount : e.Amount),
                Last = g.Max(e => e.Date),
                Uncategorized = g.Count(e => e.CategoryId == null && !e.IsTransfer && !e.Splits.Any()),
            })
            .ToDictionaryAsync(x => x.Key);

        var payees = await db.Payees.AsNoTracking()
            .Include(p => p.DefaultCategory).ThenInclude(c => c!.Parent)
            .ToListAsync();
        return payees
            .Select(p => stats.TryGetValue(p.Id, out var s)
                ? new PayeeSummary(p, s.Count, s.Total, s.Last, s.Uncategorized)
                : new PayeeSummary(p, 0, 0, null, 0))
            .OrderByDescending(s => s.ExpenseCount).ThenBy(s => s.Payee.Name)
            .ToList();
    }

    public async Task<List<string>> GetNamesAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Payees.OrderBy(p => p.Name).Select(p => p.Name).ToListAsync();
    }

    public async Task<Payee?> FindByNameAsync(string name)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await FindAsync(db, name);
    }

    /// <summary>
    /// Retrouve un bénéficiaire par son libellé bancaire (clé) ou par son nom affiché (s'il a été renommé).
    /// </summary>
    private static async Task<Payee?> FindAsync(AppDbContext db, string name)
    {
        var key = TextNormalizer.Normalize(name);
        if (key.Length == 0) return null;
        return db.Payees.Local.FirstOrDefault(p => p.Key == key)
            ?? await db.Payees.FirstOrDefaultAsync(p => p.Key == key)
            ?? (await db.Payees.ToListAsync()).FirstOrDefault(p => TextNormalizer.Normalize(p.Name) == key);
    }

    /// <summary>Renomme et/ou fixe la catégorie par défaut (ou le transfert) d'un bénéficiaire.</summary>
    public async Task UpdateAsync(int id, string name, int? defaultCategoryId, bool defaultIsTransfer)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var payee = await db.Payees.FirstAsync(p => p.Id == id);
        name = name.Trim();
        if (name.Length > 0 && name != payee.Name)
        {
            // La clé (libellé bancaire) ne change pas : les prochains imports retrouvent le bénéficiaire renommé.
            var other = await FindAsync(db, name);
            if (other is not null && other.Id != id)
                throw new InvalidOperationException($"Un bénéficiaire « {name} » existe déjà.");
            payee.Name = name.Length <= 120 ? name : name[..120];
        }
        payee.DefaultIsTransfer = defaultIsTransfer;
        payee.DefaultCategoryId = defaultIsTransfer ? null : defaultCategoryId;
        await db.SaveChangesAsync();
    }

    /// <summary>Applique la catégorie par défaut aux dépenses de ce bénéficiaire qui n'en ont pas. Renvoie le nombre modifié.</summary>
    public async Task<int> ApplyDefaultToUncategorizedAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var payee = await db.Payees.AsNoTracking().FirstAsync(p => p.Id == id);
        var query = db.Expenses.Where(e => e.PayeeId == id && e.CategoryId == null && !e.IsTransfer && !e.Splits.Any());
        if (payee.DefaultIsTransfer)
            return await query.ExecuteUpdateAsync(s => s.SetProperty(e => e.IsTransfer, true));
        if (payee.DefaultCategoryId is { } cat)
        {
            // Une catégorie de recettes ne s'applique qu'aux recettes, et inversement.
            var income = await db.BudgetCategories.Where(c => c.Id == cat).Select(c => c.Kind).FirstAsync() == CategoryKind.Income;
            return await query.Where(e => e.IsIncome == income)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.CategoryId, cat));
        }
        return 0;
    }

    /// <summary>Bénéficiaire de ce nom, créé s'il n'existe pas (null pour un nom vide).</summary>
    internal static async Task<Payee?> GetOrCreateAsync(AppDbContext db, string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        var payee = await FindAsync(db, name);
        if (payee is not null) return payee;
        var key = TextNormalizer.Normalize(name);
        payee = new Payee { Name = name.Length <= 120 ? name : name[..120], Key = key.Length <= 120 ? key : key[..120] };
        db.Payees.Add(payee);
        return payee;
    }

    /// <summary>
    /// Rattache un bénéficiaire aux dépenses importées qui n'en ont pas encore
    /// (libellé bancaire complet conservé dans les notes : « Type | Bénéficiaire | … »).
    /// </summary>
    public async Task<int> BackfillImportedAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var expenses = await db.Expenses
            .Where(e => e.PayeeId == null && e.ImportKey != null && e.Notes != null)
            .ToListAsync();
        foreach (var e in expenses)
        {
            var parts = e.Notes!.Split(" | ");
            var description = parts.Length > 1 ? parts[1] : parts[0];
            e.Payee = await GetOrCreateAsync(db, PayeeExtractor.Extract(description));
        }
        await db.SaveChangesAsync();
        return expenses.Count;
    }
}
