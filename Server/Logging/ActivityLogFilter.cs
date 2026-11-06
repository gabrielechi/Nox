using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Shared.DTO.Auth;
using Shared.DTO.Transfers;

namespace Server.Logging;

// Observe existing action inputs and outcomes without reading request bodies or changing results.
public sealed class ActivityLogFilter(ILogger<ActivityLogFilter> logger) : IAsyncActionFilter, IOrderedFilter
{
    public int Order => int.MinValue;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        string? activity = DescribeActivity(context);
        ActionExecutedContext result = await next();
        if (activity is null)
            return;

        int status = result.Exception is not null && !result.ExceptionHandled
            ? StatusCodes.Status500InternalServerError
            : (result.Result as IStatusCodeActionResult)?.StatusCode ?? context.HttpContext.Response.StatusCode;
        string outcome = status is >= 200 and < 300 ? "Riuscito" : $"Fallito, HTTP {status}";
        logger.LogInformation("{Activity} | [{Outcome}]", activity, outcome);
    }

    private static string? DescribeActivity(ActionExecutingContext context)
    {
        string? controller = context.RouteData.Values["controller"]?.ToString();
        string? action = context.RouteData.Values["action"]?.ToString();
        string username = Clean(context.HttpContext.User.Identity?.Name);
        context.ActionArguments.TryGetValue("request", out object? request);

        if (controller == "Auth")
        {
            return action switch
            {
                "Login" => $"[Login] {Clean((request as LoginRequest)?.Username)}",
                "Register" => $"[Registrazione] {Clean((request as RegisterRequest)?.Username)}",
                _ => null
            };
        }

        if (controller != "Transfers")
            return null;

        context.ActionArguments.TryGetValue("transferId", out object? transferIdValue);
        string transferId = transferIdValue?.ToString() ?? "-";
        return action switch
        {
            "CreateTransfer" when request is CreateTransferRequest transfer =>
                $"[Transfer] {username} > {Clean(transfer.RecipientUsername)} | {transfer.Files?.Count ?? 0} file | Creazione",
            "UploadFileContent" => $"[Upload] {username} | Transfer {transferId}",
            "MarkTransferAsDownloaded" => $"[Download] {username} | Transfer {transferId} | Conferma e rimozione",
            "ReportTransferAsTampered" => $"[Manomissione] {username} | Transfer {transferId} | Segnalazione e rimozione",
            _ => null
        };
    }

    private static string Clean(string? value) => string.IsNullOrWhiteSpace(value)
        ? "utente non disponibile"
        : CompactConsoleFormatter.SingleLine(value.Trim())[..Math.Min(value.Trim().Length, 80)];
}
