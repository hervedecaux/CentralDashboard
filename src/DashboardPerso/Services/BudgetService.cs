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

/// <summary>
/// Vue d'ensemble d'une période : dépenses et recettes par catégorie, montants non catégorisés et solde.
/// </summary>
public record BudgetOverview(
    List<BudgetLine> Expenses,
    List<BudgetLine> Incomes,
    decimal UncategorizedExpenses,
    decimal UncategorizedIncome)
{
    public decimal PlannedExpenses => Expenses.Sum(l => l.Planned);
    public decimal PlannedIncome => Incomes.Sum(l => l.Planned);
    public decimal AnnualExpenses => Expenses.Sum(l => l.AnnualBudget);
    public decimal AnnualIncome => Incomes.Sum(l => l.AnnualBudget);

    /// <summary>Total dépensé, non catégorisé compris (hors transferts).</summary>
    public decimal ActualExpenses => Expenses.Sum(l => l.Spent) + UncategorizedExpenses;

    /// <summary>Total des recettes, non catégorisées comprises (hors transferts).</summary>
    public decimal ActualIncome => Incomes.Sum(l => l.Spent) + UncategorizedIncome;

    /// <summary>Recettes − dépenses prévues sur la période.</summary>
    public decimal PlannedBalance => PlannedIncome - PlannedExpenses;

    /// <summary>Recettes − dépenses réelles sur la période.</summary>
    public decimal ActualBalance => ActualIncome - ActualExpenses;
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

/// <summary>Montants d'une catégorie principale (sous-catégories comprises) mois par mois ; Category null = non catégorisé.</summary>
public record MonthlyLine(BudgetCategory? Category, decimal[] Months)
{
    public decimal Total => Months.Sum();
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
    public async Task<List<BudgetCategory>> GetTopLevelAsync(int? exceptId = null, CategoryKind? kind = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.BudgetCategories.AsNoTracking()
            .Where(c => c.ParentId == null && c.Id != exceptId && (kind == null || c.Kind == kind))
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
        return (await db.Expenses.CountAsync(e => e.CategoryId == id || e.Splits.Any(s => s.CategoryId == id)),
                await db.CategoryRules.CountAsync(r => r.CategoryId == id));
    }

    public async Task SaveAsync(BudgetCategory category)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        if (category.ParentId is { } parentId)
        {
            if (parentId == category.Id)
                throw new InvalidOperationException("Une catégorie ne peut pas être sa propre catégorie parente.");
            var parent = await db.BudgetCategories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == parentId)
                ?? throw new InvalidOperationException("Catégorie principale introuvable.");
            if (parent.ParentId != null)
                throw new InvalidOperationException("Une sous-catégorie ne peut pas avoir elle-même de sous-catégories.");
            if (category.Id != 0 && await db.BudgetCategories.AnyAsync(c => c.ParentId == category.Id))
                throw new InvalidOperationException("Cette catégorie a des sous-catégories : elle doit rester une catégorie principale.");
            // Une sous-catégorie a le type (dépense / recette) de sa catégorie principale.
            category.Kind = parent.Kind;
        }

        var kindChanged = false;
        if (category.Id != 0)
        {
            var previous = await db.BudgetCategories.AsNoTracking().Where(c => c.Id == category.Id)
                .Select(c => (CategoryKind?)c.Kind).FirstOrDefaultAsync();
            kindChanged = previous is not null && previous != category.Kind;
            if (kindChanged)
            {
                var ids = await db.BudgetCategories.Where(c => c.Id == category.Id || c.ParentId == category.Id)
                    .Select(c => c.Id).ToListAsync();
                var used = await db.Expenses.CountAsync(e => (e.CategoryId != null && ids.Contains(e.CategoryId.Value))
                                                             || e.Splits.Any(x => x.CategoryId != null && ids.Contains(x.CategoryId.Value)));
                if (used > 0)
                    throw new InvalidOperationException(
                        $"Cette catégorie est déjà utilisée par {used} opération(s) : elle ne peut plus passer de "
                        + (category.Kind == CategoryKind.Income ? "dépense à recette." : "recette à dépense."));
            }
        }

        var entity = new BudgetCategory
        {
            Id = category.Id,
            Name = category.Name.Trim(),
            AnnualBudget = category.AnnualBudget,
            Color = category.Color,
            ParentId = category.ParentId,
            Kind = category.Kind,
        };
        if (entity.Id == 0) db.BudgetCategories.Add(entity);
        else db.BudgetCategories.Update(entity);
        await db.SaveChangesAsync();
        if (kindChanged)
            await db.BudgetCategories.Where(c => c.ParentId == entity.Id)
                .ExecuteUpdateAsync(x => x.SetProperty(c => c.Kind, entity.Kind));
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
        if (reassignTo is { } target)
        {
            var kinds = await db.BudgetCategories.Where(c => c.Id == id || c.Id == target).Select(c => c.Kind).Distinct().CountAsync();
            if (kinds > 1)
                throw new InvalidOperationException("Les opérations ne peuvent être réaffectées qu'à une catégorie du même type (dépense ou recette).");
        }

        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Expenses.Where(e => e.CategoryId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.CategoryId, reassignTo));
        await db.ExpenseSplits.Where(x => x.CategoryId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CategoryId, reassignTo));
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
    /// Le prévu d'un mois vaut budget annuel / 12. Pour les recettes, « Spent » est le montant encaissé.
    /// </summary>
    public async Task<List<BudgetLine>> GetSummaryAsync(BudgetPeriod period, CategoryKind kind = CategoryKind.Expense)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await SummaryAsync(db, period, kind);
    }

    /// <summary>Dépenses, recettes et solde de la période.</summary>
    public async Task<BudgetOverview> GetOverviewAsync(BudgetPeriod period)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var expenses = await SummaryAsync(db, period, CategoryKind.Expense);
        var incomes = await SummaryAsync(db, period, CategoryKind.Income);
        var from = period.From;
        var to = period.To;
        var totals = await db.Expenses
            .Where(e => e.Date >= from && e.Date < to && !e.IsTransfer)
            .GroupBy(e => e.IsIncome)
            .Select(g => new { IsIncome = g.Key, Total = g.Sum(e => e.Amount) })
            .ToListAsync();
        var totalExpenses = totals.Where(t => !t.IsIncome).Sum(t => t.Total);
        var totalIncome = totals.Where(t => t.IsIncome).Sum(t => t.Total);
        return new BudgetOverview(expenses, incomes,
            totalExpenses - expenses.Sum(l => l.Spent),
            totalIncome - incomes.Sum(l => l.Spent));
    }

    /// <summary>
    /// Montants mois par mois sur une année, par catégorie principale du type demandé
    /// (utile pour suivre des recettes variables). La dernière ligne, si besoin, regroupe le non catégorisé.
    /// </summary>
    public async Task<List<MonthlyLine>> GetMonthlyAsync(int year, CategoryKind kind)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var from = new DateOnly(year, 1, 1);
        var to = from.AddYears(1);
        var income = kind == CategoryKind.Income;

        var direct = await db.Expenses
            .Where(e => e.Date >= from && e.Date < to && !e.IsTransfer && e.IsIncome == income && !e.Splits.Any())
            .Select(e => new { e.Date, e.CategoryId, e.Amount })
            .ToListAsync();
        var splits = await db.ExpenseSplits
            .Where(x => x.Expense!.Date >= from && x.Expense.Date < to && !x.Expense.IsTransfer && x.Expense.IsIncome == income)
            .Select(x => new { x.Expense!.Date, x.CategoryId, x.Amount })
            .ToListAsync();

        var categories = Order(await db.BudgetCategories.AsNoTracking().ToListAsync());
        var byId = categories.ToDictionary(c => c.Id);
        var tops = categories.Where(c => c.ParentId is null && c.Kind == kind).ToList();
        var rows = tops.ToDictionary(c => c.Id, _ => new decimal[12]);
        var uncategorized = new decimal[12];

        foreach (var op in direct.Concat(splits))
        {
            var cat = op.CategoryId is { } id ? byId.GetValueOrDefault(id) : null;
            var topId = cat?.ParentId ?? cat?.Id;
            var target = topId is { } t && rows.TryGetValue(t, out var r) ? r : uncategorized;
            target[op.Date.Month - 1] += op.Amount;
        }

        var result = tops.Select(c => new MonthlyLine(c, rows[c.Id])).ToList();
        if (uncategorized.Any(v => v != 0)) result.Add(new MonthlyLine(null, uncategorized));
        return result;
    }

    private static async Task<List<BudgetLine>> SummaryAsync(AppDbContext db, BudgetPeriod period, CategoryKind kind)
    {
        var from = period.From;
        var to = period.To;
        var income = kind == CategoryKind.Income;

        // Opérations non ventilées + parts des opérations ventilées (hors transferts),
        // du même sens que les catégories suivies et rattachées à une catégorie de ce type.
        var direct = await db.Expenses
            .Where(e => e.Date >= from && e.Date < to && e.CategoryId != null && !e.IsTransfer
                        && e.IsIncome == income && e.Category!.Kind == kind)
            .GroupBy(e => e.CategoryId!.Value)
            .Select(g => new { CategoryId = g.Key, Total = g.Sum(e => e.Amount) })
            .ToListAsync();
        var splits = await db.ExpenseSplits
            .Where(s => s.CategoryId != null && s.Expense!.Date >= from && s.Expense.Date < to
                        && !s.Expense.IsTransfer && s.Expense.IsIncome == income && s.Category!.Kind == kind)
            .GroupBy(s => s.CategoryId!.Value)
            .Select(g => new { CategoryId = g.Key, Total = g.Sum(s => s.Amount) })
            .ToListAsync();
        var spentByCategory = direct.Concat(splits)
            .GroupBy(x => x.CategoryId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Total));

        var categories = Order(await db.BudgetCategories.AsNoTracking().Where(c => c.Kind == kind).ToListAsync());

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
