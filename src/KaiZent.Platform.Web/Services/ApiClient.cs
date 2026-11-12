using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KaiZent.Platform.Contracts.Common;
using KaiZent.Platform.Contracts.Security;

namespace KaiZent.Platform.Web.Services;

/// <summary>Fehlerantwort der API.</summary>
public sealed class ApiException(HttpStatusCode status, ApiError error) : Exception(error.Message)
{
    public HttpStatusCode Status { get; } = status;
    public ApiError Error { get; } = error;

    public bool IsUnauthorized => Status == HttpStatusCode.Unauthorized;

    /// <summary>Feldfehler der API (Feldname → erste Meldung) für die Anzeige unter den Eingabefeldern.</summary>
    public IReadOnlyDictionary<string, string> FieldErrors =>
        Error.Errors?.Where(e => e.Value.Length > 0).ToDictionary(e => e.Key, e => e.Value[0], StringComparer.OrdinalIgnoreCase)
        ?? new Dictionary<string, string>();
}

/// <summary>
/// Client der API, der einzige Weg der App zu den Daten. Routen und DTOs kommen aus den Contracts der Plattform und des Produkts.
/// Das Token des angemeldeten Benutzers kommt aus dem Anmelde-Cookie (<see cref="AuthSession"/>).
/// </summary>
public sealed class KaiZentApi(HttpClient http, AuthSession session)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Erster Schritt der Anmeldung: angemeldet (200) oder die API verlangt noch den zweiten Faktor (202 mit Challenge).</summary>
    public async Task<(LoginResponse? Login, TwoFactorChallenge? Challenge)> LoginStartAsync(LoginRequest request, string? clientIp,
        CancellationToken ct = default)
    {
        using var req = Anonymous(PlatformRoutes.Login, request, clientIp);
        using var resp = await http.SendAsync(req, ct);
        await EnsureSuccessAsync(resp, ct);
        return resp.StatusCode == HttpStatusCode.Accepted
            ? (null, await resp.Content.ReadFromJsonAsync<TwoFactorChallenge>(Json, ct))
            : (await resp.Content.ReadFromJsonAsync<LoginResponse>(Json, ct), null);
    }

    /// <summary>POST ohne Token (Schritte der Anmeldung, die nur die Challenge der API tragen).</summary>
    public async Task<TResult> PostAnonymousAsync<TResult>(string url, object body, string? clientIp, CancellationToken ct = default)
    {
        using var req = Anonymous(url, body, clientIp);
        using var resp = await http.SendAsync(req, ct);
        await EnsureSuccessAsync(resp, ct);
        return (await resp.Content.ReadFromJsonAsync<TResult>(Json, ct))!;
    }

    internal static readonly TimeSpan[] ReadRetryDelays = [TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1)];

    /// <summary>
    /// Lesen (GET). Bei kurzer Nichterreichbarkeit oder 502/503/504 bis zu zweimal wiederholt; Lesen ändert nichts. Schreibende Anfragen
    /// werden nie automatisch wiederholt.
    /// </summary>
    public async Task<T?> GetAsync<T>(string url, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            var last = attempt >= ReadRetryDelays.Length;
            HttpResponseMessage resp;
            using (var req = await RequestAsync(HttpMethod.Get, url, null))
            {
                try { resp = await http.SendAsync(req, ct); }
                catch (HttpRequestException) when (!last)
                {
                    await Task.Delay(ReadRetryDelays[attempt], ct);
                    continue;
                }
            }
            using (resp)
            {
                if (!last && IsTransient(resp.StatusCode))
                {
                    await Task.Delay(ReadRetryDelays[attempt], ct);
                    continue;
                }
                await EnsureSuccessAsync(resp, ct);
                if (resp.StatusCode == HttpStatusCode.NoContent) return default;
                return await resp.Content.ReadFromJsonAsync<T>(Json, ct);
            }
        }
    }

    internal static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    public async Task<List<T>> ListAsync<T>(string url, CancellationToken ct = default) => await GetAsync<List<T>>(url, ct) ?? [];

    public async Task<TResult?> PostAsync<TResult>(string url, object? body, CancellationToken ct = default)
    {
        using var req = await RequestAsync(HttpMethod.Post, url, body ?? new { });
        using var resp = await http.SendAsync(req, ct);
        await EnsureSuccessAsync(resp, ct);
        return resp.StatusCode == HttpStatusCode.NoContent ? default : await resp.Content.ReadFromJsonAsync<TResult>(Json, ct);
    }

    public async Task PostAsync(string url, object? body, CancellationToken ct = default)
    {
        using var req = await RequestAsync(HttpMethod.Post, url, body ?? new { });
        using var resp = await http.SendAsync(req, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    public async Task<TResult> PutAsync<TResult>(string url, object body, CancellationToken ct = default)
    {
        using var req = await RequestAsync(HttpMethod.Put, url, body);
        using var resp = await http.SendAsync(req, ct);
        await EnsureSuccessAsync(resp, ct);
        return (await resp.Content.ReadFromJsonAsync<TResult>(Json, ct))!;
    }

    public async Task PutAsync(string url, object body, CancellationToken ct = default)
    {
        using var req = await RequestAsync(HttpMethod.Put, url, body);
        using var resp = await http.SendAsync(req, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    public async Task DeleteAsync(string url, CancellationToken ct = default)
    {
        using var req = await RequestAsync(HttpMethod.Delete, url, null);
        using var resp = await http.SendAsync(req, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    /// <summary>Anfrage der Anmeldung; die Adresse des Browsers geht mit, damit die API je Absender drosselt und sperrt.</summary>
    private static HttpRequestMessage Anonymous(string url, object body, string? clientIp)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, body.GetType(), options: Json) };
        if (!string.IsNullOrEmpty(clientIp)) req.Headers.TryAddWithoutValidation(ClientAddressHeader.Name, clientIp);
        return req;
    }

    private async Task<HttpRequestMessage> RequestAsync(HttpMethod method, string url, object? body)
    {
        var req = new HttpRequestMessage(method, url);
        if (await session.GetTokenAsync() is { } token) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: Json);
        return req;
    }

    internal static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        ApiError? error = null;
        try { error = await resp.Content.ReadFromJsonAsync<ApiError>(Json, ct); } catch (JsonException) { } catch (NotSupportedException) { }
        error ??= new ApiError(resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized => ErrorCodes.Unauthorized,
            HttpStatusCode.Forbidden => ErrorCodes.Forbidden,
            HttpStatusCode.TooManyRequests => "throttled",
            _ => "http_" + (int)resp.StatusCode,
        }, "Die API hat die Anfrage abgelehnt.");
        throw new ApiException(resp.StatusCode, error);
    }
}
