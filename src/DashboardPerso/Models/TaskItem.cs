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

    public TaskPriority Priority { get; set; } = TaskPriority.Normal;

    public TaskItemStatus Status { get; set; } = TaskItemStatus.Todo;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime? CompletedAt { get; set; }

    public bool IsOverdue =>
        Status != TaskItemStatus.Done && DueDate is { } d && d < DateOnly.FromDateTime(DateTime.Today);
}
