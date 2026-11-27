using System.ComponentModel.DataAnnotations;

namespace DashboardPerso.Models;

/// <summary>Une catégorie de budget avec un montant prévu par mois.</summary>
public class BudgetCategory
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Le nom est obligatoire.")]
    [StringLength(80)]
    public string Name { get; set; } = "";

    [Range(0, 1_000_000, ErrorMessage = "Le montant doit être positif.")]
    public decimal MonthlyBudget { get; set; }

    /// <summary>Couleur d'affichage (format #RRGGBB).</summary>
    [StringLength(7)]
    public string Color { get; set; } = "#4f6bed";

    public List<Expense> Expenses { get; set; } = [];
}
