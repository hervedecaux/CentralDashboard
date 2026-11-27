using DashboardPerso.Data;
using DashboardPerso.Models;
using DashboardPerso.Services.Import;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DashboardPerso.Tests;

public sealed class ImportServiceTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly TestDbFactory factory;
    private readonly ImportService service;

    public ImportServiceTests()
    {
        connection.Open();
        factory = new TestDbFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        using var db = factory.CreateDbContext();
        db.Database.Migrate();
        service = new ImportService(factory);
    }

    public void Dispose() => connection.Dispose();

    [Fact]
    public async Task Apercu_coche_les_debits_et_ignore_les_credits()
    {
        var rows = await service.PreviewAsync(CsvTestData.Sample());
        Assert.Equal(8, rows.Count(r => r.Status == ImportStatus.New && r.Include));   // 6 débits compte + 2 paiements carte
        Assert.Equal(2, rows.Count(r => r.Status == ImportStatus.Credit && !r.Include)); // salaire + remboursement carte
    }

    [Fact]
    public async Task Deux_retraits_identiques_le_meme_jour_sont_deux_operations()
    {
        var rows = await service.PreviewAsync(CsvTestData.Sample());
        var retraits = rows.Where(r => r.Operation.Type == "Retrait au distributeur").ToList();
        Assert.Equal(2, retraits.Count);
        Assert.NotEqual(retraits[0].ImportKey, retraits[1].ImportKey);
    }

    [Fact]
    public async Task Reimporter_le_meme_fichier_ne_cree_pas_de_doublon()
    {
        var first = await service.PreviewAsync(CsvTestData.Sample());
        Assert.Equal(8, await service.ImportAsync(first));

        var second = await service.PreviewAsync(CsvTestData.Sample());
        Assert.All(second.Where(r => r.Operation.IsDebit), r => Assert.Equal(ImportStatus.AlreadyImported, r.Status));
        Assert.Equal(0, await service.ImportAsync(second));

        // Même en forçant la sélection, l'enregistrement revérifie.
        foreach (var r in second) r.Include = true;
        Assert.Equal(0, await service.ImportAsync(second));

        using var db = factory.CreateDbContext();
        Assert.Equal(8, db.Expenses.Count());
    }

    [Fact]
    public async Task Le_releve_carte_est_un_transfert_et_les_paiements_carte_gardent_leur_provenance()
    {
        var rows = await service.PreviewAsync(CsvTestData.Sample());
        var releve = rows.Single(r => r.Operation.IsCardStatementTotal);
        Assert.True(releve.IsTransfer);
        Assert.Null(releve.CategoryId);

        await service.ImportAsync(rows);
        using var db = factory.CreateDbContext();
        Assert.True(db.Expenses.Single(e => e.Amount == 107.50m).IsTransfer);
        Assert.Equal(2, db.Expenses.Count(e => e.Source == "Carte ••5524 · Mme Claire Dupont"));
        Assert.Equal(6, db.Expenses.Count(e => e.Source == "Compte"));
    }

    [Fact]
    public async Task Une_regle_peut_marquer_un_transfert()
    {
        using (var db = factory.CreateDbContext())
        {
            db.CategoryRules.Add(new CategoryRule { Pattern = "ECHEANCE", MarkAsTransfer = true });
            db.SaveChanges();
        }
        var rows = await service.PreviewAsync(CsvTestData.Sample());
        var pret = rows.Single(r => r.Operation.Type == "Remboursement de prêt");
        Assert.True(pret.IsTransfer);
        Assert.Null(pret.CategoryId);
        Assert.Equal("ECHEANCE", pret.MatchedRule);
    }

    [Fact]
    public void L_empreinte_des_operations_du_compte_ne_change_pas_avec_les_cartes()
    {
        var op = new BankOperation { Date = new DateOnly(2026, 3, 2), Type = "T", Description = "D", RawLabel = "T | D", Debit = 1m };
        var card = op with { Source = "Carte ••5524" };
        var keys = ImportService.ComputeKeys([op, card]);
        Assert.NotEqual(keys[0], keys[1]);
        // Même empreinte qu'une opération du compte importée avant l'ajout des cartes.
        Assert.Equal(ImportService.ComputeKeys([op])[0], keys[0]);
    }

    [Fact]
    public async Task Signale_un_doublon_possible_avec_une_saisie_manuelle()
    {
        using (var db = factory.CreateDbContext())
        {
            db.Expenses.Add(new Expense { Date = new DateOnly(2026, 3, 5), Label = "Prêt maison", Amount = 1386.53m });
            db.SaveChanges();
        }
        var rows = await service.PreviewAsync(CsvTestData.Sample());
        var pret = rows.Single(r => r.Operation.Type == "Remboursement de prêt");
        Assert.Equal(ImportStatus.PossibleDuplicate, pret.Status);
        Assert.False(pret.Include);
    }

    [Fact]
    public async Task Applique_les_regles_sans_tenir_compte_des_accents_et_la_plus_longue_gagne()
    {
        using (var db = factory.CreateDbContext())
        {
            db.CategoryRules.AddRange(
                new CategoryRule { Pattern = "electricite", CategoryId = 1 },          // Logement
                new CategoryRule { Pattern = "Retrait", CategoryId = 4 },              // Loisirs
                new CategoryRule { Pattern = "retrait au distributeur", CategoryId = 2 }); // Alimentation
            db.SaveChanges();
        }
        var rows = await service.PreviewAsync(CsvTestData.Sample());
        Assert.Equal(1, rows.Single(r => r.Operation.Date == new DateOnly(2026, 3, 2)).CategoryId);
        Assert.All(rows.Where(r => r.Operation.Type == "Retrait au distributeur"), r => Assert.Equal(2, r.CategoryId));

        await service.ImportAsync(rows);
        using var check = factory.CreateDbContext();
        Assert.Equal(1, check.Expenses.Single(e => e.Date == new DateOnly(2026, 3, 2)).CategoryId);
    }

    [Fact]
    public async Task ApplyRules_ne_remplace_pas_un_choix_manuel()
    {
        var rows = await service.PreviewAsync(CsvTestData.Sample());
        var edf = rows.Single(r => r.Operation.Date == new DateOnly(2026, 3, 2));
        edf.CategoryId = 5;
        edf.CategoryChosenByUser = true;

        using (var db = factory.CreateDbContext())
        {
            db.CategoryRules.Add(new CategoryRule { Pattern = "ELECTRICITE", CategoryId = 1 });
            db.SaveChanges();
        }
        await service.ApplyRulesAsync(rows);
        Assert.Equal(5, edf.CategoryId);
    }
}
