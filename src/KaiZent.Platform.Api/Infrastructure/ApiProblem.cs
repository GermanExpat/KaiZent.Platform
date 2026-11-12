using KaiZent.Platform.Contracts.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Diagnostics;
using Npgsql;

namespace KaiZent.Platform.Api.Infrastructure;

/// <summary>Fachlicher Fehler, den die API als <see cref="ApiError"/> mit passendem Status beantwortet.</summary>
public sealed class ApiProblem(int status, string code, string message, IReadOnlyDictionary<string, string[]>? errors = null) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public IReadOnlyDictionary<string, string[]>? Errors { get; } = errors;

    public static ApiProblem NotFound(string message) => new(StatusCodes.Status404NotFound, ErrorCodes.NotFound, message);
    public static ApiProblem Conflict(string message) => new(StatusCodes.Status409Conflict, ErrorCodes.Conflict, message);
    public static ApiProblem Forbidden(string message) => new(StatusCodes.Status403Forbidden, ErrorCodes.Forbidden, message);
    public static ApiProblem Unavailable(string message) => new(StatusCodes.Status503ServiceUnavailable, ErrorCodes.Unavailable, message);

    public static ApiProblem Stale() => new(StatusCodes.Status409Conflict, ErrorCodes.Stale,
        "Jemand anderes hat diesen Eintrag inzwischen geändert. Bitte neu laden und die Änderung erneut vornehmen.");

    public static ApiProblem Invalid(string field, string message) =>
        new(StatusCodes.Status400BadRequest, ErrorCodes.Validation, message, new Dictionary<string, string[]> { [field] = [message] });

    /// <summary>Mehrere Feldfehler auf einmal (z. B. aus einer Prüfung in den Contracts).</summary>
    public static ApiProblem Invalid(IReadOnlyDictionary<string, string> errors) =>
        new(StatusCodes.Status400BadRequest, ErrorCodes.Validation, errors.Values.First(), errors.ToDictionary(e => e.Key, e => new[] { e.Value }));
}

/// <summary>Sammelt Feldfehler und wirft sie gesammelt als 400.</summary>
public sealed class Validator
{
    private readonly Dictionary<string, List<string>> _errors = [];

    public Validator Check(bool ok, string field, string message)
    {
        if (!ok) (_errors.TryGetValue(field, out var l) ? l : _errors[field] = []).Add(message);
        return this;
    }

    /// <summary>Übernimmt die Befunde einer gemeinsamen Regel aus den Contracts.</summary>
    public Validator Add(IEnumerable<(string Field, string Message)> problems)
    {
        foreach (var (field, message) in problems) Check(false, field, message);
        return this;
    }

    public void ThrowIfInvalid()
    {
        if (_errors.Count == 0) return;
        throw new ApiProblem(StatusCodes.Status400BadRequest, ErrorCodes.Validation, "Die Eingaben sind unvollständig oder ungültig.",
            _errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
    }
}

/// <summary>
/// Beantwortet fachliche Fehler (<see cref="ApiProblem"/>, doppelte Einträge) und vorübergehende Datenbankfehler als <see cref="ApiError"/>,
/// ohne sie als Fehler zu protokollieren. Liegt als erster Filter auf allen API-Routen.
/// </summary>
public sealed class ApiProblemFilter(ILogger<ApiProblemFilter> log) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (ApiProblem p)
        {
            return Results.Json(new ApiError(p.Code, p.Message, p.Errors), statusCode: p.Status);
        }
        catch (Exception e) when (ApiErrorHandling.Classify(e, context.HttpContext.RequestAborted) is { } known)
        {
            if (known.Status == StatusCodes.Status503ServiceUnavailable)
            {
                log.LogWarning(e, "Vorübergehender Datenbankfehler bei {Method} {Path}.", context.HttpContext.Request.Method, context.HttpContext.Request.Path);
                context.HttpContext.Response.Headers.RetryAfter = ApiErrorHandling.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return known.Error is null ? Results.StatusCode(known.Status) : Results.Json(known.Error, statusCode: known.Status);
        }
    }
}

public static class ApiErrorHandling
{
    /// <summary>Statuscode, wenn der Aufrufer die Anfrage selbst abgebrochen hat (Seite verlassen, Zeitlimit der App); niemand liest die Antwort.</summary>
    public const int ClientClosedRequest = 499;

    /// <summary>Wartezeit, die der Kopf Retry-After bei vorübergehenden Fehlern empfiehlt.</summary>
    public const int RetryAfterSeconds = 2;

    /// <summary>
    /// Ordnet bekannte, nicht fachliche Ausnahmen einer Antwort zu (null: unerwarteter Fehler, also 500 mit Protokolleintrag):
    /// <list type="bullet">
    /// <item>Abbruch durch den Aufrufer → 499 ohne Inhalt.</item>
    /// <item>doppelter Eintrag (23505) → 409 conflict; fehlender Bezug (23503, z. B. gleichzeitig gelöscht) → 409 conflict.</item>
    /// <item>vorübergehende Datenbankfehler (Verbindung weg, Server startet neu, zu viele Verbindungen, Deadlock, Serialisierung,
    ///   Sperr-Zeitlimit) → 503 unavailable; ein erneuter Versuch kann gelingen.</item>
    /// </list>
    /// </summary>
    public static (int Status, ApiError? Error)? Classify(Exception e, CancellationToken requestAborted) => e switch
    {
        OperationCanceledException when requestAborted.IsCancellationRequested => (ClientClosedRequest, null),
        PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } =>
            (StatusCodes.Status409Conflict, new ApiError(ErrorCodes.Conflict, "Ein Eintrag mit diesen Daten existiert bereits.")),
        PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation } =>
            (StatusCodes.Status409Conflict, new ApiError(ErrorCodes.Conflict,
                "Der Eintrag hängt mit anderen Daten zusammen, die inzwischen geändert oder gelöscht wurden. Bitte neu laden.")),
        _ when IsTransient(e) => (StatusCodes.Status503ServiceUnavailable, new ApiError(ErrorCodes.Unavailable,
            "Die Datenbank ist gerade ausgelastet oder kurz nicht erreichbar. Bitte in einigen Sekunden erneut versuchen.")),
        _ => null,
    };

    /// <summary>Vorübergehender Datenbankfehler, nach dem ein erneuter Versuch sinnvoll ist.</summary>
    public static bool IsTransient(Exception e) => e switch
    {
        PostgresException p => p.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected
            or PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.TooManyConnections or PostgresErrorCodes.CannotConnectNow
            or PostgresErrorCodes.AdminShutdown or PostgresErrorCodes.CrashShutdown,
        NpgsqlException n => n.IsTransient,
        _ => false,
    };

    /// <summary>Unerwartete Ausnahmen: 500 mit neutraler Meldung, Details nur im Log. Ungültiges JSON: 400. Bekannte Fälle wie im Filter.</summary>
    public static void UseApiErrors(this WebApplication app) => app.UseExceptionHandler(handler => handler.Run(async ctx =>
    {
        var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
        var (status, error) = ex switch
        {
            BadHttpRequestException => (StatusCodes.Status400BadRequest, new ApiError(ErrorCodes.Validation, "Die Anfrage ist ungültig.")),
            not null when Classify(ex, ctx.RequestAborted) is { } known => (known.Status, known.Error),
            _ => (StatusCodes.Status500InternalServerError, new ApiError(ErrorCodes.Internal, "Interner Fehler. Bitte später erneut versuchen.")),
        };
        ctx.Response.StatusCode = status;
        if (status == StatusCodes.Status503ServiceUnavailable)
            ctx.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (error is not null && !ctx.RequestAborted.IsCancellationRequested) await ctx.Response.WriteAsJsonAsync(error);
    }));
}
