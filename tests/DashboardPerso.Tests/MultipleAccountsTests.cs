using System.Text;
using DashboardPerso.Data;
using DashboardPerso.Models;
using DashboardPerso.Services;
using DashboardPerso.Services.Import;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DashboardPerso.Tests;

public sealed class MultipleAccountsTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly TestDbFactory factory;
    private readonly ImportService import;
    private readonly AccountService accounts;

    public MultipleAccountsTests()
    {
        connection.Open();
        factory = new TestDbFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        using var db = factory.CreateDbContext();
        db.Database.Migrate();
        import = new ImportService(factory);
        accounts = new AccountService(factory);
    }

    public void Dispose() => connection.Dispose();

    /// <summary>Le relevé d'exemple, pour un autre numéro de compte.</summary>
    private static byte[] OtherAccount(string number = "99988877766")
    {
        var text = CreditAgricoleCsvParser.Decode(CsvTestData.Sample()).Replace("00000000000", number);
        return Encoding.UTF8.GetBytes(text);
    }

    [Fact]
    public void Le_numero_de_compte_est_lu_en_tete_du_releve()
    {
        Assert.Equal("00000000000", CreditAgricoleCsvParser.ParseStatement(CsvTestData.Sample()).AccountNumber);
        Assert.Null(CreditAgricoleCsvParser.ParseAccountNumber("Date d'opération;Libellé;Débit euros;Crédit euros;\ncarte n° 513780******5524"));
    }

    [Fact]
    public async Task Le_premier_releve_va_dans_le_compte_courant_et_lui_donne_son_numero()
    {
        var preview = await import.PreviewStatementAsync(CsvTestData.Sample());
        Assert.Equal(Account.MainAccountId, preview.Target!.AccountId);
        await import.ImportAsync(preview);

        using var db = factory.CreateDbContext();
        var main = db.Accounts.Single(a => a.Id == Account.MainAccountId);
        Assert.Equal("00000000000", main.AccountNumber);
        Assert.Equal("Compte", main.ImportId);      // empreintes historiques inchangées
        Assert.Equal(1234.56m, main.BankBalance);
    }

    [Fact]
    public async Task Un_releve_d_un_autre_compte_cree_ce_compte_sans_se_confondre_avec_le_premier()
    {
        await import.ImportAsync(await import.PreviewStatementAsync(CsvTestData.Sample()));

        var other = await import.PreviewStatementAsync(OtherAccount());
        Assert.True(other.Target!.IsNew);
        // Mêmes opérations que le compte courant, mais d'un autre compte : pas « déjà importées »…
        var accountRows = other.Rows.Where(r => !r.Operation.IsCardOperation).ToList();
        Assert.All(accountRows, r => Assert.Equal(ImportStatus.New, r.Status));
        // …alors que les cartes, identifiées par leur numéro, le sont.
        Assert.All(other.Rows.Where(r => r.Operation.IsCardOperation), r => Assert.Equal(ImportStatus.AlreadyImported, r.Status));

        Assert.Equal(accountRows.Count, await import.ImportAsync(other));

        int otherId;
        using (var db = factory.CreateDbContext())
        {
            var created = db.Accounts.Single(a => a.AccountNumber == "99988877766");
            otherId = created.Id;
            Assert.Equal(AccountKind.CheckingAccount, created.Kind);
            Assert.Equal("Compte ••7766", created.Label);
            Assert.Equal(1234.56m, created.BankBalance);
            Assert.Equal(7, db.Expenses.Count(e => e.AccountId == created.Id));
            Assert.Equal(7, db.Expenses.Count(e => e.AccountId == Account.MainAccountId));
        }

        // Les relevés suivants de chaque compte sont reconnus par leur numéro.
        var again = await import.PreviewStatementAsync(OtherAccount());
        Assert.Equal(otherId, again.Target!.AccountId);
        Assert.All(again.Rows, r => Assert.Equal(ImportStatus.AlreadyImported, r.Status));
        var first = await import.PreviewStatementAsync(CsvTestData.Sample());
        Assert.Equal(Account.MainAccountId, first.Target!.AccountId);
        Assert.All(first.Rows, r => Assert.Equal(ImportStatus.AlreadyImported, r.Status));
    }

    [Fact]
    public async Task On_peut_choisir_un_compte_cree_a_la_main_comme_destination()
    {
        await import.ImportAsync(await import.PreviewStatementAsync(CsvTestData.Sample()));
        var joint = new Account { Label = "Compte joint", Kind = AccountKind.CheckingAccount };
        await accounts.SaveAsync(joint);

        var preview = await import.PreviewStatementAsync(OtherAccount(), joint.Id);
        Assert.Equal(joint.Id, preview.Target!.AccountId);
        await import.ImportAsync(preview);

        using var db = factory.CreateDbContext();
        var saved = db.Accounts.Single(a => a.Id == joint.Id);
        Assert.Equal("99988877766", saved.AccountNumber);
        Assert.NotNull(saved.ImportId);
        Assert.Equal(7, db.Expenses.Count(e => e.AccountId == joint.Id));
        Assert.Equal((await import.PreviewStatementAsync(OtherAccount())).Target!.AccountId, joint.Id);
    }

    [Fact]
    public async Task Un_numero_ne_peut_appartenir_qu_a_un_compte()
    {
        await accounts.SaveAsync(new Account { Label = "A", Kind = AccountKind.CheckingAccount, AccountNumber = "123 456 789" });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            accounts.SaveAsync(new Account { Label = "B", Kind = AccountKind.CheckingAccount, AccountNumber = "123456789" }));
    }

    [Fact]
    public async Task Supprimer_une_carte_avec_ses_operations()
    {
        await import.ImportAsync(await import.PreviewStatementAsync(CsvTestData.Sample()));
        int cardId;
        using (var db = factory.CreateDbContext())
        {
            cardId = db.Accounts.Single(a => a.ImportId == "Carte ••5524").Id;
            var e = db.Expenses.First(x => x.AccountId == cardId && !x.IsIncome);
            db.ExpenseSplits.AddRange(
                new ExpenseSplit { ExpenseId = e.Id, CategoryId = 1, Amount = e.Amount - 1 },
                new ExpenseSplit { ExpenseId = e.Id, CategoryId = 2, Amount = 1 });
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => accounts.DeleteAsync(cardId));
        Assert.Equal(3, await accounts.DeleteAsync(cardId, AccountDeletion.DeleteOperations));

        using var check = factory.CreateDbContext();
        Assert.DoesNotContain(check.Accounts, a => a.Id == cardId);
        Assert.Empty(check.ExpenseSplits);
        Assert.Equal(7, check.Expenses.Count());
    }

    [Fact]
    public async Task Supprimer_un_compte_en_rattachant_ses_operations_a_un_autre()
    {
        await import.ImportAsync(await import.PreviewStatementAsync(CsvTestData.Sample()));
        int cardId;
        using (var db = factory.CreateDbContext())
            cardId = db.Accounts.Single(a => a.ImportId == "Carte ••5524").Id;
        var visa = new Account { Label = "Visa", Kind = AccountKind.CreditCard };
        await accounts.SaveAsync(visa);

        await Assert.ThrowsAsync<InvalidOperationException>(() => accounts.DeleteAsync(cardId, AccountDeletion.MoveOperations));
        Assert.Equal(3, await accounts.DeleteAsync(cardId, AccountDeletion.MoveOperations, visa.Id));

        using (var db = factory.CreateDbContext())
        {
            Assert.Equal(3, db.Expenses.Count(e => e.AccountId == visa.Id));
            Assert.Equal(10, db.Expenses.Count());
        }

        // Réimporter le relevé recrée la carte, sans doublonner les opérations déplacées.
        var rows = await import.PreviewAsync(CsvTestData.Sample());
        Assert.All(rows, r => Assert.Equal(ImportStatus.AlreadyImported, r.Status));
    }

    [Fact]
    public async Task Les_soldes_sont_calcules_pour_chaque_compte_bancaire()
    {
        await import.ImportAsync(await import.PreviewStatementAsync(CsvTestData.Sample()));
        await import.ImportAsync(await import.PreviewStatementAsync(OtherAccount()));
        using (var db = factory.CreateDbContext())
        {
            var other = db.Accounts.Single(a => a.AccountNumber == "99988877766");
            other.InitialBalance = 100m;
            other.InitialBalanceDate = new DateOnly(2026, 2, 28);
            db.SaveChanges();
        }

        var balances = await accounts.GetBankBalancesAsync();
        Assert.Equal(2, balances.Count);                                   // les cartes n'ont pas de solde
        Assert.Equal(Account.MainAccountId, balances[0].Account.Id);
        Assert.Null(balances[0].Balance);
        Assert.NotNull(balances[1].Balance!.BankBalance);
    }
}
