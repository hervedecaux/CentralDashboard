using DashboardPerso.Data;
using DashboardPerso.Models;
using Microsoft.EntityFrameworkCore;

namespace DashboardPerso.Services;

public class ExpenseService(IDbContextFactory<AppDbContext> dbFactory)
{
    /// <summary>Dépenses d'un mois, les plus récentes d'abord.</summary>
    public async Task<List<Expense>> GetByMonthAsync(int year, int month)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var from = new DateOnly(year, month, 1);
        var to = from.AddMonths(1);
        return await db.Expenses.AsNoTracking()
            .Include(e => e.Category).ThenInclude(c => c!.Parent)
            .Include(e => e.Account)
            .Include(e => e.Payee)
            .Include(e => e.Splits).ThenInclude(x => x.Category)
            .Where(e => e.Date >= from && e.Date < to)
            .OrderByDescending(e => e.Date).ThenByDescending(e => e.Id)
            .ToListAsync();
    }

    public async Task<List<Expense>> GetLatestAsync(int count)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Expenses.AsNoTracking()
            .Include(e => e.Category).ThenInclude(c => c!.Parent)
            .Include(e => e.Account)
            .Include(e => e.Payee)
            .Include(e => e.Splits).ThenInclude(x => x.Category)
            .OrderByDescending(e => e.Date).ThenByDescending(e => e.Id)
            .Take(count)
            .ToListAsync();
    }

    public async Task<Expense?> GetAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Expenses.AsNoTracking()
            .Include(e => e.Payee)
            .Include(e => e.Splits)
            .FirstOrDefaultAsync(e => e.Id == id);
    }

    /// <summary>
    /// Enregistre une dépense. Si <paramref name="payeeName"/> est fourni, le bénéficiaire est retrouvé
    /// (ou créé) et sa catégorie par défaut s'applique quand la dépense n'en a pas.
    /// </summary>
    public async Task SaveAsync(Expense expense, string? payeeName = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        // Ventilation : au moins deux parts, dont la somme égale le montant.
        var splits = expense.Splits.Where(x => x.Amount != 0).ToList();
        if (splits.Count >= 2 && !expense.IsTransfer)
        {
            if (splits.Sum(x => x.Amount) != expense.Amount)
                throw new InvalidOperationException(
                    $"La ventilation ({splits.Sum(x => x.Amount):C}) ne correspond pas au montant ({expense.Amount:C}).");
            expense.CategoryId = null;
        }
        else
        {
            if (splits.Count == 1 && expense.CategoryId is null) expense.CategoryId = splits[0].CategoryId;
            splits = [];
        }

        expense.Category = null; // on ne persiste que les clés étrangères
        expense.Account = null;
        expense.Payee = null;
        expense.Splits = [];
        if (expense.AccountId == 0) expense.AccountId = Account.MainAccountId;

        if (payeeName is not null)
        {
            var payee = await PayeeService.GetOrCreateAsync(db, payeeName);
            if (payee is null) expense.PayeeId = null;
            else
            {
                if (payee.Id == 0) await db.SaveChangesAsync();
                expense.PayeeId = payee.Id;
                if (expense.CategoryId is null && splits.Count == 0 && !expense.IsTransfer)
                {
                    if (payee.DefaultIsTransfer) expense.IsTransfer = true;
                    else if (payee.DefaultCategoryId is { } def && await KindOfAsync(db, def) == Kind(expense))
                        expense.CategoryId = def;
                }
            }
        }
        if (expense.IsTransfer)
        {
            expense.CategoryId = null;
            splits = [];
        }

        // Une dépense va dans une catégorie de dépenses, une recette dans une catégorie de recettes.
        var used = splits.Select(x => x.CategoryId).Append(expense.CategoryId).OfType<int>().Distinct().ToList();
        if (used.Count > 0)
        {
            var wrong = await db.BudgetCategories.Where(c => used.Contains(c.Id) && c.Kind != Kind(expense))
                .Select(c => c.Name).FirstOrDefaultAsync();
            if (wrong is not null)
                throw new InvalidOperationException(expense.IsIncome
                    ? $"« {wrong} » est une catégorie de dépenses : choisissez une catégorie de recettes."
                    : $"« {wrong} » est une catégorie de recettes : choisissez une catégorie de dépenses.");
        }

        await using var tx = await db.Database.BeginTransactionAsync();
        if (expense.Id == 0) db.Expenses.Add(expense);
        else
        {
            db.Expenses.Update(expense);
            await db.ExpenseSplits.Where(x => x.ExpenseId == expense.Id).ExecuteDeleteAsync();
        }
        await db.SaveChangesAsync();
        foreach (var part in splits)
            db.ExpenseSplits.Add(new ExpenseSplit { ExpenseId = expense.Id, CategoryId = part.CategoryId, Amount = part.Amount });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        expense.Splits = splits;
    }

    private static CategoryKind Kind(Expense e) => e.IsIncome ? CategoryKind.Income : CategoryKind.Expense;

    private static Task<CategoryKind> KindOfAsync(AppDbContext db, int categoryId) =>
        db.BudgetCategories.Where(c => c.Id == categoryId).Select(c => c.Kind).FirstOrDefaultAsync();

    public async Task DeleteAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Expenses.Where(e => e.Id == id).ExecuteDeleteAsync();
    }

    public async Task<decimal> GetMonthTotalAsync(int year, int month)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var from = new DateOnly(year, month, 1);
        var to = from.AddMonths(1);
        return await db.Expenses.Where(e => e.Date >= from && e.Date < to && !e.IsTransfer && !e.IsIncome).SumAsync(e => e.Amount);
    }

}
