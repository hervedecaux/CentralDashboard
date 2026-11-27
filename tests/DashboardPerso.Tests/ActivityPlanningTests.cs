using DashboardPerso.Data;
using DashboardPerso.Models;
using DashboardPerso.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DashboardPerso.Tests;

public sealed class ActivityPlanningTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly TestDbFactory factory;
    private readonly ActivityService activities;
    private readonly TaskService tasks;
    private static readonly DateOnly Monday = new(2026, 11, 23);

    public ActivityPlanningTests()
    {
        connection.Open();
        factory = new TestDbFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        using var db = factory.CreateDbContext();
        db.Database.Migrate();
        activities = new ActivityService(factory);
        tasks = new TaskService(factory);
    }

    public void Dispose() => connection.Dispose();

    [Fact]
    public async Task Les_categories_de_depart_sont_creees_avec_les_loisirs_en_sous_categories()
    {
        var cats = await activities.GetCategoriesAsync();
        Assert.Equal(["Travail", "Travaux", "Tâches ménagères", "Loisirs", "Sport", "Musique", "Lecture", "Photo"],
            cats.Select(c => c.Name));
        Assert.All(cats.Where(c => c.Name is "Sport" or "Photo"), c => Assert.Equal(4, c.ParentId));
    }

    [Fact]
    public void Le_lundi_de_la_semaine_est_calcule_pour_chaque_jour()
    {
        Assert.Equal(Monday, ActivityService.MondayOf(Monday));
        Assert.Equal(Monday, ActivityService.MondayOf(new DateOnly(2026, 11, 29)));   // dimanche
        Assert.Equal(DayOfWeek.Sunday, ActivityService.DayFromIndex(6));
        Assert.Equal(0, ActivityService.DayIndex(DayOfWeek.Monday));
    }

    [Fact]
    public async Task Un_creneau_peut_etre_repete_sur_plusieurs_jours_et_applique_une_seule_fois()
    {
        var work = new WeeklySlot { Start = new(9, 0), End = new(12, 0), CategoryId = 1 };
        await activities.SaveSlotAsync(work, [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Friday]);
        await activities.SaveSlotAsync(new WeeklySlot { Day = DayOfWeek.Sunday, Start = new(10, 0), End = new(11, 30), CategoryId = 5, Title = "Course" });

        Assert.Equal(4, (await activities.GetSlotsAsync()).Count);
        Assert.Equal(4, await activities.ApplyTemplateAsync(Monday.AddDays(2)));   // n'importe quel jour de la semaine
        Assert.Equal(0, await activities.ApplyTemplateAsync(Monday));              // déjà appliquée

        var week = await activities.GetWeekAsync(Monday);
        Assert.Equal([Monday, Monday.AddDays(1), Monday.AddDays(4), Monday.AddDays(6)], week.Select(a => a.Date));
        Assert.Equal("Course", week.Last().Title);

        // Une activité supprimée revient si on réapplique ; les autres semaines ne sont pas touchées.
        await activities.DeleteActivityAsync(week[0].Id);
        Assert.Equal(1, await activities.ApplyTemplateAsync(Monday));
        Assert.Empty(await activities.GetWeekAsync(Monday.AddDays(7)));
    }

    [Fact]
    public async Task La_fin_doit_etre_apres_le_debut()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            activities.SaveSlotAsync(new WeeklySlot { Start = new(10, 0), End = new(9, 0), CategoryId = 1 }));
    }

    [Fact]
    public async Task Les_totaux_sont_regroupes_par_categorie_principale()
    {
        await activities.SaveActivityAsync(new PlannedActivity { Date = Monday, Start = new(18, 0), End = new(19, 0), CategoryId = 5, Done = true });
        await activities.SaveActivityAsync(new PlannedActivity { Date = Monday, Start = new(20, 0), End = new(20, 30), CategoryId = 6 });
        var cats = await activities.GetCategoriesAsync();
        var week = await activities.GetWeekAsync(Monday);

        var total = Assert.Single(ActivityService.Totals(week.Select(a => (a.Category!, a.Start, a.End, a.Done)), cats));
        Assert.Equal("Loisirs", total.Category.Name);
        Assert.Equal(TimeSpan.FromMinutes(90), total.Planned);
        Assert.Equal(TimeSpan.FromHours(1), total.Done);
        Assert.Equal("1 h 30", ActivityService.FormatDuration(total.Planned));
    }

    [Fact]
    public async Task Supprimer_une_categorie_rattache_ses_creneaux_et_taches_a_une_autre()
    {
        var garden = new ActivityCategory { Name = "Jardinage", Color = "#2e9e6a" };
        await activities.SaveCategoryAsync(garden);
        await activities.SaveSlotAsync(new WeeklySlot { Day = DayOfWeek.Saturday, Start = new(9, 0), End = new(11, 0), CategoryId = garden.Id });
        await activities.ApplyTemplateAsync(Monday);
        var task = new TaskItem { Title = "Tailler la haie", ActivityCategoryId = garden.Id };
        await tasks.SaveAsync(task);

        await activities.DeleteCategoryAsync(garden.Id, reassignTo: 2);

        Assert.All(await activities.GetSlotsAsync(), s => Assert.Equal(2, s.CategoryId));
        Assert.All(await activities.GetWeekAsync(Monday), a => Assert.Equal(2, a.CategoryId));
        Assert.Equal(2, (await tasks.GetAsync(task.Id))!.ActivityCategoryId);
    }

    [Fact]
    public async Task Supprimer_une_categorie_sans_rattachement_supprime_ses_creneaux_et_garde_les_taches()
    {
        await activities.SaveSlotAsync(new WeeklySlot { Day = DayOfWeek.Monday, Start = new(9, 0), End = new(10, 0), CategoryId = 8 });
        await activities.ApplyTemplateAsync(Monday);
        var task = new TaskItem { Title = "Trier les photos", ActivityCategoryId = 8 };
        await tasks.SaveAsync(task);

        await activities.DeleteCategoryAsync(8, null);

        Assert.Empty(await activities.GetSlotsAsync());
        Assert.Empty(await activities.GetWeekAsync(Monday));
        Assert.Null((await tasks.GetAsync(task.Id))!.ActivityCategoryId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => activities.DeleteCategoryAsync(4, null));   // a des sous-catégories
    }

    [Fact]
    public async Task Les_taches_ouvertes_d_une_categorie_d_activite_sont_proposees()
    {
        await tasks.SaveAsync(new TaskItem { Title = "Repeindre le portail", ActivityCategoryId = 2 });
        await tasks.SaveAsync(new TaskItem { Title = "Faite", ActivityCategoryId = 2, Status = TaskItemStatus.Done });
        await tasks.SaveAsync(new TaskItem { Title = "Aspirateur", ActivityCategoryId = 3 });

        var list = await tasks.GetOpenForActivityAsync([2]);
        Assert.Equal(["Repeindre le portail"], list.Select(t => t.Title));
    }
}
