using DashboardPerso.Data;
using DashboardPerso.Models;
using Microsoft.EntityFrameworkCore;

namespace DashboardPerso.Services;

/// <summary>Temps prévu (et fait) pour une catégorie principale sur une semaine.</summary>
public record ActivityTotal(ActivityCategory Category, TimeSpan Planned, TimeSpan Done);

/// <summary>Catégories d'activités, semaine type et planning des semaines réelles.</summary>
public class ActivityService(IDbContextFactory<AppDbContext> dbFactory)
{
    /// <summary>Lundi de la semaine qui contient le jour donné.</summary>
    public static DateOnly MondayOf(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    /// <summary>Lundi = 0 … dimanche = 6.</summary>
    public static int DayIndex(DayOfWeek day) => ((int)day + 6) % 7;

    public static DayOfWeek DayFromIndex(int index) => (DayOfWeek)((index + 1) % 7);

    // ---------- Catégories ----------

    /// <summary>Catégories triées : chaque principale suivie de ses sous-catégories (Parent chargé).</summary>
    public async Task<List<ActivityCategory>> GetCategoriesAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var all = await db.ActivityCategories.AsNoTracking().Include(c => c.Parent).ToListAsync();
        var result = new List<ActivityCategory>();
        foreach (var root in all.Where(c => c.ParentId is null).OrderBy(c => c.SortOrder).ThenBy(c => c.Name))
        {
            result.Add(root);
            result.AddRange(all.Where(c => c.ParentId == root.Id).OrderBy(c => c.SortOrder).ThenBy(c => c.Name));
        }
        return result;
    }

    /// <summary>Nombre de créneaux types, d'activités prévues et de tâches par catégorie.</summary>
    public async Task<Dictionary<int, (int Slots, int Activities, int Tasks)>> GetUsageAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var slots = await db.WeeklySlots.GroupBy(s => s.CategoryId).Select(g => new { g.Key, N = g.Count() }).ToListAsync();
        var acts = await db.PlannedActivities.GroupBy(s => s.CategoryId).Select(g => new { g.Key, N = g.Count() }).ToListAsync();
        var tasks = await db.Tasks.Where(t => t.ActivityCategoryId != null)
            .GroupBy(t => t.ActivityCategoryId!.Value).Select(g => new { g.Key, N = g.Count() }).ToListAsync();
        return slots.Select(x => x.Key).Union(acts.Select(x => x.Key)).Union(tasks.Select(x => x.Key))
            .ToDictionary(id => id, id => (
                slots.FirstOrDefault(x => x.Key == id)?.N ?? 0,
                acts.FirstOrDefault(x => x.Key == id)?.N ?? 0,
                tasks.FirstOrDefault(x => x.Key == id)?.N ?? 0));
    }

    public async Task SaveCategoryAsync(ActivityCategory category)
    {
        category.Name = category.Name.Trim();
        if (string.IsNullOrWhiteSpace(category.Name)) throw new InvalidOperationException("Le nom est obligatoire.");
        await using var db = await dbFactory.CreateDbContextAsync();
        if (category.ParentId is { } parentId)
        {
            if (parentId == category.Id) throw new InvalidOperationException("Une catégorie ne peut pas être sa propre catégorie principale.");
            var parent = await db.ActivityCategories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == parentId)
                         ?? throw new InvalidOperationException("Catégorie principale introuvable.");
            if (parent.ParentId is not null) throw new InvalidOperationException("Une sous-catégorie ne peut pas avoir de sous-catégories.");
            if (category.Id != 0 && await db.ActivityCategories.AnyAsync(c => c.ParentId == category.Id))
                throw new InvalidOperationException("Cette catégorie a des sous-catégories : elle doit rester une catégorie principale.");
        }
        var entity = new ActivityCategory
        {
            Id = category.Id, Name = category.Name, Color = category.Color, ParentId = category.ParentId,
            SortOrder = category.SortOrder,
        };
        if (entity.Id == 0)
        {
            var siblings = db.ActivityCategories.Where(c => c.ParentId == category.ParentId);
            entity.SortOrder = (await siblings.MaxAsync(c => (int?)c.SortOrder) ?? 0) + 1;
            db.ActivityCategories.Add(entity);
        }
        else
        {
            db.ActivityCategories.Update(entity);
        }
        await db.SaveChangesAsync();
        category.Id = entity.Id;
    }

    /// <summary>
    /// Supprime une catégorie sans sous-catégorie. Ses créneaux et activités sont rattachés à une autre
    /// catégorie, ou supprimés si aucune n'est choisie ; ses tâches perdent simplement leur type d'activité.
    /// </summary>
    public async Task DeleteCategoryAsync(int id, int? reassignTo)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.ActivityCategories.AnyAsync(c => c.ParentId == id))
            throw new InvalidOperationException("Supprimez ou déplacez d'abord ses sous-catégories.");
        if (reassignTo == id) reassignTo = null;
        await using var tx = await db.Database.BeginTransactionAsync();
        if (reassignTo is { } target)
        {
            await db.WeeklySlots.Where(s => s.CategoryId == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CategoryId, target));
            await db.PlannedActivities.Where(a => a.CategoryId == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CategoryId, target));
            await db.Tasks.Where(t => t.ActivityCategoryId == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ActivityCategoryId, target));
        }
        else
        {
            await db.PlannedActivities.Where(a => a.CategoryId == id).ExecuteDeleteAsync();
            await db.PlannedActivities.Where(a => a.Slot!.CategoryId == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.SlotId, (int?)null));
            await db.WeeklySlots.Where(s => s.CategoryId == id).ExecuteDeleteAsync();
            await db.Tasks.Where(t => t.ActivityCategoryId == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ActivityCategoryId, (int?)null));
        }
        await db.ActivityCategories.Where(c => c.Id == id).ExecuteDeleteAsync();
        await tx.CommitAsync();
    }

    // ---------- Semaine type ----------

    public async Task<List<WeeklySlot>> GetSlotsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var list = await db.WeeklySlots.AsNoTracking().Include(s => s.Category).ThenInclude(c => c!.Parent).ToListAsync();
        return list.OrderBy(s => DayIndex(s.Day)).ThenBy(s => s.Start).ToList();
    }

    /// <summary>Enregistre un créneau ; à la création, il peut être répété sur plusieurs jours.</summary>
    public async Task SaveSlotAsync(WeeklySlot slot, IEnumerable<DayOfWeek>? days = null)
    {
        Validate(slot.Start, slot.End);
        slot.Title = Clean(slot.Title);
        slot.Notes = Clean(slot.Notes);
        await using var db = await dbFactory.CreateDbContextAsync();
        if (slot.Id != 0)
        {
            db.WeeklySlots.Update(Copy(slot, slot.Day));
        }
        else
        {
            var targets = days?.Distinct().ToList() is { Count: > 0 } d ? d : [slot.Day];
            db.WeeklySlots.AddRange(targets.Select(day => Copy(slot, day)));
        }
        await db.SaveChangesAsync();
    }

    private static WeeklySlot Copy(WeeklySlot s, DayOfWeek day) => new()
    {
        Id = s.Id, Day = day, Start = s.Start, End = s.End, CategoryId = s.CategoryId, Title = s.Title, Notes = s.Notes,
    };

    /// <summary>Supprime un créneau type ; les activités déjà planifiées à partir de lui sont conservées.</summary>
    public async Task DeleteSlotAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.PlannedActivities.Where(a => a.SlotId == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.SlotId, (int?)null));
        await db.WeeklySlots.Where(s => s.Id == id).ExecuteDeleteAsync();
    }

    // ---------- Semaines réelles ----------

    /// <summary>Activités prévues du lundi donné au dimanche suivant.</summary>
    public async Task<List<PlannedActivity>> GetWeekAsync(DateOnly monday)
    {
        var end = monday.AddDays(7);
        await using var db = await dbFactory.CreateDbContextAsync();
        var list = await db.PlannedActivities.AsNoTracking()
            .Include(a => a.Category).ThenInclude(c => c!.Parent)
            .Where(a => a.Date >= monday && a.Date < end)
            .ToListAsync();
        return list.OrderBy(a => a.Date).ThenBy(a => a.Start).ToList();
    }

    public async Task<List<PlannedActivity>> GetDayAsync(DateOnly day)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var list = await db.PlannedActivities.AsNoTracking()
            .Include(a => a.Category).ThenInclude(c => c!.Parent)
            .Where(a => a.Date == day)
            .ToListAsync();
        return list.OrderBy(a => a.Start).ToList();
    }

    /// <summary>
    /// Copie les créneaux de la semaine type dans la semaine du lundi donné. Un créneau déjà copié
    /// sur cette semaine n'est pas recopié. Renvoie le nombre d'activités ajoutées.
    /// </summary>
    public async Task<int> ApplyTemplateAsync(DateOnly monday)
    {
        monday = MondayOf(monday);
        var end = monday.AddDays(7);
        await using var db = await dbFactory.CreateDbContextAsync();
        var slots = await db.WeeklySlots.AsNoTracking().ToListAsync();
        var existing = (await db.PlannedActivities
                .Where(a => a.Date >= monday && a.Date < end && a.SlotId != null)
                .Select(a => new { a.SlotId, a.Date })
                .ToListAsync())
            .Select(a => (a.SlotId!.Value, a.Date)).ToHashSet();
        var added = 0;
        foreach (var s in slots)
        {
            var date = monday.AddDays(DayIndex(s.Day));
            if (existing.Contains((s.Id, date))) continue;
            db.PlannedActivities.Add(new PlannedActivity
            {
                Date = date, Start = s.Start, End = s.End, CategoryId = s.CategoryId,
                Title = s.Title, Notes = s.Notes, SlotId = s.Id,
            });
            added++;
        }
        await db.SaveChangesAsync();
        return added;
    }

    /// <summary>Supprime toutes les activités de la semaine. Renvoie le nombre supprimé.</summary>
    public async Task<int> ClearWeekAsync(DateOnly monday)
    {
        monday = MondayOf(monday);
        var end = monday.AddDays(7);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.PlannedActivities.Where(a => a.Date >= monday && a.Date < end).ExecuteDeleteAsync();
    }

    public async Task SaveActivityAsync(PlannedActivity activity)
    {
        Validate(activity.Start, activity.End);
        await using var db = await dbFactory.CreateDbContextAsync();
        var entity = new PlannedActivity
        {
            Id = activity.Id, Date = activity.Date, Start = activity.Start, End = activity.End,
            CategoryId = activity.CategoryId, Title = Clean(activity.Title), Notes = Clean(activity.Notes),
            Done = activity.Done, SlotId = activity.SlotId,
        };
        if (entity.Id == 0) db.PlannedActivities.Add(entity);
        else db.PlannedActivities.Update(entity);
        await db.SaveChangesAsync();
        activity.Id = entity.Id;
    }

    public async Task SetDoneAsync(int id, bool done)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.PlannedActivities.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Done, done));
    }

    public async Task DeleteActivityAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.PlannedActivities.Where(a => a.Id == id).ExecuteDeleteAsync();
    }

    /// <summary>Temps par catégorie principale, dans l'ordre des catégories.</summary>
    public static List<ActivityTotal> Totals(IEnumerable<(ActivityCategory Category, TimeOnly Start, TimeOnly End, bool Done)> items,
        List<ActivityCategory> categories)
    {
        var byRoot = items
            .GroupBy(i => i.Category.RootId)
            .ToDictionary(g => g.Key, g => (
                Planned: TimeSpan.FromMinutes(g.Sum(i => (i.End - i.Start).TotalMinutes)),
                Done: TimeSpan.FromMinutes(g.Where(i => i.Done).Sum(i => (i.End - i.Start).TotalMinutes))));
        return categories.Where(c => c.ParentId is null && byRoot.ContainsKey(c.Id))
            .Select(c => new ActivityTotal(c, byRoot[c.Id].Planned, byRoot[c.Id].Done))
            .ToList();
    }

    /// <summary>« 3 h », « 1 h 30 », « 45 min ».</summary>
    public static string FormatDuration(TimeSpan d)
    {
        var minutes = (int)Math.Round(d.TotalMinutes);
        var (h, m) = (minutes / 60, minutes % 60);
        return h == 0 ? $"{m} min" : m == 0 ? $"{h} h" : $"{h} h {m:00}";
    }

    private static void Validate(TimeOnly start, TimeOnly end)
    {
        if (end <= start) throw new InvalidOperationException("L'heure de fin doit être après l'heure de début.");
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
