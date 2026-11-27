using System.ComponentModel.DataAnnotations;

namespace DashboardPerso.Models;

public enum AccountKind
{
    [Display(Name = "Compte courant")] CheckingAccount = 0,
    [Display(Name = "Carte à débit différé")] DeferredDebitCard = 1,
    [Display(Name = "Carte de crédit")] CreditCard = 2,
    [Display(Name = "Autre")] Other = 3
}

/// <summary>
/// Compte ou carte d'où proviennent les dépenses. Créé automatiquement à l'import
/// (identifié par <see cref="ImportId"/>), ou à la main pour une carte sans relevé téléchargeable.
/// </summary>
public class Account
{
    public const int MainAccountId = 1;

    public int Id { get; set; }

    [Required(ErrorMessage = "Le libellé est obligatoire.")]
    [StringLength(80)]
    public string Label { get; set; } = "";

    public AccountKind Kind { get; set; } = AccountKind.CreditCard;

    /// <summary>
    /// Identifiant stable dans les relevés importés : « Compte » pour le compte, « Carte ••5524 » pour une carte.
    /// Null pour un compte saisi uniquement à la main.
    /// </summary>
    [StringLength(80)]
    public string? ImportId { get; set; }

    public bool IsManual => ImportId is null;

    /// <summary>Solde de départ, à la fin de la journée <see cref="InitialBalanceDate"/>.</summary>
    public decimal? InitialBalance { get; set; }
    public DateOnly? InitialBalanceDate { get; set; }

    /// <summary>Dernier solde indiqué par la banque dans un relevé importé (« Solde au … »).</summary>
    public decimal? BankBalance { get; set; }
    public DateOnly? BankBalanceDate { get; set; }
}
