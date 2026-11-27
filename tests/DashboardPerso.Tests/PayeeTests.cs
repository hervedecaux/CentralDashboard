using DashboardPerso.Data;
using DashboardPerso.Models;
using DashboardPerso.Services;
using DashboardPerso.Services.Import;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DashboardPerso.Tests;

public sealed class PayeeTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly TestDbFactory factory;

    public PayeeTests()
    {
        connection.Open();
        factory = new TestDbFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        using var db = factory.CreateDbContext();
        db.Database.Migrate();
    }

    public void Dispose() => connection.Dispose();

    [Theory]
    [InlineData("ORANGE SA-ORANGE - Orange - Votre abonnement mobile", "ORANGE SA-ORANGE")]
    [InlineData("SUPERMARCHE EXEMPLE 25/02", "SUPERMARCHE EXEMPLE")]
    [InlineData("GAB VILLE CENTRE 10/03 12H00", "GAB VILLE CENTRE")]
    [InlineData("C.P.A.M. TOURS 2632400034", "C.P.A.M. TOURS")]
    [InlineData("10000228591 ECHEANCE 10/11/26", "ECHEANCE")]
    [InlineData("MAAF SANTE 05092026051531", "MAAF SANTE")]
    [InlineData("Offre Essentiel", "Offre Essentiel")]
    public void Extraction_du_beneficiaire(string description, string expected) =>
        Assert.Equal(expected, PayeeExtractor.Extract(description));

    [Fact]
    public async Task Saisie_manuelle_le_beneficiaire_apporte_sa_categorie_par_defaut()
    {
        using (var db = factory.CreateDbContext())
        {
            db.Payees.Add(new Payee { Name = "TOTALENERGIES", Key = "TOTALENERGIES", DefaultCategoryId = 3 });
            db.Accounts.Add(new Account { Label = "Carte de crédit Visa", Kind = AccountKind.CreditCard });
            db.SaveChanges();
        }
        var visa = factory.CreateDbContext().Accounts.Single(a => a.ImportId == null);
        var expenses = new ExpenseService(factory);

        var e = new Expense { Label = "Plein", Amount = 60, AccountId = visa.Id };
        await expenses.SaveAsync(e, "totalénergies");

        using var check = factory.CreateDbContext();
        var saved = check.Expenses.Include(x => x.Payee).Single();
        Assert.Equal(3, saved.CategoryId);
        Assert.Equal("TOTALENERGIES", saved.Payee!.Name);
        Assert.Equal(visa.Id, saved.AccountId);
    }

    [Fact]
    public async Task Renommer_garde_le_lien_avec_le_libelle_bancaire()
    {
        int id;
        using (var db = factory.CreateDbContext())
        {
            var p = new Payee { Name = "ORANGE SA-ORANGE", Key = "ORANGE SA-ORANGE" };
            db.Payees.Add(p);
            db.SaveChanges();
            id = p.Id;
        }
        var service = new PayeeService(factory);
        await service.UpdateAsync(id, "Orange (mobile)", 1, false);

        Assert.Equal(id, (await service.FindByNameAsync("ORANGE SA-ORANGE"))!.Id);   // import suivant
        Assert.Equal(id, (await service.FindByNameAsync("orange (mobile)"))!.Id);    // saisie manuelle
    }

    [Fact]
    public async Task Appliquer_la_categorie_par_defaut_aux_depenses_existantes()
    {
        using (var db = factory.CreateDbContext())
        {
            var p = new Payee { Name = "EDF", Key = "EDF", DefaultCategoryId = 1 };
            db.Expenses.AddRange(
                new Expense { Label = "a", Amount = 1, Payee = p },
                new Expense { Label = "b", Amount = 2, Payee = p, CategoryId = 2 },
                new Expense { Label = "c", Amount = 3, Payee = p, IsTransfer = true });
            db.SaveChanges();
        }
        var service = new PayeeService(factory);
        var payee = (await service.GetSummariesAsync()).Single();
        Assert.Equal(1, payee.Uncategorized);
        Assert.Equal(1, await service.ApplyDefaultToUncategorizedAsync(payee.Payee.Id));
        using var check = factory.CreateDbContext();
        Assert.Equal(2, check.Expenses.Single(e => e.Label == "b").CategoryId);   // déjà catégorisée : inchangée
        Assert.Equal(1, check.Expenses.Single(e => e.Label == "a").CategoryId);
    }

    [Fact]
    public async Task Le_compte_courant_ne_peut_pas_etre_supprime_ni_un_compte_utilise()
    {
        var accounts = new AccountService(factory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => accounts.DeleteAsync(Account.MainAccountId));

        var visa = new Account { Label = "Visa", Kind = AccountKind.CreditCard };
        await accounts.SaveAsync(visa);
        using (var db = factory.CreateDbContext())
        {
            db.Expenses.Add(new Expense { Label = "x", Amount = 1, AccountId = visa.Id });
            db.SaveChanges();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => accounts.DeleteAsync(visa.Id));
    }
}
