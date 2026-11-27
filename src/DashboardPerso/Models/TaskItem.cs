using System.ComponentModel.DataAnnotations;

namespace DashboardPerso.Models;

public enum TaskItemStatus
{
    [Display(Name = "À faire")] Todo = 0,
    [Display(Name = "En cours")] InProgress = 1,
    [Display(Name = "Terminée")] Done = 2
}

public enum TaskPriority
{
    [Display(Name = "Basse")] Low = 0,
    [Display(Name = "Normale")] Normal = 1,
    [Display(Name = "Haute")] High = 2
}

public class TaskItem
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Le titre est obligatoire.")]
    [StringLength(150)]
    public string Title { get; set; } = "";

    [StringLength(2000)]
    public string? Description { get; set; }

    public DateOnly? DueDate { get; set; }

    /// <summary>
    /// Jour où l'on prévoit de faire la tâche (liste « Aujourd'hui » ou « Demain »), distinct de l'échéance.
    /// Une tâche prévue un jour passé et non terminée reste dans la liste du jour (reportée).
    /// </summary>
    public DateOnly? PlannedFor { get; set; }

    public TaskPriority Priority { get; set; } = TaskPriority.Normal;

    /// <summary>Type d'activité (Travaux, Tâches ménagères…) : la tâche est proposée pendant ces créneaux du planning.</summary>
    public int? ActivityCategoryId { get; set; }
    public ActivityCategory? ActivityCategory { get; set; }

    public TaskItemStatus Status { get; set; } = TaskItemStatus.Todo;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime? CompletedAt { get; set; }

    /// <summary>Prévue un jour précédent et pas encore terminée : reportée à aujourd'hui.</summary>
    public bool IsCarriedOver =>
        Status != TaskItemStatus.Done && PlannedFor is { } p && p < DateOnly.FromDateTime(DateTime.Today);

    public bool IsOverdue =>
        Status != TaskItemStatus.Done && DueDate is { } d && d < DateOnly.FromDateTime(DateTime.Today);
}
