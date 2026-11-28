using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DashboardPerso.Services.Google;

// Format JSON de l'API Google Calendar v3 (seuls les champs utilisés).

public class GoogleEventTime
{
    /// <summary>Événement sur la journée entière : « 2026-11-28 ».</summary>
    [JsonPropertyName("date")] public string? Date { get; set; }

    /// <summary>Événement avec horaire : « 2026-11-28T09:00:00+01:00 ».</summary>
    [JsonPropertyName("dateTime")] public string? DateTime { get; set; }

    [JsonPropertyName("timeZone")] public string? TimeZone { get; set; }
}

public class GoogleExtendedProperties
{
    [JsonPropertyName("private")] public Dictionary<string, string>? Private { get; set; }
}

public class GoogleEvent
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("summary")] public string? Summary { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("location")] public string? Location { get; set; }
    [JsonPropertyName("htmlLink")] public string? HtmlLink { get; set; }
    [JsonPropertyName("start")] public GoogleEventTime? Start { get; set; }
    [JsonPropertyName("end")] public GoogleEventTime? End { get; set; }
    [JsonPropertyName("transparency")] public string? Transparency { get; set; }
    [JsonPropertyName("extendedProperties")] public GoogleExtendedProperties? ExtendedProperties { get; set; }
}

public class GoogleCalendarListEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("summary")] public string? Summary { get; set; }
    [JsonPropertyName("summaryOverride")] public string? SummaryOverride { get; set; }
    [JsonPropertyName("backgroundColor")] public string? BackgroundColor { get; set; }
    [JsonPropertyName("primary")] public bool Primary { get; set; }
    [JsonPropertyName("accessRole")] public string? AccessRole { get; set; }

    [JsonIgnore] public string Name => SummaryOverride ?? Summary ?? Id;
    [JsonIgnore] public bool CanWrite => AccessRole is "owner" or "writer";
}

file sealed class Page<T>
{
    [JsonPropertyName("items")] public List<T>? Items { get; set; }
    [JsonPropertyName("nextPageToken")] public string? NextPageToken { get; set; }
}

/// <summary>Appels à l'API Google Calendar v3, avec le jeton de <see cref="GoogleAuth"/>.</summary>
public class GoogleCalendarApi(GoogleAuth auth, IHttpClientFactory httpFactory)
{
    public const string BaseUrl = "https://www.googleapis.com/calendar/v3/";

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public async Task<List<GoogleCalendarListEntry>> ListCalendarsAsync(CancellationToken ct = default)
    {
        var result = new List<GoogleCalendarListEntry>();
        string? page = null;
        do
        {
            var url = "users/me/calendarList?maxResults=250" + (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page));
            var p = await SendAsync<Page<GoogleCalendarListEntry>>(HttpMethod.Get, url, null, ct);
            result.AddRange(p?.Items ?? []);
            page = p?.NextPageToken;
        } while (page is not null);
        return result;
    }

    /// <summary>
    /// Événements d'un agenda (récurrences développées). Sans bornes : tous les événements, utile
    /// avec <paramref name="privateProperties"/> pour retrouver un événement exporté.
    /// </summary>
    public async Task<List<GoogleEvent>> ListEventsAsync(string calendarId, DateTimeOffset? from, DateTimeOffset? to,
        IReadOnlyDictionary<string, string>? privateProperties = null, CancellationToken ct = default)
    {
        var query = new List<string> { "singleEvents=true", "maxResults=2500" };
        if (from is { } f) query.Add("timeMin=" + Uri.EscapeDataString(f.ToString("yyyy-MM-dd'T'HH:mm:sszzz")));
        if (to is { } t) query.Add("timeMax=" + Uri.EscapeDataString(t.ToString("yyyy-MM-dd'T'HH:mm:sszzz")));
        if (from is not null) query.Add("orderBy=startTime");
        foreach (var (k, v) in privateProperties ?? new Dictionary<string, string>())
            query.Add("privateExtendedProperty=" + Uri.EscapeDataString($"{k}={v}"));

        var result = new List<GoogleEvent>();
        string? page = null;
        do
        {
            var url = $"calendars/{Uri.EscapeDataString(calendarId)}/events?{string.Join("&", query)}"
                      + (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page));
            var p = await SendAsync<Page<GoogleEvent>>(HttpMethod.Get, url, null, ct);
            result.AddRange(p?.Items ?? []);
            page = p?.NextPageToken;
        } while (page is not null);
        return result;
    }

    public async Task<GoogleEvent> InsertEventAsync(string calendarId, GoogleEvent e, CancellationToken ct = default) =>
        (await SendAsync<GoogleEvent>(HttpMethod.Post, $"calendars/{Uri.EscapeDataString(calendarId)}/events", e, ct))!;

    /// <summary>Remplace l'événement (les champs absents sont effacés).</summary>
    public async Task<GoogleEvent> UpdateEventAsync(string calendarId, string eventId, GoogleEvent e, CancellationToken ct = default) =>
        (await SendAsync<GoogleEvent>(HttpMethod.Put,
            $"calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}", e, ct))!;

    /// <summary>Supprime l'événement ; ne fait rien s'il n'existe déjà plus.</summary>
    public async Task DeleteEventAsync(string calendarId, string eventId, CancellationToken ct = default) =>
        await SendAsync<object>(HttpMethod.Delete,
            $"calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}", null, ct, ignoreGone: true);

    private async Task<T?> SendAsync<T>(HttpMethod method, string url, object? body, CancellationToken ct, bool ignoreGone = false)
    {
        var token = await auth.GetAccessTokenAsync(ct);
        using var http = httpFactory.CreateClient(nameof(GoogleCalendarApi));
        using var request = new HttpRequestMessage(method, BaseUrl + url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: Json);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new GoogleApiException($"Google Agenda est injoignable ({ex.Message}).");
        }
        using (response)
        {
            if (ignoreGone && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return default;
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                auth.Reset();
                throw new GoogleNotConnectedException("Google a refusé l'accès : reconnectez-vous dans Réglages › Google Agenda.");
            }
            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                throw new GoogleApiException($"Erreur Google Agenda ({(int)response.StatusCode}) : {ExtractMessage(text)}");
            }
            if (response.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(object)) return default;
            return await response.Content.ReadFromJsonAsync<T>(Json, ct);
        }
    }

    private static string ExtractMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m))
                return m.GetString() ?? body;
        }
        catch (JsonException)
        {
        }
        return body.Length > 200 ? body[..200] : body;
    }
}
