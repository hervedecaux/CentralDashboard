using DashboardPerso.Data;
using DashboardPerso.Models;
using DashboardPerso.Services;
using DashboardPerso.Services.Import;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DashboardPerso.Tests;

public sealed class SplitAndBalanceTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly TestDbFactory factory;
    private readonly ExpenseService expenses;

    public SplitAndBalanceTests()
    {
        connection.Open();
        factory = new TestDbFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        using var db = factory.CreateDbContext();
        db.Database.Migrate();
        expenses = new ExpenseService(factory);
    }

    public void Dispose() => connection.Dispose();

    private static Expense Split(decimal amount, params (int Category, decimal Amount)[] parts) => new()
    {
        Date = new DateOnly(2026, 3, 10), Label = "Hypermarché", Amount = amount,
        Splits = parts.Select(p => new ExpenseSplit { CategoryId = p.Category, Amount = p.Amount }).ToList(),
    };

    [Fact]
    public async Task Une_depense_ventilee_compte_chaque_part_dans_son_budget()
    {
        await expenses.SaveAsync(Split(100m, (2, 80m), (1, 20m)));   // Alimentation 80, Logement 20
        await expenses.SaveAsync(new Expense { Date = new DateOnly(2026, 3, 11), Label = "Pain", Amount = 5m, CategoryId = 2 });

        var lines = await new BudgetService(factory).GetSummaryAsync(new BudgetPeriod(2026, 3));
        Assert.Equal(85m, lines.Single(l => l.Category.Id == 2).Spent);
        Assert.Equal(20m, lines.Single(l => l.Category.Id == 1).Spent);
        Assert.Equal(105m, await expenses.GetMonthTotalAsync(2026, 3));

        using var db = factory.CreateDbContext();
        Assert.Null(db.Expenses.Single(e => e.Label == "Hypermarché").CategoryId);
    }

    [Fact]
    public async Task La_ventilation_doit_egaler_le_montant() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => expenses.SaveAsync(Split(100m, (2, 80m), (1, 10m))));

    [Fact]
    public async Task Modifier_une_ventilation_remplace_les_parts_et_une_seule_part_redevient_une_categorie()
    {
        var e = Split(100m, (2, 80m), (1, 20m));
        await expenses.SaveAsync(e);
        var edit = await expenses.GetAsync(e.Id);
        edit!.Splits = [new ExpenseSplit { CategoryId = 3, Amount = 100m }];
        await expenses.SaveAsync(edit);

        using var db = factory.CreateDbContext();
        Assert.Empty(db.ExpenseSplits);
        Assert.Equal(3, db.Expenses.Single().CategoryId);
    }

    [Fact]
    public async Task Supprimer_une_categorie_reaffecte_aussi_les_parts()
    {
        await expenses.SaveAsync(Split(100m, (2, 80m), (4, 20m)));
        await new BudgetService(factory).DeleteAsync(4, reassignTo: 1);
        using var db = factory.CreateDbContext();
        Assert.Equal([1, 2], db.ExpenseSplits.Select(x => x.CategoryId!.Value).OrderBy(x => x).ToList());
    }

    [Fact]
    public async Task Une_recette_est_hors_budget_et_sans_categorie()
    {
        await expenses.SaveAsync(new Expense { Date = new DateOnly(2026, 3, 1), Label = "Salaire", Amount = 2000m, IsIncome = true, CategoryId = 2 });
        Assert.Equal(0m, await expenses.GetMonthTotalAsync(2026, 3));
        using var db = factory.CreateDbContext();
        Assert.Null(db.Expenses.Single().CategoryId);
    }

    [Fact]
    public async Task Solde_theorique_depuis_le_solde_initial_compare_au_solde_banque()
    {
        var accounts = new AccountService(factory);
        Assert.Null(await accounts.GetBalanceAsync());

        await accounts.SetInitialBalanceAsync(Account.MainAccountId, 1000m, new DateOnly(2026, 2, 28));
        using (var db = factory.CreateDbContext())
        {
            db.Expenses.AddRange(
                new Expense { Date = new DateOnly(2026, 2, 28), Label = "Avant (déjà dans le solde)", Amount = 999m },
                new Expense { Date = new DateOnly(2026, 3, 1), Label = "Salaire", Amount = 2000m, IsIncome = true },
                new Expense { Date = new DateOnly(2026, 3, 5), Label = "Loyer", Amount = 800m },
                new Expense { Date = new DateOnly(2026, 3, 6), Label = "Relevé carte", Amount = 150m, IsTransfer = true },
                new Expense { Date = new DateOnly(2026, 3, 20), Label = "Courses", Amount = 50m });
            // Une dépense d'une autre carte ne touche pas le compte courant.
            var visa = new Account { Label = "Visa", Kind = AccountKind.CreditCard };
            db.Accounts.Add(visa);
            db.Expenses.Add(new Expense { Date = new DateOnly(2026, 3, 7), Label = "Visa", Amount = 70m, Account = visa });
            var main = db.Accounts.Single(a => a.Id == Account.MainAccountId);
            main.BankBalance = 2040m;
            main.BankBalanceDate = new DateOnly(2026, 3, 10);
            db.SaveChanges();
        }

        var b = (await accounts.GetBalanceAsync(at: new DateOnly(2026, 3, 31)))!;
        Assert.Equal(2000m, b.Theoretical);                 // 1000 + 2000 - 800 - 150 - 50
        Assert.Equal(2050m, b.TheoreticalAtBankDate);       // au 10/03 : sans les courses du 20
        Assert.Equal(-10m, b.Difference);                   // la banque indique 2040
    }
}
