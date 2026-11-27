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
            .OrderByDescending(e => e.Date).ThenByDescending(e => e.Id)
            .Take(count)
            .ToListAsync();
    }

    public async Task<Expense?> GetAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Expenses.AsNoTracking().Include(e => e.Payee).FirstOrDefaultAsync(e => e.Id == id);
    }

    /// <summary>
    /// Enregistre une dépense. Si <paramref name="payeeName"/> est fourni, le bénéficiaire est retrouvé
    /// (ou créé) et sa catégorie par défaut s'applique quand la dépense n'en a pas.
    /// </summary>
    public async Task SaveAsync(Expense expense, string? payeeName = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        expense.Category = null; // on ne persiste que les clés étrangères
        expense.Account = null;
        expense.Payee = null;
        if (expense.AccountId == 0) expense.AccountId = Account.MainAccountId;

        if (payeeName is not null)
        {
            var payee = await PayeeService.GetOrCreateAsync(db, payeeName);
            if (payee is null) expense.PayeeId = null;
            else
            {
                if (payee.Id == 0) await db.SaveChangesAsync();
                expense.PayeeId = payee.Id;
                if (expense.CategoryId is null && !expense.IsTransfer)
                {
                    expense.IsTransfer = payee.DefaultIsTransfer;
                    expense.CategoryId = payee.DefaultCategoryId;
                }
            }
        }
        if (expense.IsTransfer) expense.CategoryId = null;

        if (expense.Id == 0) db.Expenses.Add(expense);
        else db.Expenses.Update(expense);
        await db.SaveChangesAsync();
    }

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
        return await db.Expenses.Where(e => e.Date >= from && e.Date < to && !e.IsTransfer).SumAsync(e => e.Amount);
    }

}
