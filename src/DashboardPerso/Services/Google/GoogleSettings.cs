using System.Text.Json;
using System.Text.Json.Serialization;

namespace DashboardPerso.Services.Google;

/// <summary>Agenda Google affiché dans le planning.</summary>
public class GoogleCalendarChoice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#4285f4";
}

/// <summary>
/// Réglages de la connexion à Google Agenda : identifiants OAuth du projet Google Cloud de l'utilisateur,
/// jeton de rafraîchissement et agendas choisis. Enregistrés dans un fichier à part, à côté de la base
/// (jamais dans le dépôt ni dans les sauvegardes de la base).
/// </summary>
public class GoogleSettings
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? RefreshToken { get; set; }

    /// <summary>Adresse du compte connecté (identifiant de son agenda principal).</summary>
    public string? AccountEmail { get; set; }

    public DateTime? ConnectedAt { get; set; }

    /// <summary>Agendas dont les événements apparaissent dans le planning.</summary>
    public List<GoogleCalendarChoice> ShownCalendars { get; set; } = [];

    /// <summary>Agenda dans lequel les tâches et activités sont exportées (« primary » : l'agenda principal).</summary>
    public string ExportCalendarId { get; set; } = "primary";

    [JsonIgnore] public bool HasCredentials => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
    [JsonIgnore] public bool IsConnected => HasCredentials && !string.IsNullOrWhiteSpace(RefreshToken);
}

/// <summary>Lecture et écriture du fichier de réglages Google (google-agenda.json).</summary>
public class GoogleSettingsStore
{
    public const string FileName = "google-agenda.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object gate = new();
    private GoogleSettings? cached;

    public GoogleSettingsStore(string path) => FilePath = path;

    public string FilePath { get; }

    /// <summary>Une copie des réglages (à modifier puis passer à <see cref="Save"/>).</summary>
    public GoogleSettings Load()
    {
        lock (gate)
        {
            cached ??= Read();
            return Clone(cached);
        }
    }

    public void Save(GoogleSettings settings)
    {
        lock (gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
            // Fichier lisible par l'utilisateur seul : il contient le secret client et le jeton Google.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tmp, FilePath, overwrite: true);
            cached = Clone(settings);
        }
    }

    /// <summary>Applique une modification et enregistre.</summary>
    public GoogleSettings Update(Action<GoogleSettings> change)
    {
        lock (gate)
        {
            var s = Load();
            change(s);
            Save(s);
            return s;
        }
    }

    private GoogleSettings Read()
    {
        if (!File.Exists(FilePath)) return new GoogleSettings();
        try
        {
            return JsonSerializer.Deserialize<GoogleSettings>(File.ReadAllText(FilePath), Json) ?? new GoogleSettings();
        }
        catch (JsonException)
        {
            return new GoogleSettings();
        }
    }

    private static GoogleSettings Clone(GoogleSettings s) =>
        JsonSerializer.Deserialize<GoogleSettings>(JsonSerializer.Serialize(s, Json), Json)!;

    /// <summary>
    /// Lit le fichier JSON téléchargé depuis Google Cloud (« client_secret_….json ») : renvoie l'ID et le secret client.
    /// </summary>
    public static (string ClientId, string ClientSecret)? ParseClientSecretFile(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var kind in new[] { "installed", "web" })
            {
                if (doc.RootElement.TryGetProperty(kind, out var c)
                    && c.TryGetProperty("client_id", out var id) && c.TryGetProperty("client_secret", out var secret)
                    && id.GetString() is { Length: > 0 } i && secret.GetString() is { Length: > 0 } s)
                    return (i, s);
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }
}
