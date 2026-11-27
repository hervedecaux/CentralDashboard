using DashboardPerso.Data;
using DashboardPerso.Models;
using Microsoft.EntityFrameworkCore;

namespace DashboardPerso.Services;

/// <summary>Une ligne de suivi : budget prévu vs dépensé sur un mois.</summary>
public record BudgetLine(BudgetCategory Category, decimal Spent)
{
    public decimal Remaining => Category.MonthlyBudget - Spent;
    public bool IsOver => Spent > Category.MonthlyBudget;
    public double Percent => Category.MonthlyBudget <= 0 ? (Spent > 0 ? 100 : 0)
        : (double)(Spent / Category.MonthlyBudget * 100);
}

public class BudgetService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<List<BudgetCategory>> GetCategoriesAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.BudgetCategories.AsNoTracking().OrderBy(c => c.Name).ToListAsync();
    }

    public async Task<BudgetCategory?> GetAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.BudgetCategories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
    }

    public async Task SaveAsync(BudgetCategory category)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (category.Id == 0) db.BudgetCategories.Add(category);
        else db.BudgetCategories.Update(category);
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        // Les dépenses liées sont conservées, sans catégorie.
        await db.Expenses.Where(e => e.CategoryId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.CategoryId, (int?)null));
        await db.BudgetCategories.Where(c => c.Id == id).ExecuteDeleteAsync();
    }

    /// <summary>Budget prévu vs dépensé pour chaque catégorie, sur le mois donné.</summary>
    public async Task<List<BudgetLine>> GetMonthSummaryAsync(int year, int month)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var from = new DateOnly(year, month, 1);
        var to = from.AddMonths(1);

        var spentByCategory = await db.Expenses
            .Where(e => e.Date >= from && e.Date < to && e.CategoryId != null)
            .GroupBy(e => e.CategoryId!.Value)
            .Select(g => new { CategoryId = g.Key, Total = g.Sum(e => e.Amount) })
            .ToDictionaryAsync(x => x.CategoryId, x => x.Total);

        var categories = await db.BudgetCategories.AsNoTracking().OrderBy(c => c.Name).ToListAsync();
        return categories
            .Select(c => new BudgetLine(c, spentByCategory.GetValueOrDefault(c.Id)))
            .ToList();
    }
}
