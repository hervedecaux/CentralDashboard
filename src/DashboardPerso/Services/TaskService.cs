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
