using DashboardPerso.Data;
using DashboardPerso.Models;
using DashboardPerso.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DashboardPerso.Tests;

public sealed class BudgetServiceTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly TestDbFactory factory;
    private readonly BudgetService service;
    private readonly int auto, carburant, assurance;

    public BudgetServiceTests()
    {
        connection.Open();
        factory = new TestDbFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        using var db = factory.CreateDbContext();
        db.Database.Migrate();
        service = new BudgetService(factory);

        var a = new BudgetCategory { Name = "Automobile", AnnualBudget = 120 };
        service.SaveAsync(a).GetAwaiter().GetResult();
        var c = new BudgetCategory { Name = "Carburant", AnnualBudget = 1200, ParentId = a.Id };
        var s = new BudgetCategory { Name = "Assurance", AnnualBudget = 600, ParentId = a.Id };
        service.SaveAsync(c).GetAwaiter().GetResult();
        service.SaveAsync(s).GetAwaiter().GetResult();
        (auto, carburant, assurance) = (a.Id, c.Id, s.Id);

        db.Expenses.AddRange(
            new Expense { Date = new DateOnly(2026, 3, 10), Label = "Plein", Amount = 70, CategoryId = carburant },
            new Expense { Date = new DateOnly(2026, 3, 20), Label = "Lavage", Amount = 10, CategoryId = auto },
            new Expense { Date = new DateOnly(2026, 5, 1), Label = "Assurance", Amount = 50, CategoryId = assurance });
        db.CategoryRules.Add(new CategoryRule { Pattern = "TOTAL", CategoryId = carburant });
        // Un transfert, même catégorisé par erreur, ne compte pas dans le budget.
        db.Expenses.Add(new Expense { Date = new DateOnly(2026, 3, 1), Label = "Relevé carte", Amount = 999, CategoryId = carburant, IsTransfer = true });
        db.SaveChanges();
    }

    public void Dispose() => connection.Dispose();

    [Fact]
    public async Task Mois_le_prevu_vaut_annuel_sur_12_et_le_parent_cumule_ses_enfants()
    {
        var lines = await service.GetSummaryAsync(new BudgetPeriod(2026, 3));
        var line = lines.Single(l => l.Category.Id == auto);
        Assert.Equal(1920m, line.AnnualBudget);          // 120 non réparti + 1200 + 600
        Assert.Equal(160m, line.Planned);                // 1920 / 12
        Assert.Equal(80m, line.Spent);                   // 70 carburant + 10 directement sur Automobile
        Assert.Equal(10m, line.OwnSpent);
        Assert.Equal(100m, line.Children.Single(c => c.Category.Id == carburant).Planned);
    }

    [Fact]
    public async Task Annee_cumule_toutes_les_depenses_de_l_annee()
    {
        var line = (await service.GetSummaryAsync(new BudgetPeriod(2026, null))).Single(l => l.Category.Id == auto);
        Assert.Equal(1920m, line.Planned);
        Assert.Equal(130m, line.Spent);
    }

    [Fact]
    public async Task Les_categories_sont_ordonnees_parent_puis_enfants_avec_nom_complet()
    {
        var all = await service.GetCategoriesAsync();
        var i = all.FindIndex(c => c.Id == auto);
        Assert.Equal(["Automobile › Assurance", "Automobile › Carburant"], all.Skip(i + 1).Take(2).Select(c => c.FullName));
    }

    [Fact]
    public async Task Supprimer_reaffecte_les_depenses_et_les_regles()
    {
        await service.DeleteAsync(carburant, reassignTo: assurance);
        using var db = factory.CreateDbContext();
        Assert.Equal(assurance, db.Expenses.Single(e => e.Label == "Plein").CategoryId);
        Assert.Equal(assurance, db.CategoryRules.Single().CategoryId);
        Assert.False(db.BudgetCategories.Any(c => c.Id == carburant));
    }

    [Fact]
    public async Task Supprimer_sans_reaffectation_garde_les_depenses_sans_categorie()
    {
        await service.DeleteAsync(carburant);
        using var db = factory.CreateDbContext();
        Assert.Null(db.Expenses.Single(e => e.Label == "Plein").CategoryId);
        Assert.Empty(db.CategoryRules);
    }

    [Fact]
    public async Task Impossible_de_supprimer_une_categorie_qui_a_des_sous_categories() =>
        await Assert.ThrowsAsync<CategoryInUseException>(() => service.DeleteAsync(auto));

    [Fact]
    public async Task Un_seul_niveau_de_sous_categories()
    {
        var sub = new BudgetCategory { Name = "Diesel", ParentId = carburant };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(sub));

        var parent = await service.GetAsync(auto);
        parent!.Children = [];
        parent.ParentId = (await service.GetCategoriesAsync()).First(c => c.ParentId == null && c.Id != auto).Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync(parent));
    }

    [Fact]
    public async Task Renommer_une_categorie()
    {
        var c = await service.GetAsync(carburant);
        c!.Name = "Essence";
        c.Parent = null;
        await service.SaveAsync(c);
        Assert.Equal("Automobile › Essence", (await service.GetCategoriesAsync()).Single(x => x.Id == carburant).FullName);
    }

    [Fact]
    public void Prevu_a_date()
    {
        var p = new BudgetPeriod(2026, null);
        Assert.Equal(1m, p.ElapsedFraction(new DateOnly(2027, 1, 5)));
        Assert.Equal(0m, p.ElapsedFraction(new DateOnly(2025, 12, 31)));
        Assert.Equal(1m / 365m, p.ElapsedFraction(new DateOnly(2026, 1, 1)));
    }

    [Fact]
    public async Task Renommer_une_categorie_seule()
    {
        await service.RenameAsync(carburant, "  Carburant et péages ");
        Assert.Equal("Carburant et péages", (await service.GetAsync(carburant))!.Name);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RenameAsync(carburant, "   "));
    }

    [Fact]
    public async Task Supprimer_reaffecte_aussi_les_beneficiaires_et_l_utilisation_est_comptee()
    {
        using (var db = factory.CreateDbContext())
        {
            db.Payees.Add(new Payee { Name = "TOTAL", Key = "TOTAL", DefaultCategoryId = carburant });
            db.SaveChanges();
        }
        var usage = await service.GetAllUsageAsync();
        Assert.Equal((2, 1, 1), usage[carburant]);                // plein + transfert, règle TOTAL, bénéficiaire
        Assert.Equal((0, 0, 0), usage[1]);

        await service.DeleteAsync(carburant, reassignTo: assurance);
        using var check = factory.CreateDbContext();
        Assert.Equal(assurance, check.Payees.Single().DefaultCategoryId);
        Assert.Equal(assurance, check.CategoryRules.Single(r => r.Pattern == "TOTAL").CategoryId);
        Assert.Equal(assurance, check.Expenses.Single(e => e.Label == "Plein").CategoryId);
    }
}
