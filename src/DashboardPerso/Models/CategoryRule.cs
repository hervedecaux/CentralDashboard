using System.ComponentModel.DataAnnotations;

namespace DashboardPerso.Models;

/// <summary>
/// Règle de catégorisation automatique : si le libellé bancaire contient <see cref="Pattern"/>
/// (sans tenir compte des majuscules ni des accents), la dépense importée reçoit la catégorie,
/// ou est marquée comme transfert.
/// </summary>
public class CategoryRule
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Le texte à rechercher est obligatoire.")]
    [StringLength(100)]
    public string Pattern { get; set; } = "";

    /// <summary>Catégorie attribuée (null si la règle marque un transfert).</summary>
    public int? CategoryId { get; set; }
    public BudgetCategory? Category { get; set; }

    /// <summary>Si vrai, l'opération est un transfert : sans catégorie, hors budget.</summary>
    public bool MarkAsTransfer { get; set; }
}
