using DashboardPerso.Data;
using DashboardPerso.Models;
using Microsoft.EntityFrameworkCore;

namespace DashboardPerso.Services;

public class TaskService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<List<TaskItem>> GetAllAsync(TaskItemStatus? status = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var query = db.Tasks.AsNoTracking();
        if (status is not null) query = query.Where(t => t.Status == status);
        var list = await query.ToListAsync();
        // Tri : non terminées d'abord, puis échéance, puis priorité.
        return list
            .OrderBy(t => t.Status == TaskItemStatus.Done)
            .ThenBy(t => t.DueDate ?? DateOnly.MaxValue)
            .ThenByDescending(t => t.Priority)
            .ToList();
    }

    /// <summary>Tâches non terminées, triées par échéance.</summary>
    public async Task<List<TaskItem>> GetUpcomingAsync(int count)
    {
        var open = await GetAllAsync();
        return open.Where(t => t.Status != TaskItemStatus.Done).Take(count).ToList();
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    /// <summary>
    /// Liste du jour : tâches ouvertes prévues aujourd'hui ou un jour passé (reportées),
    /// plus celles de la liste terminées aujourd'hui, pour voir l'avancement.
    /// </summary>
    public async Task<List<TaskItem>> GetDayListAsync(DateOnly? today = null)
    {
        var day = today ?? Today;
        var start = day.ToDateTime(TimeOnly.MinValue);
        var end = start.AddDays(1);
        await using var db = await dbFactory.CreateDbContextAsync();
        var list = await db.Tasks.AsNoTracking()
            .Where(t => t.PlannedFor != null && t.PlannedFor <= day
                        && (t.Status != TaskItemStatus.Done || (t.CompletedAt >= start && t.CompletedAt < end)))
            .ToListAsync();
        return Sort(list);
    }

    /// <summary>Tâches prévues un jour donné (ex. demain).</summary>
    public async Task<List<TaskItem>> GetPlannedForAsync(DateOnly day)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return Sort(await db.Tasks.AsNoTracking().Where(t => t.PlannedFor == day).ToListAsync());
    }

    /// <summary>Tâches ouvertes qui ne sont pas encore prévues aujourd'hui ni demain : à planifier.</summary>
    public async Task<List<TaskItem>> GetUnplannedAsync(DateOnly? today = null)
    {
        var tomorrow = (today ?? Today).AddDays(1);
        await using var db = await dbFactory.CreateDbContextAsync();
        return Sort(await db.Tasks.AsNoTracking()
            .Where(t => t.Status != TaskItemStatus.Done && (t.PlannedFor == null || t.PlannedFor > tomorrow))
            .ToListAsync());
    }

    /// <summary>Tâches non terminées rattachées à l'une des catégories d'activité données.</summary>
    public async Task<List<TaskItem>> GetOpenForActivityAsync(IReadOnlyCollection<int> categoryIds)
    {
        if (categoryIds.Count == 0) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        return Sort(await db.Tasks.AsNoTracking()
            .Where(t => t.Status != TaskItemStatus.Done && t.ActivityCategoryId != null && categoryIds.Contains(t.ActivityCategoryId.Value))
            .ToListAsync());
    }

    /// <summary>Prévoit la tâche pour un jour (null : la retire des listes du jour).</summary>
    public async Task PlanAsync(int id, DateOnly? day)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Tasks.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.PlannedFor, day));
    }

    /// <summary>Reporte au lendemain les tâches non terminées de la liste du jour. Renvoie le nombre reporté.</summary>
    public async Task<int> CarryOverToTomorrowAsync(DateOnly? today = null)
    {
        var day = today ?? Today;
        var tomorrow = day.AddDays(1);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Tasks.Where(t => t.Status != TaskItemStatus.Done && t.PlannedFor != null && t.PlannedFor <= day)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.PlannedFor, tomorrow));
    }

    /// <summary>Crée rapidement une tâche prévue pour un jour.</summary>
    public async Task<TaskItem> QuickAddAsync(string title, DateOnly day)
    {
        var task = new TaskItem { Title = title.Trim(), PlannedFor = day };
        await SaveAsync(task);
        return task;
    }

    /// <summary>Non terminées d'abord, puis priorité, puis échéance.</summary>
    private static List<TaskItem> Sort(List<TaskItem> list) => list
        .OrderBy(t => t.Status == TaskItemStatus.Done)
        .ThenByDescending(t => t.Priority)
        .ThenBy(t => t.DueDate ?? DateOnly.MaxValue)
        .ThenBy(t => t.Id)
        .ToList();

    public async Task<TaskItem?> GetAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);
    }

    public async Task SaveAsync(TaskItem task)
    {
        task.CompletedAt = task.Status == TaskItemStatus.Done ? task.CompletedAt ?? DateTime.Now : null;
        await using var db = await dbFactory.CreateDbContextAsync();
        if (task.Id == 0) db.Tasks.Add(task);
        else db.Tasks.Update(task);
        await db.SaveChangesAsync();
    }

    public async Task SetStatusAsync(int id, TaskItemStatus status)
    {
        var task = await GetAsync(id);
        if (task is null) return;
        task.Status = status;
        await SaveAsync(task);
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Tasks.Where(t => t.Id == id).ExecuteDeleteAsync();
    }
}
