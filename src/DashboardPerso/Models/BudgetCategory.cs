using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DashboardPerso.Models;

/// <summary>
/// Une catégorie de budget, avec un montant prévu par an.
/// Une catégorie peut avoir des sous-catégories (un seul niveau) :
/// Automobile › Carburant, Assurance, Entretien.
/// </summary>
public class BudgetCategory
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Le nom est obligatoire.")]
    [StringLength(80)]
    public string Name { get; set; } = "";

    /// <summary>
    /// Budget annuel propre à la catégorie. Pour une catégorie principale qui a des sous-catégories,
    /// c'est la part « hors sous-catégories » : le total s'obtient en y ajoutant celui des sous-catégories.
    /// </summary>
    [Range(0, 10_000_000, ErrorMessage = "Le montant doit être positif.")]
    public decimal AnnualBudget { get; set; }

    /// <summary>Couleur d'affichage (format #RRGGBB).</summary>
    [StringLength(7)]
    public string Color { get; set; } = "#4f6bed";

    /// <summary>Catégorie principale, ou null si c'est elle-même une catégorie principale.</summary>
    public int? ParentId { get; set; }
    public BudgetCategory? Parent { get; set; }
    public List<BudgetCategory> Children { get; set; } = [];

    public List<Expense> Expenses { get; set; } = [];

    /// <summary>« Automobile › Carburant », ou simplement « Automobile ». Nécessite Parent chargé.</summary>
    [NotMapped]
    public string FullName => Parent is null ? Name : $"{Parent.Name} › {Name}";

    [NotMapped]
    public bool IsSubcategory => ParentId is not null;
}
