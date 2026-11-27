using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace DashboardPerso.Models;

public static class EnumLabels
{
    /// <summary>Renvoie le libellé [Display(Name)] d'une valeur d'enum.</summary>
    public static string Label(this Enum value) =>
        value.GetType().GetField(value.ToString())?.GetCustomAttribute<DisplayAttribute>()?.Name
        ?? value.ToString();
}
