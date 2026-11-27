using DashboardPerso.Services.Import;

namespace DashboardPerso.Tests;

public class CreditAgricoleCsvParserTests
{
    private readonly List<BankOperation> ops = CreditAgricoleCsvParser.Parse(CsvTestData.Sample());

    [Fact]
    public void Lit_les_operations_du_compte_puis_celles_des_cartes()
    {
        Assert.Equal(7, ops.Count(o => !o.IsCardOperation));
        var carte = ops.Where(o => o.IsCardOperation).ToList();
        Assert.Equal(3, carte.Count);
        Assert.All(carte, o => Assert.Equal("Carte ••5524 · Mme Claire Dupont", o.Source));
        Assert.Equal("SUPERMARCHE EXEMPLE 25/02", carte[0].Description);
        Assert.Equal(10m, carte.Single(o => !o.IsDebit).Credit);
    }

    [Fact]
    public void Repere_le_debit_du_releve_carte_par_son_montant()
    {
        var releve = ops.Single(o => o.IsCardStatementTotal);
        Assert.Equal(107.50m, releve.Debit);          // 45 + 72,50 - 10
        Assert.False(releve.IsCardOperation);
        Assert.Single(ops, o => o.IsCardStatementTotal);
    }

    [Fact]
    public void Decode_le_Windows_1252_et_separe_type_et_beneficiaire()
    {
        var edf = ops.Single(o => o.Date == new DateOnly(2026, 3, 2));
        Assert.Equal("Prélèvement", edf.Type);
        Assert.Equal("ÉLECTRICITÉ EXEMPLE - Facture énergie", edf.Description);
    }

    [Fact]
    public void Gere_les_points_virgules_dans_un_libelle_entre_guillemets()
    {
        var telecom = ops.Single(o => o.Date == new DateOnly(2026, 3, 12));
        Assert.Equal(42.99m, telecom.Debit);
        Assert.Null(telecom.Credit);
        Assert.Contains("FR00ZZZ000000", telecom.RawLabel);
    }

    [Fact]
    public void Lit_les_montants_francais_avec_separateur_de_milliers()
    {
        Assert.Equal(1386.53m, ops.Single(o => o.Type == "Remboursement de prêt").Debit);
        var salaire = ops.Single(o => o.Date == new DateOnly(2026, 3, 15));
        Assert.Equal(2100m, salaire.Credit);
        Assert.False(salaire.IsDebit);
    }

    [Theory]
    [InlineData("1 386,53", 1386.53)]
    [InlineData("1 386,53", 1386.53)]
    [InlineData("1 386,53", 1386.53)]
    [InlineData(" 60,00 €", 60.00)]
    public void ParseAmount(string input, double expected) =>
        Assert.Equal((decimal)expected, CreditAgricoleCsvParser.ParseAmount(input));

    [Fact]
    public void Refuse_un_fichier_sans_en_tete_attendu() =>
        Assert.Throws<FormatException>(() => CreditAgricoleCsvParser.ParseText("a;b;c\n1;2;3\n"));

    [Fact]
    public void Accepte_aussi_un_fichier_en_UTF8()
    {
        var utf8 = System.Text.Encoding.UTF8.GetBytes(CreditAgricoleCsvParser.Decode(CsvTestData.Sample()));
        Assert.Equal(ops.Count, CreditAgricoleCsvParser.Parse(utf8).Count);
    }
}
