namespace DashboardPerso.Services.Import;

/// <summary>Une opération lue dans un relevé bancaire, avant import.</summary>
public record BankOperation
{
    public required DateOnly Date { get; init; }

    /// <summary>Type d'opération (1re ligne du libellé) : « Prélèvement », « Retrait au distributeur »…</summary>
    public required string Type { get; init; }

    /// <summary>Bénéficiaire / description (2e ligne du libellé).</summary>
    public required string Description { get; init; }

    /// <summary>Libellé complet, toutes lignes, tel que fourni par la banque.</summary>
    public required string RawLabel { get; init; }

    /// <summary>Montant débité (positif), ou null pour un crédit.</summary>
    public decimal? Debit { get; init; }

    /// <summary>Montant crédité (positif), ou null pour un débit.</summary>
    public decimal? Credit { get; init; }

    public bool IsDebit => Debit is > 0;
}
