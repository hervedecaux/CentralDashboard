using System.ComponentModel.DataAnnotations;

namespace DashboardPerso.Models;

public class Expense
{
    public int Id { get; set; }

    public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.Today);

    [Required(ErrorMessage = "Le libellé est obligatoire.")]
    [StringLength(120)]
    public string Label { get; set; } = "";

    [Range(0.01, 1_000_000, ErrorMessage = "Le montant doit être supérieur à 0.")]
    public decimal Amount { get; set; }

    public int? CategoryId { get; set; }
    public BudgetCategory? Category { get; set; }

    [StringLength(500)]
    public string? Notes { get; set; }

    /// <summary>
    /// Empreinte de l'opération bancaire d'origine (null pour une saisie manuelle).
    /// Sert à ne pas importer deux fois la même opération.
    /// </summary>
    [StringLength(64)]
    public string? ImportKey { get; set; }

    /// <summary>Compte ou carte d'origine.</summary>
    public int AccountId { get; set; } = Account.MainAccountId;
    public Account? Account { get; set; }

    /// <summary>Bénéficiaire (null si inconnu).</summary>
    public int? PayeeId { get; set; }
    public Payee? Payee { get; set; }

    /// <summary>
    /// Transfert (débit du relevé d'une carte différée, virement vers un compte de crédit…) :
    /// visible dans les listes, sans catégorie, et exclu du budget et des totaux de dépenses.
    /// </summary>
    public bool IsTransfer { get; set; }

}
