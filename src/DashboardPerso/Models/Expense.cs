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
}
