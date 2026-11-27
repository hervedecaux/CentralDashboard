using System.ComponentModel.DataAnnotations;

namespace DashboardPerso.Models;

/// <summary>
/// Bénéficiaire d'une opération (commerçant, organisme, personne…), extrait des libellés bancaires
/// ou saisi à la main. Peut porter une catégorie par défaut, appliquée à ses nouvelles dépenses.
/// </summary>
public class Payee
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Le nom est obligatoire.")]
    [StringLength(120)]
    public string Name { get; set; } = "";

    /// <summary>Nom normalisé (majuscules, sans accents) : sert à reconnaître le bénéficiaire.</summary>
    [StringLength(120)]
    public string Key { get; set; } = "";

    public int? DefaultCategoryId { get; set; }
    public BudgetCategory? DefaultCategory { get; set; }

    /// <summary>Les opérations vers ce bénéficiaire sont des transferts (ex. organisme de crédit).</summary>
    public bool DefaultIsTransfer { get; set; }

    public List<Expense> Expenses { get; set; } = [];
}
