using System.Diagnostics;
using Microsoft.AspNetCore.Routing;
using static Server.Logging.ServerText;

namespace Server.Logging;

/// <summary>
/// Writes one line for every request the server receives, including health checks, inbox polling and
/// requests refused by the rate limiter or not found: time, client IP, user, operation, HTTP result,
/// duration and size of uploads and downloads. Request bodies are never read or logged.
/// </summary>
public sealed class RequestLogMiddleware(RequestDelegate next, ILogger<RequestLogMiddleware> logger, bool showInboxPolling)
{
    public const string ActivityItemKey = "nox.activity";

    public async Task InvokeAsync(HttpContext context)
    {
        long started = Stopwatch.GetTimestamp();
        Exception? failure = null;
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            Write(context, Stopwatch.GetElapsedTime(started), failure);
        }
    }

    private void Write(HttpContext context, TimeSpan elapsed, Exception? failure)
    {
        int status = failure is not null && !context.Response.HasStarted ? 500 : context.Response.StatusCode;
        string method = context.Request.Method;
        string pattern = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? string.Empty;
        string operation = context.Items[ActivityItemKey] as string ?? Describe(method, pattern, context);

        if (!showInboxPolling && pattern == "api/transfers/inbox" && status == 200)
            return;

        System.Net.IPAddress? remote = context.Connection.RemoteIpAddress;
        string ip = remote is null ? "?" : remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4().ToString() : remote.ToString();
        string user = context.User.Identity?.IsAuthenticated == true ? Clean(context.User.Identity.Name) : "-";
        string path = Clean(context.Request.Path.Value ?? "/", 160);
        string size = Size(context, method);
        string outcome = status switch
        {
            >= 200 and < 300 => "OK",
            401 => L("not signed in", "non autenticato"),
            403 => L("forbidden", "non autorizzato"),
            404 => L("not found", "non trovato"),
            409 => L("conflict", "conflitto"),
            413 => L("too large", "troppo grande"),
            429 => L("too many requests", "troppe richieste"),
            >= 500 => L("server error", "errore del server"),
            _ => L("refused", "rifiutato")
        };

        string line = $"{ip} {user} {operation} | {method} {path} | {status} {outcome} | {elapsed.TotalMilliseconds:0} ms{size}";
        LogLevel level = status >= 500 ? LogLevel.Error : status >= 400 ? LogLevel.Warning : LogLevel.Information;
        logger.Log(level, failure, "{Line}", line);
    }

    /// <summary>Name of each API operation (route templates of the controllers).</summary>
    private static string Describe(string method, string pattern, HttpContext context) => (method, pattern) switch
    {
        ("GET", "api/health") => L("[Health] Connection check", "[Health] Controllo connessione"),
        ("POST", "api/auth/username-exists") => L("[Sign-in] Username check", "[Accesso] Verifica nome utente"),
        ("POST", "api/auth/login") => L("[Sign-in] Login", "[Accesso] Login"),
        ("POST", "api/auth/register") => L("[Sign-in] Registration", "[Accesso] Registrazione"),
        ("GET", "api/auth/me") => L("[Sign-in] User profile", "[Accesso] Profilo utente"),
        ("PUT", "api/auth/vault") => L("[Vault] Encrypted keys saved", "[Vault] Salvataggio chiavi cifrate"),
        ("GET", "api/prekeys/status") => L("[Prekey] Status", "[Prekey] Stato"),
        ("POST", "api/prekeys/signed") => L("[Prekey] New signed prekey", "[Prekey] Nuova signed prekey"),
        ("POST", "api/prekeys/one-time") => L("[Prekey] New one-time prekeys", "[Prekey] Nuove one-time prekey"),
        ("PUT", "api/prekeys/recover-bundle") => L("[Identity] Recovery", "[Identità] Ripristino"),
        ("GET", "api/users/{username}/prekey-bundle") =>
            L("[Recipient] Keys of ", "[Destinatario] Chiavi di ") + Clean(context.Request.RouteValues["username"]?.ToString()),
        ("POST", "api/transfers") => L("[Transfer] Created", "[Transfer] Creazione"),
        ("POST", "api/transfers/{transferId:guid}/files/{fileId:guid}/content") => L("[Upload] Encrypted file", "[Upload] File cifrato"),
        ("GET", "api/transfers/inbox") => L("[Inbox] Incoming files check", "[Inbox] Controllo file in arrivo"),
        ("GET", "api/transfers/{transferId:guid}") => L("[Transfer] Details", "[Transfer] Dettagli"),
        ("GET", "api/transfers/{transferId:guid}/files/{fileId:guid}/content") => L("[Download] Encrypted file", "[Download] File cifrato"),
        ("POST", "api/transfers/{transferId:guid}/downloaded") => L("[Download] Confirmed and removed", "[Download] Conferma e rimozione"),
        ("POST", "api/transfers/{transferId:guid}/tampered") => L("[Tampering] Report", "[Manomissione] Segnalazione"),
        _ when pattern.Length == 0 => L("[Request] Unknown address", "[Richiesta] Indirizzo sconosciuto"),
        _ => L("[Request] ", "[Richiesta] ") + pattern
    };

    private static string Size(HttpContext context, string method)
    {
        long? bytes = method == "GET" ? context.Response.ContentLength : context.Request.ContentLength;
        if (bytes is not > 1024) return string.Empty;
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes.Value;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $" | {value:0.#} {units[unit]}";
    }

    private static string Clean(string? value, int maximum = 80)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        string single = CompactConsoleFormatter.SingleLine(value.Trim());
        return single.Length <= maximum ? single : single[..maximum];
    }
}
