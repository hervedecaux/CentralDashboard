using System.Net;
using System.Text;
using System.Text.Json;
using DashboardPerso.Models;
using DashboardPerso.Services;
using DashboardPerso.Services.Google;

namespace DashboardPerso.Tests;

public sealed class GoogleCalendarTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "dp-google-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHttp http = new();
    private readonly GoogleSettingsStore store;
    private readonly GoogleAuth auth;
    private readonly GoogleCalendarService service;

    // Fuseau fixe pour des tests indépendants de la machine : UTC+1, sans heure d'été.
    private static readonly TimeZoneInfo Paris = TimeZoneInfo.CreateCustomTimeZone("Test+1", TimeSpan.FromHours(1), "Test+1", "Test+1");

    public GoogleCalendarTests()
    {
        store = new GoogleSettingsStore(Path.Combine(dir, GoogleSettingsStore.FileName));
        auth = new GoogleAuth(store, http, TimeProvider.System);
        service = new GoogleCalendarService(store, new GoogleCalendarApi(auth, http), new DatabaseFile(Path.Combine(dir, "dashboard.db")));
    }

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private void Connect() => store.Save(new GoogleSettings
    {
        ClientId = "id.apps.googleusercontent.com", ClientSecret = "secret", RefreshToken = "refresh",
        AccountEmail = "moi@example.com",
        ShownCalendars = [new GoogleCalendarChoice { Id = "moi@example.com", Name = "Perso", Color = "#123456" }],
    });

    private void TokenOk() => http.On(HttpMethod.Post, GoogleAuth.TokenEndpoint,
        """{"access_token":"tok","expires_in":3600,"token_type":"Bearer"}""");

    // ---------- Réglages ----------

    [Fact]
    public void Settings_are_saved_and_reloaded_privately()
    {
        Connect();
        var reloaded = new GoogleSettingsStore(store.FilePath).Load();
        Assert.True(reloaded.IsConnected);
        Assert.Equal("Perso", reloaded.ShownCalendars.Single().Name);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.FilePath));
    }

    [Fact]
    public void Client_secret_file_from_google_cloud_is_parsed()
    {
        var json = """{"installed":{"client_id":"abc.apps.googleusercontent.com","project_id":"p","client_secret":"GOCSPX-x","redirect_uris":["http://localhost"]}}""";
        Assert.Equal(("abc.apps.googleusercontent.com", "GOCSPX-x"), GoogleSettingsStore.ParseClientSecretFile(json));
        Assert.Null(GoogleSettingsStore.ParseClientSecretFile("""{"type":"service_account"}"""));
        Assert.Null(GoogleSettingsStore.ParseClientSecretFile("pas du json"));
    }

    // ---------- Connexion OAuth ----------

    [Fact]
    public async Task Authorization_uses_pkce_and_exchanges_the_code()
    {
        store.Save(new GoogleSettings { ClientId = "id.apps.googleusercontent.com", ClientSecret = "secret" });
        var url = new Uri(auth.BuildAuthorizeUrl("http://localhost:5080/"));
        var q = System.Web.HttpUtility.ParseQueryString(url.Query);
        Assert.Equal("http://localhost:5080/google/retour", q["redirect_uri"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal("offline", q["access_type"]);
        Assert.Contains("calendar.events", q["scope"]);

        http.On(HttpMethod.Post, GoogleAuth.TokenEndpoint,
            """{"access_token":"tok","expires_in":3600,"refresh_token":"r1","scope":"https://www.googleapis.com/auth/calendar.calendarlist.readonly https://www.googleapis.com/auth/calendar.events"}""");
        await auth.CompleteAsync(q["state"], "code123");

        Assert.Equal("r1", store.Load().RefreshToken);
        var form = System.Web.HttpUtility.ParseQueryString(http.Requests.Single().Body!);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal(q["code_challenge"], GoogleAuth.Challenge(form["code_verifier"]!));
        Assert.Equal("http://localhost:5080/google/retour", form["redirect_uri"]);

        // Un état déjà utilisé (ou inconnu) est refusé.
        await Assert.ThrowsAsync<GoogleApiException>(() => auth.CompleteAsync(q["state"], "code123"));
    }

    [Fact]
    public async Task Missing_calendar_permission_is_reported()
    {
        store.Save(new GoogleSettings { ClientId = "id.apps.googleusercontent.com", ClientSecret = "secret" });
        var q = System.Web.HttpUtility.ParseQueryString(new Uri(auth.BuildAuthorizeUrl("http://localhost:5080/")).Query);
        http.On(HttpMethod.Post, GoogleAuth.TokenEndpoint,
            """{"access_token":"tok","expires_in":3600,"refresh_token":"r1","scope":"https://www.googleapis.com/auth/calendar.calendarlist.readonly"}""");
        await Assert.ThrowsAsync<GoogleApiException>(() => auth.CompleteAsync(q["state"], "code"));
        Assert.Null(store.Load().RefreshToken);
    }

    [Fact]
    public async Task Revoked_refresh_token_disconnects()
    {
        Connect();
        http.On(HttpMethod.Post, GoogleAuth.TokenEndpoint, """{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
        await Assert.ThrowsAsync<GoogleNotConnectedException>(() => auth.GetAccessTokenAsync());
        Assert.False(store.Load().IsConnected);
    }

    [Fact]
    public async Task Access_token_is_reused_until_it_expires()
    {
        Connect();
        TokenOk();
        Assert.Equal("tok", await auth.GetAccessTokenAsync());
        Assert.Equal("tok", await auth.GetAccessTokenAsync());
        Assert.Single(http.Requests);
    }

    // ---------- Lecture des événements ----------

    private static readonly GoogleCalendarChoice Cal = new() { Id = "c", Name = "Perso", Color = "#123456" };
    private static readonly DateOnly Monday = new(2026, 11, 23);

    [Fact]
    public void Timed_event_is_converted_to_local_time()
    {
        var e = new GoogleEvent
        {
            Id = "e1", Summary = "Dentiste",
            Start = new GoogleEventTime { DateTime = "2026-11-24T08:30:00Z" },
            End = new GoogleEventTime { DateTime = "2026-11-24T09:15:00Z" },
        };
        var entry = Assert.Single(GoogleCalendarService.Expand(e, Cal, Monday, Monday.AddDays(7), Paris));
        Assert.Equal(new DateOnly(2026, 11, 24), entry.Date);
        Assert.Equal(new TimeOnly(9, 30), entry.Start);
        Assert.Equal(new TimeOnly(10, 15), entry.End);
        Assert.False(entry.AllDay);
    }

    [Fact]
    public void All_day_event_over_several_days_gives_one_entry_per_day_within_the_week()
    {
        var e = new GoogleEvent
        {
            Id = "e2", Summary = "Vacances",
            Start = new GoogleEventTime { Date = "2026-11-28" },
            End = new GoogleEventTime { Date = "2026-12-02" },   // fin exclue
        };
        var entries = GoogleCalendarService.Expand(e, Cal, Monday, Monday.AddDays(7), Paris).ToList();
        Assert.Equal([new DateOnly(2026, 11, 28), new DateOnly(2026, 11, 29)], entries.Select(x => x.Date));
        Assert.All(entries, x => Assert.True(x.AllDay));
    }

    [Fact]
    public void Overnight_event_is_split_and_cancelled_event_ignored()
    {
        var e = new GoogleEvent
        {
            Id = "e3", Summary = "Train de nuit",
            Start = new GoogleEventTime { DateTime = "2026-11-25T22:00:00+01:00" },
            End = new GoogleEventTime { DateTime = "2026-11-26T06:00:00+01:00" },
        };
        var parts = GoogleCalendarService.Expand(e, Cal, Monday, Monday.AddDays(7), Paris).ToList();
        Assert.Equal(2, parts.Count);
        Assert.Equal((new TimeOnly(22, 0), new TimeOnly(23, 59)), (parts[0].Start!.Value, parts[0].End!.Value));
        Assert.Equal((new TimeOnly(0, 0), new TimeOnly(6, 0)), (parts[1].Start!.Value, parts[1].End!.Value));

        e.Status = "cancelled";
        Assert.Empty(GoogleCalendarService.Expand(e, Cal, Monday, Monday.AddDays(7), Paris));
    }

    [Fact]
    public async Task Week_entries_flag_items_exported_from_this_database_only()
    {
        Connect();
        TokenOk();
        var mine = new Dictionary<string, string> { ["dashboardPerso"] = "activity:5", ["dashboardPersoBase"] = "dashboard" };
        var dev = new Dictionary<string, string> { ["dashboardPerso"] = "activity:6", ["dashboardPersoBase"] = "dashboard-dev" };
        http.On(HttpMethod.Get, "calendars/moi%40example.com/events", JsonSerializer.Serialize(new
        {
            items = new object[]
            {
                new { id = "a", summary = "Exportée", start = new { dateTime = "2026-11-24T09:00:00+01:00" }, end = new { dateTime = "2026-11-24T10:00:00+01:00" }, extendedProperties = new { @private = mine } },
                new { id = "b", summary = "Exportée en dev", start = new { dateTime = "2026-11-24T11:00:00+01:00" }, end = new { dateTime = "2026-11-24T12:00:00+01:00" }, extendedProperties = new { @private = dev } },
                new { id = "c", summary = "Anniversaire", start = new { date = "2026-11-27" }, end = new { date = "2026-11-28" } },
            },
        }));

        var week = await service.GetEntriesAsync(Monday, Monday.AddDays(7));

        Assert.Equal(["activity:5"], week.ExportedKeys);
        Assert.Equal(3, week.Entries.Count);
        Assert.Equal("activity:5", week.Entries.Single(x => x.EventId == "a").LocalKey);
        Assert.Null(week.Entries.Single(x => x.EventId == "b").LocalKey);
        // L'agenda principal est à la fois affiché et agenda d'export : une seule requête d'événements.
        Assert.Single(http.Requests, r => r.Method == HttpMethod.Get);
    }

    // ---------- Export ----------

    [Fact]
    public void Task_event_is_all_day_on_planned_day_or_due_date()
    {
        var task = new TaskItem { Id = 3, Title = " Appeler le plombier ", DueDate = new DateOnly(2026, 12, 4) };
        var e = GoogleCalendarService.BuildTaskEvent(task);
        Assert.Equal("Appeler le plombier", e.Summary);
        Assert.Equal(("2026-12-04", "2026-12-05"), (e.Start!.Date, e.End!.Date));
        Assert.Null(e.Start.DateTime);

        task.PlannedFor = new DateOnly(2026, 12, 1);
        task.Status = TaskItemStatus.Done;
        e = GoogleCalendarService.BuildTaskEvent(task);
        Assert.Equal("2026-12-01", e.Start!.Date);
        Assert.StartsWith("✔ ", e.Summary);
        Assert.Contains("Échéance", e.Description);

        Assert.Throws<InvalidOperationException>(() => GoogleCalendarService.BuildTaskEvent(new TaskItem { Title = "Sans date" }));
    }

    [Fact]
    public void Activity_event_keeps_local_times_with_offset()
    {
        var a = new PlannedActivity { Id = 5, Date = new DateOnly(2026, 11, 24), Start = new TimeOnly(9, 0), End = new TimeOnly(10, 30), Title = "Piano" };
        var e = GoogleCalendarService.BuildActivityEvent(a, "Musique", Paris);
        Assert.Equal("Piano (Musique)", e.Summary);
        Assert.Equal("2026-11-24T09:00:00+01:00", e.Start!.DateTime);
        Assert.Equal("2026-11-24T10:30:00+01:00", e.End!.DateTime);
    }

    [Fact]
    public async Task Export_inserts_then_updates_the_same_event()
    {
        Connect();
        TokenOk();
        var task = new TaskItem { Id = 12, Title = "Vidange", PlannedFor = new DateOnly(2026, 12, 3) };

        http.On(HttpMethod.Get, "calendars/primary/events", """{"items":[]}""");
        http.On(HttpMethod.Post, "calendars/primary/events", """{"id":"ev1","htmlLink":"https://calendar.google.com/x"}""");
        var first = await service.ExportTaskAsync(task);
        Assert.Equal("ev1", first.EventId);
        var search = http.Requests.First(r => r.Method == HttpMethod.Get).Url;
        Assert.Contains("privateExtendedProperty=dashboardPerso%3Dtask%3A12", search);
        Assert.Contains("privateExtendedProperty=dashboardPersoBase%3Ddashboard", search);
        var posted = JsonDocument.Parse(http.Requests.Single(r => r.Method == HttpMethod.Post && r.Url.Contains("/events")).Body!).RootElement;
        Assert.Equal("task:12", posted.GetProperty("extendedProperties").GetProperty("private").GetProperty("dashboardPerso").GetString());

        http.Requests.Clear();
        http.On(HttpMethod.Get, "calendars/primary/events", """{"items":[{"id":"ev1","status":"confirmed"}]}""");
        http.On(HttpMethod.Put, "calendars/primary/events/ev1", """{"id":"ev1"}""");
        await service.ExportTaskAsync(task);
        Assert.Contains(http.Requests, r => r.Method == HttpMethod.Put);
        Assert.DoesNotContain(http.Requests, r => r.Method == HttpMethod.Post && r.Url.Contains("/events"));

        http.On(HttpMethod.Delete, "calendars/primary/events/ev1", "", HttpStatusCode.NoContent);
        Assert.True(await service.RemoveExportAsync(GoogleCalendarService.TaskKey(12)));
    }

    // ---------- Faux serveur HTTP ----------

    private sealed record Sent(HttpMethod Method, string Url, string? Body);

    private sealed class FakeHttp : HttpMessageHandler, IHttpClientFactory
    {
        private readonly List<(HttpMethod Method, string UrlPart, string Body, HttpStatusCode Status)> routes = [];
        public List<Sent> Requests { get; } = [];

        public void On(HttpMethod method, string urlPart, string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            routes.RemoveAll(r => r.Method == method && r.UrlPart == urlPart);
            routes.Insert(0, (method, urlPart, body, status));
        }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new Sent(request.Method, url, body));
            // La route la plus précise (la plus longue) l'emporte.
            var route = routes.Where(r => r.Method == request.Method && url.Contains(r.UrlPart))
                .OrderByDescending(r => r.UrlPart.Length).FirstOrDefault();
            if (route.UrlPart is null) return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
            return new HttpResponseMessage(route.Status) { Content = new StringContent(route.Body, Encoding.UTF8, "application/json") };
        }
    }
}
