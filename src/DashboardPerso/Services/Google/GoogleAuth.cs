using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace DashboardPerso.Services.Google;

/// <summary>La connexion Google n'est pas (ou plus) valide : il faut se reconnecter depuis Réglages › Google Agenda.</summary>
public class GoogleNotConnectedException(string message) : Exception(message);

/// <summary>Erreur renvoyée par l'API Google.</summary>
public class GoogleApiException(string message) : Exception(message);

/// <summary>
/// Connexion OAuth 2.0 à Google (application de bureau, retour sur http://localhost) avec PKCE,
/// et jeton d'accès renouvelé automatiquement à partir du jeton de rafraîchissement.
/// </summary>
public class GoogleAuth(GoogleSettingsStore store, IHttpClientFactory httpFactory, TimeProvider clock)
{
    public const string AuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    public const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";

    /// <summary>Lire la liste des agendas, et lire / créer / modifier des événements.</summary>
    public const string Scopes =
        "https://www.googleapis.com/auth/calendar.calendarlist.readonly https://www.googleapis.com/auth/calendar.events";

    public const string CallbackPath = "/google/retour";

    private readonly ConcurrentDictionary<string, (string Verifier, string RedirectUri, DateTimeOffset Expires)> pending = new();
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private (string Token, DateTimeOffset Expires)? access;

    /// <summary>Adresse de la page Google de consentement. <paramref name="baseUri"/> : ex. http://localhost:5080/.</summary>
    public string BuildAuthorizeUrl(string baseUri)
    {
        var settings = store.Load();
        if (!settings.HasCredentials) throw new GoogleNotConnectedException("Renseignez d'abord l'ID client et le secret client.");

        foreach (var (key, value) in pending)
            if (value.Expires < clock.GetUtcNow()) pending.TryRemove(key, out _);

        var state = RandomToken();
        var verifier = RandomToken() + RandomToken();
        var redirect = baseUri.TrimEnd('/') + CallbackPath;
        pending[state] = (verifier, redirect, clock.GetUtcNow().AddMinutes(15));

        var query = new Dictionary<string, string>
        {
            ["client_id"] = settings.ClientId!,
            ["redirect_uri"] = redirect,
            ["response_type"] = "code",
            ["scope"] = Scopes,
            ["access_type"] = "offline",
            ["prompt"] = "consent",          // pour toujours recevoir un jeton de rafraîchissement
            ["include_granted_scopes"] = "true",
            ["state"] = state,
            ["code_challenge"] = Challenge(verifier),
            ["code_challenge_method"] = "S256",
        };
        return AuthorizeEndpoint + "?" + string.Join("&", query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    /// <summary>Échange le code reçu au retour de Google contre les jetons, puis les enregistre.</summary>
    public async Task CompleteAsync(string? state, string? code, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(state) || !pending.TryRemove(state, out var p) || p.Expires < clock.GetUtcNow())
            throw new GoogleApiException("Demande de connexion inconnue ou expirée : recommencez depuis Réglages › Google Agenda.");
        if (string.IsNullOrEmpty(code)) throw new GoogleApiException("Google n'a pas renvoyé de code d'autorisation.");

        var settings = store.Load();
        var token = await PostTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = settings.ClientId!,
            ["client_secret"] = settings.ClientSecret!,
            ["code"] = code,
            ["code_verifier"] = p.Verifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = p.RedirectUri,
        }, ct);
        if (string.IsNullOrEmpty(token.RefreshToken))
            throw new GoogleApiException("Google n'a pas fourni de jeton de rafraîchissement. Retirez l'accès de l'application dans votre compte Google puis reconnectez-vous.");

        var scopes = (token.Scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!scopes.Contains("https://www.googleapis.com/auth/calendar.events"))
            throw new GoogleApiException("L'accès aux événements de l'agenda n'a pas été accordé : cochez toutes les autorisations demandées.");

        access = (token.AccessToken, clock.GetUtcNow().AddSeconds(token.ExpiresIn - 60));
        store.Update(s =>
        {
            s.RefreshToken = token.RefreshToken;
            s.ConnectedAt = DateTime.Now;
        });
    }

    /// <summary>Jeton d'accès valide, renouvelé si besoin.</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (access is { } a && a.Expires > clock.GetUtcNow()) return a.Token;
        await refreshGate.WaitAsync(ct);
        try
        {
            if (access is { } b && b.Expires > clock.GetUtcNow()) return b.Token;
            var settings = store.Load();
            if (!settings.IsConnected) throw new GoogleNotConnectedException("Google Agenda n'est pas connecté.");
            TokenResponse token;
            try
            {
                token = await PostTokenAsync(new Dictionary<string, string>
                {
                    ["client_id"] = settings.ClientId!,
                    ["client_secret"] = settings.ClientSecret!,
                    ["refresh_token"] = settings.RefreshToken!,
                    ["grant_type"] = "refresh_token",
                }, ct);
            }
            catch (InvalidGrantException)
            {
                // Accès retiré depuis le compte Google, ou jeton expiré (application restée en mode « Test »).
                store.Update(s => s.RefreshToken = null);
                throw new GoogleNotConnectedException("La connexion à Google Agenda a expiré ou a été retirée : reconnectez-vous dans Réglages › Google Agenda.");
            }
            access = (token.AccessToken, clock.GetUtcNow().AddSeconds(token.ExpiresIn - 60));
            return token.AccessToken;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    /// <summary>Révoque l'accès chez Google (au mieux) et oublie le jeton.</summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        var settings = store.Load();
        if (!string.IsNullOrEmpty(settings.RefreshToken))
        {
            try
            {
                using var http = httpFactory.CreateClient(nameof(GoogleAuth));
                await http.PostAsync(RevokeEndpoint, new FormUrlEncodedContent(
                    new Dictionary<string, string> { ["token"] = settings.RefreshToken }), ct);
            }
            catch (HttpRequestException)
            {
                // Hors ligne : on oublie quand même le jeton localement.
            }
        }
        access = null;
        store.Update(s =>
        {
            s.RefreshToken = null;
            s.AccountEmail = null;
            s.ConnectedAt = null;
            s.ShownCalendars = [];
            s.ExportCalendarId = "primary";
        });
    }

    /// <summary>Oublie le jeton d'accès en mémoire (ex. après un changement d'identifiants).</summary>
    public void Reset() => access = null;

    private sealed class InvalidGrantException : Exception;

    private async Task<TokenResponse> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var http = httpFactory.CreateClient(nameof(GoogleAuth));
        using var response = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await SafeReadError(response, ct);
            if (error?.Error == "invalid_grant" && form["grant_type"] == "refresh_token") throw new InvalidGrantException();
            throw new GoogleApiException(error?.Error switch
            {
                "invalid_client" => "ID client ou secret client refusé par Google : vérifiez-les dans Réglages › Google Agenda.",
                "invalid_grant" => "Code d'autorisation refusé ou expiré : recommencez la connexion.",
                _ => $"Google a refusé la connexion ({error?.Error ?? ((int)response.StatusCode).ToString()}"
                     + (error?.Description is { } d ? $" : {d}" : "") + ").",
            });
        }
        return await response.Content.ReadFromJsonAsync<TokenResponse>(ct)
               ?? throw new GoogleApiException("Réponse de Google illisible.");
    }

    private static async Task<TokenError?> SafeReadError(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadFromJsonAsync<TokenError>(ct); }
        catch (Exception) { return null; }
    }

    private static string RandomToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("scope")] string? Scope);

    private sealed record TokenError(
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? Description);
}
