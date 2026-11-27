using DashboardPerso.Data;
using DashboardPerso.Models;
using DashboardPerso.Services;
using DashboardPerso.Services.Import;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DashboardPerso.Tests;

public sealed class IncomeTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly TestDbFactory factory;
    private readonly ExpenseService expenses;
    private readonly BudgetService budget;
    private readonly BudgetCategory salaire;
    private readonly BudgetCategory primes;
    private readonly BudgetCategory remboursements;

    public IncomeTests()
    {
        connection.Open();
        factory = new TestDbFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        using var db = factory.CreateDbContext();
        db.Database.Migrate();
        expenses = new ExpenseService(factory);
        budget = new BudgetService(factory);

        salaire = new BudgetCategory { Name = "Salaire", Kind = CategoryKind.Income, AnnualBudget = 24_000m };
        remboursements = new BudgetCategory { Name = "Remboursements", Kind = CategoryKind.Income, AnnualBudget = 600m };
        budget.SaveAsync(salaire).GetAwaiter().GetResult();
        budget.SaveAsync(remboursements).GetAwaiter().GetResult();
        // Sous-catégorie : hérite du type de sa catégorie principale, même si on demande « dépense ».
        primes = new BudgetCategory { Name = "Primes", ParentId = salaire.Id, AnnualBudget = 1_200m };
        budget.SaveAsync(primes).GetAwaiter().GetResult();
    }

    public void Dispose() => connection.Dispose();

    private Task Income(int day, decimal amount, int? category, int month = 3) => expenses.SaveAsync(new Expense
    {
        Date = new DateOnly(2026, month, day), Label = "Recette", Amount = amount, IsIncome = true, CategoryId = category,
    });

    [Fact]
    public void Une_sous_categorie_prend_le_type_de_sa_categorie_principale() =>
        Assert.Equal(CategoryKind.Income, primes.Kind);

    [Fact]
    public async Task Le_budget_suit_les_recettes_prevues_et_encaissees_et_le_solde()
    {
        await Income(1, 2000m, salaire.Id);
        await Income(15, 300m, primes.Id);
        await Income(20, 45m, null);                                          // recette non catégorisée
        await expenses.SaveAsync(new Expense { Date = new DateOnly(2026, 3, 5), Label = "Courses", Amount = 500m, CategoryId = 2 });
        await expenses.SaveAsync(new Expense { Date = new DateOnly(2026, 3, 6), Label = "Divers", Amount = 20m });
        await expenses.SaveAsync(new Expense { Date = new DateOnly(2026, 3, 7), Label = "Épargne", Amount = 999m, IsIncome = true, IsTransfer = true });

        var o = await budget.GetOverviewAsync(new BudgetPeriod(2026, 3));

        var line = o.Incomes.Single(l => l.Category.Id == salaire.Id);
        Assert.Equal(25_200m, line.AnnualBudget);                             // 24 000 + primes 1 200
        Assert.Equal(2_100m, line.Planned);                                   // ÷ 12
        Assert.Equal(2_300m, line.Spent);
        Assert.Equal(300m, line.Children.Single().Spent);
        Assert.Equal(45m, o.UncategorizedIncome);
        Assert.Equal(2_345m, o.ActualIncome);                                 // transfert exclu
        Assert.Equal(520m, o.ActualExpenses);
        Assert.Equal(20m, o.UncategorizedExpenses);
        Assert.Equal(1_825m, o.ActualBalance);
        Assert.DoesNotContain(o.Expenses, l => l.Category.IsIncome);
        Assert.Equal(500m, o.Expenses.Single(l => l.Category.Id == 2).Spent);
    }

    [Fact]
    public async Task Une_recette_peut_etre_ventilee_sur_des_categories_de_recettes()
    {
        await expenses.SaveAsync(new Expense
        {
            Date = new DateOnly(2026, 3, 1), Label = "Virement employeur", Amount = 2500m, IsIncome = true,
            Splits = [new() { CategoryId = salaire.Id, Amount = 2000m }, new() { CategoryId = primes.Id, Amount = 500m }],
        });
        var lines = await budget.GetSummaryAsync(new BudgetPeriod(2026, 3), CategoryKind.Income);
        Assert.Equal(2500m, lines.Single(l => l.Category.Id == salaire.Id).Spent);

        await Assert.ThrowsAsync<InvalidOperationException>(() => expenses.SaveAsync(new Expense
        {
            Date = new DateOnly(2026, 3, 2), Label = "Mélange", Amount = 100m, IsIncome = true,
            Splits = [new() { CategoryId = salaire.Id, Amount = 50m }, new() { CategoryId = 2, Amount = 50m }],
        }));
    }

    [Fact]
    public async Task Recettes_mois_par_mois_par_categorie_principale()
    {
        await Income(1, 2000m, salaire.Id, month: 1);
        await Income(1, 2100m, salaire.Id, month: 2);
        await Income(10, 400m, primes.Id, month: 2);
        await Income(3, 30m, null, month: 2);

        var monthly = await budget.GetMonthlyAsync(2026, CategoryKind.Income);
        var sal = monthly.Single(m => m.Category?.Id == salaire.Id);
        Assert.Equal([2000m, 2500m], sal.Months.Take(2));
        Assert.Equal(4500m, sal.Total);
        Assert.Equal(30m, monthly.Single(m => m.Category is null).Months[1]);
        Assert.Equal(0m, monthly.Single(m => m.Category?.Id == remboursements.Id).Total);
    }

    [Fact]
    public async Task Le_type_d_une_categorie_utilisee_ne_peut_plus_changer()
    {
        await Income(1, 2000m, salaire.Id);
        var edit = await budget.GetAsync(salaire.Id);
        edit!.Children = [];
        edit.Kind = CategoryKind.Expense;
        await Assert.ThrowsAsync<InvalidOperationException>(() => budget.SaveAsync(edit));

        // Non utilisée : le changement passe et s'applique aux sous-catégories.
        var r = (await budget.GetAsync(remboursements.Id))!;
        r.Children = [];
        r.Kind = CategoryKind.Expense;
        await budget.SaveAsync(r);
        Assert.Equal(CategoryKind.Expense, (await budget.GetAsync(remboursements.Id))!.Kind);
    }

    [Fact]
    public async Task On_ne_reaffecte_pas_une_categorie_de_recettes_vers_une_categorie_de_depenses() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => budget.DeleteAsync(remboursements.Id, reassignTo: 2));

    [Fact]
    public async Task Import_les_regles_et_le_beneficiaire_categorisent_les_recettes_selon_leur_type()
    {
        using (var db = factory.CreateDbContext())
        {
            // « EXEMPLE » correspond à la fois au salaire et à des dépenses : chaque opération prend la règle de son type.
            db.CategoryRules.Add(new CategoryRule { Pattern = "EXEMPLE", CategoryId = 2 });
            db.CategoryRules.Add(new CategoryRule { Pattern = "SALAIRE", CategoryId = salaire.Id });
            db.Payees.Add(new Payee { Name = "BOUTIQUE EXEMPLE", Key = "BOUTIQUE EXEMPLE", DefaultCategoryId = 2 });
            db.SaveChanges();
        }
        var service = new ImportService(factory);
        var rows = await service.PreviewAsync(CsvTestData.Sample());

        var pay = rows.Single(r => r.Operation.Credit == 2100m);
        Assert.Equal(salaire.Id, pay.CategoryId);
        Assert.Equal("SALAIRE", pay.MatchedRule);
        // Le remboursement carte vient d'un bénéficiaire dont le défaut est une catégorie de dépenses : ignoré.
        var refund = rows.Single(r => r.Operation.Credit == 10m);
        Assert.NotEqual(2, refund.CategoryId);
        Assert.False(refund.FromPayeeDefault);
        Assert.All(rows.Where(r => !r.IsIncome && r.CategoryId is not null), r => Assert.Equal(2, r.CategoryId));

        // Une catégorie qui ne correspond pas au sens de l'opération n'est pas enregistrée.
        refund.CategoryId = 2;
        await service.ImportAsync(rows);
        using var check = factory.CreateDbContext();
        Assert.Equal(salaire.Id, check.Expenses.Single(e => e.Amount == 2100m).CategoryId);
        Assert.Null(check.Expenses.Single(e => e.Amount == 10m && e.IsIncome).CategoryId);
    }

    [Fact]
    public async Task Le_defaut_du_beneficiaire_s_applique_aux_recettes_saisies()
    {
        using (var db = factory.CreateDbContext())
        {
            db.Payees.Add(new Payee { Name = "MUTUELLE", Key = "MUTUELLE", DefaultCategoryId = remboursements.Id });
            db.SaveChanges();
        }
        var e = new Expense { Date = new DateOnly(2026, 3, 1), Label = "Remboursement", Amount = 25m, IsIncome = true };
        await expenses.SaveAsync(e, "Mutuelle");
        Assert.Equal(remboursements.Id, e.CategoryId);

        // Une dépense chez ce bénéficiaire ne reçoit pas la catégorie de recettes.
        var d = new Expense { Date = new DateOnly(2026, 3, 2), Label = "Cotisation", Amount = 60m };
        await expenses.SaveAsync(d, "Mutuelle");
        Assert.Null(d.CategoryId);
    }
}
