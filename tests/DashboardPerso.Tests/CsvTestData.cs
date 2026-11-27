namespace DashboardPerso.Tests;

internal static class CsvTestData
{
    /// <summary>Export Crédit Agricole fictif (Windows-1252), même structure que le vrai fichier.</summary>
    public static byte[] Sample() =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ca-anonymise.csv"));
}
