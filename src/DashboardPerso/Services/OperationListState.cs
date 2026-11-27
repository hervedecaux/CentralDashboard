namespace DashboardPerso.Services;

/// <summary>
/// Dernier affichage de la liste des opérations (vue, période, onglet, filtres, page), conservé le temps de la session
/// pour retrouver la liste dans le même état au retour d'une fiche.
/// </summary>
public class OperationListState
{
    public DateOnly Month { get; set; } = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    public bool YearView { get; set; }
    public int? AccountId { get; set; }
    public string CategoryFilter { get; set; } = "";
    public bool ShowTransfers { get; set; } = true;
    public bool ShowIncome { get; set; } = true;
    public int PageIndex { get; set; }
}
