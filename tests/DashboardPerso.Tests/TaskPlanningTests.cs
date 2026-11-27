using DashboardPerso.Data;
using DashboardPerso.Models;
using DashboardPerso.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DashboardPerso.Tests;

public sealed class TaskPlanningTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly TestDbFactory factory;
    private readonly TaskService tasks;
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    public TaskPlanningTests()
    {
        connection.Open();
        factory = new TestDbFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        using var db = factory.CreateDbContext();
        db.Database.Migrate();
        tasks = new TaskService(factory);
    }

    public void Dispose() => connection.Dispose();

    private async Task<TaskItem> Add(string title, DateOnly? planned = null, TaskItemStatus status = TaskItemStatus.Todo,
        DateTime? completedAt = null)
    {
        var t = new TaskItem { Title = title, PlannedFor = planned, Status = status, CompletedAt = completedAt };
        await tasks.SaveAsync(t);
        return t;
    }

    [Fact]
    public async Task La_liste_du_jour_contient_les_taches_prevues_reportees_et_terminees_aujourd_hui()
    {
        await Add("Prévue aujourd'hui", Today);
        await Add("Reportée d'hier", Today.AddDays(-1));
        await Add("Faite aujourd'hui", Today, TaskItemStatus.Done, DateTime.Now);
        await Add("Faite hier", Today.AddDays(-1), TaskItemStatus.Done, DateTime.Now.AddDays(-1));
        await Add("Demain", Today.AddDays(1));
        await Add("Non planifiée");

        var day = await tasks.GetDayListAsync(Today);
        Assert.Equal(["Faite aujourd'hui", "Prévue aujourd'hui", "Reportée d'hier"], day.Select(t => t.Title).Order().ToList());
        Assert.True(day.Single(t => t.Title == "Reportée d'hier").IsCarriedOver);
        Assert.Equal("Faite aujourd'hui", day[^1].Title);                         // terminées en dernier

        Assert.Equal(["Demain"], (await tasks.GetPlannedForAsync(Today.AddDays(1))).Select(t => t.Title));
        Assert.Equal(["Non planifiée"], (await tasks.GetUnplannedAsync(Today)).Select(t => t.Title));
    }

    [Fact]
    public async Task Planifier_retirer_et_reporter_a_demain()
    {
        var a = await Add("A");
        var b = await Add("B");
        await tasks.PlanAsync(a.Id, Today);
        await tasks.PlanAsync(b.Id, Today);
        await tasks.SetStatusAsync(a.Id, TaskItemStatus.Done);

        Assert.Equal(1, await tasks.CarryOverToTomorrowAsync(Today));             // seule B, non faite
        Assert.Equal(["B"], (await tasks.GetPlannedForAsync(Today.AddDays(1))).Select(t => t.Title));
        Assert.Equal(["A"], (await tasks.GetDayListAsync(Today)).Select(t => t.Title));

        await tasks.PlanAsync(b.Id, null);
        Assert.Null((await tasks.GetAsync(b.Id))!.PlannedFor);

        var quick = await tasks.QuickAddAsync("  Appeler le garage ", Today.AddDays(1));
        Assert.Equal("Appeler le garage", quick.Title);
        Assert.Contains((await tasks.GetPlannedForAsync(Today.AddDays(1))), t => t.Id == quick.Id);
    }
}
