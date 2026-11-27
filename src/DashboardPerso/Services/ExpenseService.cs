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
            .Where(e => e.Date >= from && e.Date < to)
            .OrderByDescending(e => e.Date).ThenByDescending(e => e.Id)
            .ToListAsync();
    }

    public async Task<List<Expense>> GetLatestAsync(int count)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Expenses.AsNoTracking()
            .Include(e => e.Category).ThenInclude(c => c!.Parent)
            .OrderByDescending(e => e.Date).ThenByDescending(e => e.Id)
            .Take(count)
            .ToListAsync();
    }

    public async Task<Expense?> GetAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Expenses.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id);
    }

    public async Task SaveAsync(Expense expense)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        expense.Category = null; // on ne persiste que la clé étrangère
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
        return await db.Expenses.Where(e => e.Date >= from && e.Date < to).SumAsync(e => e.Amount);
    }
}
