using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DashboardPerso.Models;

/// <summary>
/// Catégorie d'activité (Travail, Travaux, Tâches ménagères, Loisirs…), avec des sous-catégories
/// sur un seul niveau : Loisirs › Sport, Musique, Lecture, Photo.
/// </summary>
public class ActivityCategory
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Le nom est obligatoire.")]
    [StringLength(80)]
    public string Name { get; set; } = "";

    /// <summary>Couleur d'affichage (format #RRGGBB).</summary>
    [StringLength(7)]
    public string Color { get; set; } = "#4f6bed";

    /// <summary>Catégorie principale, ou null si c'est elle-même une catégorie principale.</summary>
    public int? ParentId { get; set; }
    public ActivityCategory? Parent { get; set; }
    public List<ActivityCategory> Children { get; set; } = [];

    /// <summary>Ordre d'affichage parmi les catégories de même niveau.</summary>
    public int SortOrder { get; set; }

    [NotMapped]
    public string FullName => Parent is null ? Name : $"{Parent.Name} › {Name}";

    [NotMapped]
    public bool IsSubcategory => ParentId is not null;

    /// <summary>Catégorie principale (elle-même si elle n'a pas de parent).</summary>
    [NotMapped]
    public int RootId => ParentId ?? Id;
}

/// <summary>Créneau de la semaine type : chaque lundi de 9 h à 12 h, Travail.</summary>
public class WeeklySlot
{
    public int Id { get; set; }

    public DayOfWeek Day { get; set; } = DayOfWeek.Monday;

    public TimeOnly Start { get; set; } = new(9, 0);

    public TimeOnly End { get; set; } = new(10, 0);

    public int CategoryId { get; set; }
    public ActivityCategory? Category { get; set; }

    /// <summary>Précision facultative (« Piano », « Course à pied »). Sinon, le nom de la catégorie.</summary>
    [StringLength(100)]
    public string? Title { get; set; }

    [StringLength(500)]
    public string? Notes { get; set; }
}

/// <summary>Activité prévue à une date précise, copiée de la semaine type ou ajoutée à la main.</summary>
public class PlannedActivity
{
    public int Id { get; set; }

    public DateOnly Date { get; set; }

    public TimeOnly Start { get; set; } = new(9, 0);

    public TimeOnly End { get; set; } = new(10, 0);

    public int CategoryId { get; set; }
    public ActivityCategory? Category { get; set; }

    [StringLength(100)]
    public string? Title { get; set; }

    [StringLength(500)]
    public string? Notes { get; set; }

    public bool Done { get; set; }

    /// <summary>Créneau de la semaine type d'origine (null : ajoutée à la main).</summary>
    public int? SlotId { get; set; }
    public WeeklySlot? Slot { get; set; }
}
