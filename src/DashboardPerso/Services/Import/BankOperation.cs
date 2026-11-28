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

    /// <summary>Provenance, identifiant stable : « Compte » ou une carte, ex. « Carte ••5524 ».</summary>
    public string Source { get; init; } = AccountSource;

    /// <summary>Porteur de la carte (ex. « Mme Claire Dupont »), pour proposer un libellé.</summary>
    public string? CardHolder { get; init; }

    /// <summary>Libellé proposé pour le compte ou la carte à sa première apparition.</summary>
    public string DefaultSourceLabel => !IsCardOperation ? "Compte courant"
        : CardHolder is null ? Source : $"{Source} · {CardHolder}";

    /// <summary>Bénéficiaire déduit du libellé (voir <see cref="PayeeExtractor"/>).</summary>
    public string Payee => PayeeExtractor.Extract(Description);

    public bool IsCardOperation => Source != AccountSource;

    /// <summary>
    /// Ligne du compte qui débite le total d'une carte à débit différé : les paiements détaillés
    /// sont importés depuis la section carte, cette ligne est donc un simple transfert.
    /// </summary>
    public bool IsCardStatementTotal { get; init; }

    public bool IsDebit => Debit is > 0;

    /// <summary>Montant (positif) de l'opération, débit ou crédit.</summary>
    public decimal Amount => Debit ?? Credit ?? 0;

    public const string AccountSource = "Compte";
}

/// <summary>Relevé lu : opérations et solde du compte indiqué par la banque.</summary>
public record BankStatement(List<BankOperation> Operations, decimal? Balance, DateOnly? BalanceDate, string? AccountNumber = null);
