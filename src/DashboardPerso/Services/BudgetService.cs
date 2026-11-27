using DashboardPerso.Data;
using DashboardPerso.Models;
using Microsoft.EntityFrameworkCore;

namespace DashboardPerso.Services;

/// <summary>Une ligne de suivi sur une période : prévu (au prorata du budget annuel) vs dépensé.</summary>
public class BudgetLine
{
    public required BudgetCategory Category { get; init; }

    /// <summary>Budget annuel total (propre + sous-catégories).</summary>
    public decimal AnnualBudget { get; init; }

    /// <summary>Montant prévu sur la période affichée (ex. annuel / 12 pour un mois).</summary>
    public decimal Planned { get; init; }

    /// <summary>Dépensé sur la période (sous-catégories comprises).</summary>
    public decimal Spent { get; init; }

    /// <summary>Sous-catégories (vide pour une sous-catégorie ou une catégorie sans enfant).</summary>
    public List<BudgetLine> Children { get; init; } = [];

    /// <summary>Dépenses affectées directement à la catégorie principale, hors sous-catégories.</summary>
    public decimal OwnSpent { get; init; }

    public decimal Remaining => Planned - Spent;
    public bool IsOver => Spent > Planned;
    public double Percent => Planned <= 0 ? (Spent > 0 ? 100 : 0) : (double)(Spent / Planned * 100);
}

/// <summary>Période de suivi : un mois, ou une année entière.</summary>
public readonly record struct BudgetPeriod(int Year, int? Month)
{
    public DateOnly From => new(Year, Month ?? 1, 1);
    public DateOnly To => Month is null ? From.AddYears(1) : From.AddMonths(1);

    /// <summary>Part du budget annuel correspondant à la période (1/12 pour un mois).</summary>
    public decimal Fraction => Month is null ? 1m : 1m / 12m;

    /// <summary>
    /// Part de l'année écoulée à la date donnée (pour l'année en cours) : sert au « prévu à date ».
    /// 1 pour une année passée, 0 pour une année future.
    /// </summary>
    public decimal ElapsedFraction(DateOnly today)
    {
        if (today.Year > Year) return 1m;
        if (today.Year < Year) return 0m;
        var days = DateTime.IsLeapYear(Year) ? 366m : 365m;
        return today.DayOfYear / days;
    }
}

public class CategoryInUseException(string message) : Exception(message);

public class BudgetService(IDbContextFactory<AppDbContext> dbFactory)
{
    /// <summary>
    /// Toutes les catégories, dans l'ordre d'affichage : chaque catégorie principale suivie de ses sous-catégories.
    /// Parent est chargé (pour <see cref="BudgetCategory.FullName"/>).
    /// </summary>
    public async Task<List<BudgetCategory>> GetCategoriesAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var all = await db.BudgetCategories.AsNoTracking().ToListAsync();
        return Order(all);
    }

    /// <summary>Catégories principales (pouvant recevoir des sous-catégories), sauf <paramref name="exceptId"/>.</summary>
    public async Task<List<BudgetCategory>> GetTopLevelAsync(int? exceptId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.BudgetCategories.AsNoTracking()
            .Where(c => c.ParentId == null && c.Id != exceptId)
            .OrderBy(c => c.Name)
            .ToListAsync();
    }

    public async Task<BudgetCategory?> GetAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.BudgetCategories.AsNoTracking()
            .Include(c => c.Parent)
            .Include(c => c.Children)
            .FirstOrDefaultAsync(c => c.Id == id);
    }

    /// <summary>Nombre de dépenses et de règles rattachées directement à la catégorie.</summary>
    public async Task<(int Expenses, int Rules)> GetUsageAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return (await db.Expenses.CountAsync(e => e.CategoryId == id),
                await db.CategoryRules.CountAsync(r => r.CategoryId == id));
    }

    public async Task SaveAsync(BudgetCategory category)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        if (category.ParentId is { } parentId)
        {
            if (parentId == category.Id)
                throw new InvalidOperationException("Une catégorie ne peut pas être sa propre catégorie parente.");
            if (await db.BudgetCategories.AnyAsync(c => c.Id == parentId && c.ParentId != null))
                throw new InvalidOperationException("Une sous-catégorie ne peut pas avoir elle-même de sous-catégories.");
            if (category.Id != 0 && await db.BudgetCategories.AnyAsync(c => c.ParentId == category.Id))
                throw new InvalidOperationException("Cette catégorie a des sous-catégories : elle doit rester une catégorie principale.");
        }

        var entity = new BudgetCategory
        {
            Id = category.Id,
            Name = category.Name.Trim(),
            AnnualBudget = category.AnnualBudget,
            Color = category.Color,
            ParentId = category.ParentId,
        };
        if (entity.Id == 0) db.BudgetCategories.Add(entity);
        else db.BudgetCategories.Update(entity);
        await db.SaveChangesAsync();
        category.Id = entity.Id;
    }

    /// <summary>
    /// Supprime une catégorie. Ses dépenses et ses règles sont réaffectées à <paramref name="reassignTo"/>
    /// (ou laissées sans catégorie / supprimées pour les règles, si null).
    /// Refusé si la catégorie a encore des sous-catégories.
    /// </summary>
    public async Task DeleteAsync(int id, int? reassignTo = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.BudgetCategories.AnyAsync(c => c.ParentId == id))
            throw new CategoryInUseException(
                "Cette catégorie a des sous-catégories : supprimez-les ou rattachez-les ailleurs d'abord.");
        if (reassignTo == id) reassignTo = null;

        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Expenses.Where(e => e.CategoryId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.CategoryId, reassignTo));
        if (reassignTo is null)
            await db.CategoryRules.Where(r => r.CategoryId == id).ExecuteDeleteAsync();
        else
            await db.CategoryRules.Where(r => r.CategoryId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.CategoryId, reassignTo));
        await db.BudgetCategories.Where(c => c.Id == id).ExecuteDeleteAsync();
        await tx.CommitAsync();
    }

    /// <summary>
    /// Suivi par catégorie principale sur la période, sous-catégories en lignes enfants.
    /// Le prévu d'un mois vaut budget annuel / 12.
    /// </summary>
    public async Task<List<BudgetLine>> GetSummaryAsync(BudgetPeriod period)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var from = period.From;
        var to = period.To;

        var spentByCategory = await db.Expenses
            .Where(e => e.Date >= from && e.Date < to && e.CategoryId != null)
            .GroupBy(e => e.CategoryId!.Value)
            .Select(g => new { CategoryId = g.Key, Total = g.Sum(e => e.Amount) })
            .ToDictionaryAsync(x => x.CategoryId, x => x.Total);

        var categories = Order(await db.BudgetCategories.AsNoTracking().ToListAsync());

        BudgetLine Leaf(BudgetCategory c) => new()
        {
            Category = c,
            AnnualBudget = c.AnnualBudget,
            Planned = Math.Round(c.AnnualBudget * period.Fraction, 2),
            Spent = spentByCategory.GetValueOrDefault(c.Id),
            OwnSpent = spentByCategory.GetValueOrDefault(c.Id),
        };

        return categories.Where(c => c.ParentId is null).Select(parent =>
        {
            var children = categories.Where(c => c.ParentId == parent.Id).Select(Leaf).ToList();
            var own = Leaf(parent);
            if (children.Count == 0) return own;
            var annual = parent.AnnualBudget + children.Sum(c => c.AnnualBudget);
            return new BudgetLine
            {
                Category = parent,
                AnnualBudget = annual,
                Planned = Math.Round(annual * period.Fraction, 2),
                Spent = own.Spent + children.Sum(c => c.Spent),
                OwnSpent = own.Spent,
                Children = children,
            };
        }).ToList();
    }

    /// <summary>Compatibilité : suivi d'un mois.</summary>
    public Task<List<BudgetLine>> GetMonthSummaryAsync(int year, int month) =>
        GetSummaryAsync(new BudgetPeriod(year, month));

    /// <summary>Ordonne : catégories principales par nom, chacune suivie de ses sous-catégories, et relie Parent.</summary>
    private static List<BudgetCategory> Order(List<BudgetCategory> all)
    {
        var byId = all.ToDictionary(c => c.Id);
        foreach (var c in all)
            c.Parent = c.ParentId is { } p && byId.TryGetValue(p, out var parent) ? parent : null;

        var result = new List<BudgetCategory>(all.Count);
        foreach (var top in all.Where(c => c.ParentId is null).OrderBy(c => c.Name, StringComparer.CurrentCulture))
        {
            result.Add(top);
            result.AddRange(all.Where(c => c.ParentId == top.Id).OrderBy(c => c.Name, StringComparer.CurrentCulture));
        }
        return result;
    }
}
