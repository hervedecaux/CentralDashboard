using DashboardPerso.Data;
using DashboardPerso.Models;
using DashboardPerso.Services.Import;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DashboardPerso.Tests;

public sealed class DeleteImportedTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly TestDbFactory factory;
    private readonly ImportService service;

    public DeleteImportedTests()
    {
        connection.Open();
        factory = new TestDbFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        using var db = factory.CreateDbContext();
        db.Database.Migrate();
        service = new ImportService(factory);
    }

    public void Dispose() => connection.Dispose();

    private async Task ImportSampleWithManualAndSplitAsync()
    {
        var preview = await service.PreviewStatementAsync(CsvTestData.Sample());
        await service.ImportAsync(preview.Rows, preview.BankBalance, preview.BankBalanceDate);
        using var db = factory.CreateDbContext();
        db.Expenses.Add(new Expense { Date = new DateOnly(2026, 3, 4), Label = "Saisie manuelle", Amount = 12m, CategoryId = 2 });
        db.CategoryRules.Add(new CategoryRule { Pattern = "ORANGE", CategoryId = 1 });
        var imported = db.Expenses.First(e => e.ImportKey != null && !e.IsIncome && !e.IsTransfer);
        db.ExpenseSplits.AddRange(
            new ExpenseSplit { ExpenseId = imported.Id, CategoryId = 1, Amount = imported.Amount - 1 },
            new ExpenseSplit { ExpenseId = imported.Id, CategoryId = 2, Amount = 1 });
        db.SaveChanges();
    }

    [Fact]
    public async Task Tout_effacer_garde_les_saisies_manuelles_et_les_reglages_et_permet_de_reimporter()
    {
        await ImportSampleWithManualAndSplitAsync();
        int payees, accounts;
        using (var db = factory.CreateDbContext())
        {
            payees = db.Payees.Count();
            accounts = db.Accounts.Count();
            Assert.NotNull(db.Accounts.Single(a => a.Id == Account.MainAccountId).BankBalance);
        }

        var summary = await service.GetImportedSummaryAsync(new ImportedFilter());
        Assert.Equal(10, summary.Count);
        Assert.Equal(10, await service.DeleteImportedAsync(new ImportedFilter()));

        using (var db = factory.CreateDbContext())
        {
            Assert.Equal("Saisie manuelle", db.Expenses.Single().Label);
            Assert.Empty(db.ExpenseSplits);
            Assert.Equal(payees, db.Payees.Count());
            Assert.Equal(accounts, db.Accounts.Count());
            Assert.Single(db.CategoryRules);
            Assert.Equal(5, db.BudgetCategories.Count());
            Assert.Null(db.Accounts.Single(a => a.Id == Account.MainAccountId).BankBalance);
        }

        // Les opérations peuvent être réimportées : plus aucune n'est vue comme « déjà importée ».
        var rows = await service.PreviewAsync(CsvTestData.Sample());
        Assert.DoesNotContain(rows, r => r.Status == ImportStatus.AlreadyImported);
    }

    [Fact]
    public async Task Effacer_une_carte_ou_une_periode_ne_touche_pas_le_reste()
    {
        await ImportSampleWithManualAndSplitAsync();
        int cardId;
        using (var db = factory.CreateDbContext())
            cardId = db.Accounts.Single(a => a.ImportId == "Carte ••5524").Id;

        Assert.Equal(3, await service.DeleteImportedAsync(new ImportedFilter(AccountId: cardId)));
        using (var db = factory.CreateDbContext())
        {
            Assert.Equal(8, db.Expenses.Count());                // 7 du compte + 1 saisie
            Assert.NotNull(db.Accounts.Single(a => a.Id == Account.MainAccountId).BankBalance);
        }

        var march = new ImportedFilter(From: new DateOnly(2026, 3, 1), To: new DateOnly(2026, 3, 31));
        var n = (await service.GetImportedSummaryAsync(march)).Count;
        Assert.True(n > 0);
        Assert.Equal(n, await service.DeleteImportedAsync(march));
        using var check = factory.CreateDbContext();
        Assert.Contains(check.Expenses, e => e.Label == "Saisie manuelle");
        Assert.DoesNotContain(check.Expenses, e => e.ImportKey != null && e.Date.Month == 3 && e.Date.Year == 2026);
    }
}
