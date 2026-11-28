using System.Globalization;
using DashboardPerso.Models;

namespace DashboardPerso.Services.Google;

/// <summary>
/// Un événement Google Agenda ramené à une journée du planning (un événement sur plusieurs jours
/// donne une entrée par jour). Start/End sont null pour un événement « journée entière ».
/// </summary>
public record CalendarEntry(
    string CalendarId, string CalendarName, string Color, string EventId,
    string Title, DateOnly Date, TimeOnly? Start, TimeOnly? End,
    string? Location, string? Description, string? Link, string? LocalKey)
{
    public bool AllDay => Start is null;
}

/// <summary>
/// Événements d'une période : ceux à afficher (y compris les éléments exportés, repérables par LocalKey)
/// et les clés des éléments exportés trouvés dans l'agenda d'export (task:12, activity:5).
/// </summary>
public record CalendarWeek(List<CalendarEntry> Entries, HashSet<string> ExportedKeys, string? Error = null);

/// <summary>Événement exporté retrouvé dans l'agenda d'export.</summary>
public record ExportedEvent(string CalendarId, string EventId, string? Link);

/// <summary>Lecture des agendas Google et export des tâches / activités choisies.</summary>
public class GoogleCalendarService(GoogleSettingsStore store, GoogleCalendarApi api, DatabaseFile database)
{
    /// <summary>Propriétés privées posées sur les événements exportés, pour les retrouver et les mettre à jour.</summary>
    public const string KeyProperty = "dashboardPerso";
    public const string BaseProperty = "dashboardPersoBase";

    public GoogleSettings Settings => store.Load();
    public bool IsConnected => store.Load().IsConnected;

    /// <summary>Base d'origine (dashboard, dashboard-dev) : les essais en développement ne touchent pas aux exports de la version installée.</summary>
    public string BaseTag => Path.GetFileNameWithoutExtension(database.Path);

    public static string TaskKey(int id) => $"task:{id}";
    public static string ActivityKey(int id) => $"activity:{id}";

    // ---------- Lecture ----------

    public Task<List<GoogleCalendarListEntry>> GetCalendarsAsync(CancellationToken ct = default) => api.ListCalendarsAsync(ct);

    /// <summary>Mémorise le compte (agenda principal) et, la première fois, affiche l'agenda principal.</summary>
    public async Task InitializeAfterConnectAsync(CancellationToken ct = default)
    {
        var calendars = await api.ListCalendarsAsync(ct);
        var primary = calendars.FirstOrDefault(c => c.Primary);
        store.Update(s =>
        {
            s.AccountEmail = primary?.Id;
            if (s.ShownCalendars.Count == 0 && primary is not null)
                s.ShownCalendars = [new GoogleCalendarChoice { Id = primary.Id, Name = primary.Name, Color = primary.BackgroundColor ?? "#4285f4" }];
        });
    }

    /// <summary>Événements des agendas affichés, du jour <paramref name="from"/> inclus à <paramref name="to"/> exclu.</summary>
    public async Task<CalendarWeek> GetEntriesAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var settings = store.Load();
        if (!settings.IsConnected) return new CalendarWeek([], []);
        var tz = TimeZoneInfo.Local;
        var start = ToOffset(from.ToDateTime(TimeOnly.MinValue), tz);
        var end = ToOffset(to.ToDateTime(TimeOnly.MinValue), tz);

        var entries = new List<CalendarEntry>();
        var keys = new HashSet<string>();
        var errors = new List<string>();
        var calendars = settings.ShownCalendars.ToList();
        var exportShown = calendars.Any(c => SameCalendar(c.Id, settings.ExportCalendarId, settings));
        if (!exportShown) calendars.Add(new GoogleCalendarChoice { Id = settings.ExportCalendarId, Name = "" });

        foreach (var cal in calendars)
        {
            List<GoogleEvent> events;
            try
            {
                events = await api.ListEventsAsync(cal.Id, start, end, ct: ct);
            }
            catch (GoogleNotConnectedException) { throw; }
            catch (GoogleApiException ex)
            {
                errors.Add(cal.Name.Length > 0 ? $"{cal.Name} : {ex.Message}" : ex.Message);
                continue;
            }
            var isExportCalendar = SameCalendar(cal.Id, settings.ExportCalendarId, settings);
            foreach (var e in events)
            {
                var key = LocalKey(e);
                if (key is not null && isExportCalendar) keys.Add(key);
                if (cal.Name.Length == 0) continue;   // agenda d'export non affiché : seulement pour les clés
                entries.AddRange(Expand(e, cal, from, to, tz, key));
            }
        }
        return new CalendarWeek(entries, keys, errors.Count == 0 ? null : string.Join(" · ", errors));
    }

    private static bool SameCalendar(string a, string b, GoogleSettings s) =>
        a == b || (b == "primary" && a == s.AccountEmail) || (a == "primary" && b == s.AccountEmail);

    /// <summary>Clé locale (task:12…) si l'événement a été exporté depuis cette base.</summary>
    private string? LocalKey(GoogleEvent e) =>
        e.ExtendedProperties?.Private is { } p && p.TryGetValue(KeyProperty, out var key)
        && p.TryGetValue(BaseProperty, out var b) && b == BaseTag ? key : null;

    /// <summary>Découpe un événement en entrées par jour, limitées à la période [from, to[.</summary>
    public static IEnumerable<CalendarEntry> Expand(GoogleEvent e, GoogleCalendarChoice cal, DateOnly from, DateOnly to,
        TimeZoneInfo tz, string? localKey = null)
    {
        if (e.Status == "cancelled" || e.Id is null || e.Start is null) yield break;
        var title = string.IsNullOrWhiteSpace(e.Summary) ? "(sans titre)" : e.Summary.Trim();
        CalendarEntry Entry(DateOnly d, TimeOnly? s, TimeOnly? en) =>
            new(cal.Id, cal.Name, cal.Color, e.Id!, title, d, s, en, e.Location, e.Description, e.HtmlLink, localKey);

        if (e.Start.Date is { } sd && DateOnly.TryParseExact(sd, "yyyy-MM-dd", out var startDay))
        {
            // Journée entière : la date de fin est exclue.
            var endDay = e.End?.Date is { } ed && DateOnly.TryParseExact(ed, "yyyy-MM-dd", out var x) && x > startDay ? x : startDay.AddDays(1);
            for (var d = Max(startDay, from); d < endDay && d < to; d = d.AddDays(1))
                yield return Entry(d, null, null);
            yield break;
        }

        if (!TryParseDateTime(e.Start.DateTime, tz, out var start)) yield break;
        if (!TryParseDateTime(e.End?.DateTime, tz, out var end) || end <= start) end = start.AddMinutes(30);

        for (var d = Max(DateOnly.FromDateTime(start), from); d < to; d = d.AddDays(1))
        {
            var dayStart = d.ToDateTime(TimeOnly.MinValue);
            var dayEnd = dayStart.AddDays(1);
            if (end <= dayStart) break;
            var s = start > dayStart ? start : dayStart;
            var en = end < dayEnd ? end : dayEnd;
            if (en <= s) continue;
            var endTime = en == dayEnd ? new TimeOnly(23, 59) : TimeOnly.FromDateTime(en);
            yield return Entry(d, TimeOnly.FromDateTime(s), endTime);
        }
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;

    private static bool TryParseDateTime(string? value, TimeZoneInfo tz, out DateTime local)
    {
        local = default;
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)) return false;
        local = TimeZoneInfo.ConvertTime(dto, tz).DateTime;
        return true;
    }

    // ---------- Export ----------

    /// <summary>Retrouve l'événement exporté pour une tâche ou une activité, dans l'agenda d'export.</summary>
    public async Task<ExportedEvent?> FindExportAsync(string key, CancellationToken ct = default)
    {
        var calendarId = store.Load().ExportCalendarId;
        var found = await api.ListEventsAsync(calendarId, null, null, KeyFilter(key), ct);
        var e = found.FirstOrDefault(x => x.Status != "cancelled" && x.Id is not null);
        return e is null ? null : new ExportedEvent(calendarId, e.Id!, e.HtmlLink);
    }

    /// <summary>Exporte une tâche comme événement « journée entière », le jour prévu (sinon l'échéance).</summary>
    public Task<ExportedEvent> ExportTaskAsync(TaskItem task, CancellationToken ct = default) =>
        UpsertAsync(TaskKey(task.Id), BuildTaskEvent(task), ct);

    /// <summary>Exporte une activité du planning, avec ses horaires.</summary>
    public Task<ExportedEvent> ExportActivityAsync(PlannedActivity activity, string categoryName, CancellationToken ct = default) =>
        UpsertAsync(ActivityKey(activity.Id), BuildActivityEvent(activity, categoryName, TimeZoneInfo.Local), ct);

    /// <summary>Met à jour l'événement s'il a déjà été exporté ; ne fait rien sinon.</summary>
    public async Task<bool> RefreshExportAsync(string key, GoogleEvent body, CancellationToken ct = default)
    {
        var existing = await FindExportAsync(key, ct);
        if (existing is null) return false;
        Tag(body, key);
        await api.UpdateEventAsync(existing.CalendarId, existing.EventId, body, ct);
        return true;
    }

    /// <summary>Retire de Google Agenda l'événement exporté (s'il existe).</summary>
    public async Task<bool> RemoveExportAsync(string key, CancellationToken ct = default)
    {
        var existing = await FindExportAsync(key, ct);
        if (existing is null) return false;
        await api.DeleteEventAsync(existing.CalendarId, existing.EventId, ct);
        return true;
    }

    private async Task<ExportedEvent> UpsertAsync(string key, GoogleEvent body, CancellationToken ct)
    {
        Tag(body, key);
        var existing = await FindExportAsync(key, ct);
        var calendarId = store.Load().ExportCalendarId;
        var saved = existing is null
            ? await api.InsertEventAsync(calendarId, body, ct)
            : await api.UpdateEventAsync(existing.CalendarId, existing.EventId, body, ct);
        return new ExportedEvent(existing?.CalendarId ?? calendarId, saved.Id ?? existing?.EventId ?? "", saved.HtmlLink);
    }

    private void Tag(GoogleEvent e, string key) =>
        e.ExtendedProperties = new GoogleExtendedProperties { Private = new() { [KeyProperty] = key, [BaseProperty] = BaseTag } };

    private Dictionary<string, string> KeyFilter(string key) => new() { [KeyProperty] = key, [BaseProperty] = BaseTag };

    /// <summary>Jour de la tâche dans l'agenda : prévue pour, sinon échéance.</summary>
    public static DateOnly? TaskDate(TaskItem task) => task.PlannedFor ?? task.DueDate;

    public static GoogleEvent BuildTaskEvent(TaskItem task)
    {
        var day = TaskDate(task) ?? throw new InvalidOperationException(
            "Choisissez d'abord un jour (« Prévue pour ») ou une échéance pour placer la tâche dans l'agenda.");
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(task.Description)) lines.Add(task.Description.Trim());
        if (task.PlannedFor is not null && task.DueDate is { } due && due != day) lines.Add($"Échéance : {due:dddd d MMMM yyyy}");
        lines.Add("Tâche exportée depuis Dashboard perso.");
        return new GoogleEvent
        {
            Summary = (task.Status == TaskItemStatus.Done ? "✔ " : "") + task.Title.Trim(),
            Description = string.Join("\n\n", lines),
            Start = new GoogleEventTime { Date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
            End = new GoogleEventTime { Date = day.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
            Transparency = "transparent",   // n'apparaît pas comme « occupé »
        };
    }

    public static GoogleEvent BuildActivityEvent(PlannedActivity a, string categoryName, TimeZoneInfo tz)
    {
        var title = string.IsNullOrWhiteSpace(a.Title) ? categoryName : $"{a.Title.Trim()} ({categoryName})";
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(a.Notes)) lines.Add(a.Notes.Trim());
        lines.Add("Activité exportée depuis Dashboard perso.");
        var zone = IanaId(tz);
        return new GoogleEvent
        {
            Summary = (a.Done ? "✔ " : "") + title,
            Description = string.Join("\n\n", lines),
            Start = new GoogleEventTime { DateTime = Format(a.Date.ToDateTime(a.Start), tz), TimeZone = zone },
            End = new GoogleEventTime { DateTime = Format(a.Date.ToDateTime(a.End), tz), TimeZone = zone },
        };
    }

    private static DateTimeOffset ToOffset(DateTime local, TimeZoneInfo tz) => new(local, tz.GetUtcOffset(local));

    private static string Format(DateTime local, TimeZoneInfo tz) =>
        ToOffset(local, tz).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary>Fuseau au format attendu par Google (Europe/Paris), ou null si inconnu.</summary>
    private static string? IanaId(TimeZoneInfo tz)
    {
        if (tz.HasIanaId) return tz.Id == "UTC" ? "Etc/UTC" : tz.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(tz.Id, out var iana) ? iana : null;
    }
}
