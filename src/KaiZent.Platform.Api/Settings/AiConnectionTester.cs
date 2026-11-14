using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using KaiZent.Platform.Contracts.Settings;
using Microsoft.Extensions.Logging;

namespace KaiZent.Platform.Api.Settings;

/// <summary>Prüft die KI-Anbindung, ohne Inhalte zu senden und ohne Kosten zu verursachen.</summary>
public interface IAiConnectionTester
{
    Task<SettingsTestResult> TestAsync(EffectiveAi ai, CancellationToken ct);
}

/// <summary>
/// Fragt die Modellliste des Anbieters ab (kostenlos, keine Inhalte): bestätigt Erreichbarkeit und API-Schlüssel und sagt, ob das eingestellte
/// Modell gelistet ist. Die Adressen der Anbieter stehen fest im Code.
/// </summary>
public sealed class AiConnectionTester(HttpClient http, ILogger<AiConnectionTester> log) : IAiConnectionTester
{
    public const string MistralBase = "https://api.mistral.ai/v1/";
    public const string OpenAiBase = "https://api.openai.com/v1/";
    public const string AnthropicBase = "https://api.anthropic.com/v1/";
    public const string AnthropicVersion = "2023-06-01";

    public async Task<SettingsTestResult> TestAsync(EffectiveAi ai, CancellationToken ct)
    {
        if (ai.Active is not { } p) return new SettingsTestResult(false, "Es ist keine KI eingerichtet (Anbieter und API-Schlüssel fehlen).");
        var model = ai.ModelFor(p);
        using var request = new HttpRequestMessage(HttpMethod.Get, p.Code switch
        {
            AiProviders.Mistral => MistralBase + "models",
            AiProviders.OpenAi => OpenAiBase + "models",
            _ => AnthropicBase + "models?limit=1000",
        });
        if (p.Code == AiProviders.Anthropic)
        {
            request.Headers.Add("x-api-key", ai.ApiKey);
            request.Headers.Add("anthropic-version", AnthropicVersion);
        }
        else request.Headers.Authorization = new("Bearer", ai.ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
            return new SettingsTestResult(false, $"{p.Name} ist nicht erreichbar.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SettingsTestResult(false, $"{p.Name} hat nicht rechtzeitig geantwortet.");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("KI-Verbindungstest {Provider}: Status {Status}", p.Code, (int)response.StatusCode);
                return new SettingsTestResult(false, response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"{p.Name} lehnt den API-Schlüssel ab.",
                    HttpStatusCode.PaymentRequired => $"{p.Name}: Guthaben oder Abrechnung des Kontos prüfen.",
                    HttpStatusCode.TooManyRequests => $"{p.Name} ist gerade überlastet. Bitte gleich noch einmal versuchen.",
                    >= HttpStatusCode.InternalServerError => $"{p.Name} meldet einen Serverfehler ({(int)response.StatusCode}).",
                    _ => $"{p.Name} lehnt die Anfrage ab ({(int)response.StatusCode}).",
                });
            }
            JsonNode? body;
            try
            {
                body = await response.Content.ReadFromJsonAsync<JsonNode>(ct);
            }
            catch (JsonException)
            {
                body = null;
            }
            var listed = Models(body).Contains(model, StringComparer.OrdinalIgnoreCase);
            return new SettingsTestResult(true, listed
                ? $"Verbindung zu {p.Name} steht, der Schlüssel gilt und das Modell {model} ist verfügbar."
                : $"Verbindung zu {p.Name} steht und der Schlüssel gilt. Das Modell {model} steht nicht in der Modellliste; bitte die Schreibweise prüfen.");
        }
    }

    /// <summary>Modellnamen aus der Antwort (<c>data[].id</c>, bei Mistral auch <c>data[].aliases</c>).</summary>
    private static IEnumerable<string> Models(JsonNode? body)
    {
        foreach (var item in body?["data"] as JsonArray ?? new JsonArray())
        {
            if (item?["id"]?.GetValue<string>() is { } id) yield return id;
            foreach (var alias in item?["aliases"] as JsonArray ?? new JsonArray())
                if (alias?.GetValue<string>() is { } a) yield return a;
        }
    }
}
