namespace DashboardPerso.Models;

/// <summary>
/// Part d'une dépense ventilée sur plusieurs catégories (ex. un ticket de supermarché :
/// 80 € Alimentation + 20 € Maison). La somme des parts égale le montant de la dépense.
/// </summary>
public class ExpenseSplit
{
    public int Id { get; set; }

    public int ExpenseId { get; set; }
    public Expense? Expense { get; set; }

    public int? CategoryId { get; set; }
    public BudgetCategory? Category { get; set; }

    public decimal Amount { get; set; }
}
