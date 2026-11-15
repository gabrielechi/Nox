using Microsoft.AspNetCore.Mvc.Filters;
using Shared.DTO.Auth;
using Shared.DTO.Transfers;

namespace Server.Logging;

// Adds the details that only the controller arguments know (user names in login and registration,
// recipient and number of files of a transfer) to the request line written by RequestLogMiddleware.
// Request bodies are not read again and results are not changed.
public sealed class ActivityLogFilter : IActionFilter, IOrderedFilter
{
    public int Order => int.MinValue;

    public void OnActionExecuting(ActionExecutingContext context)
    {
        string? activity = DescribeActivity(context);
        if (activity is not null)
            context.HttpContext.Items[RequestLogMiddleware.ActivityItemKey] = activity;
    }

    public void OnActionExecuted(ActionExecutedContext context) { }

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
                "Login" => ServerText.L("[Sign-in] Login of ", "[Accesso] Login di ") + Clean((request as LoginRequest)?.Username),
                "Register" => ServerText.L("[Sign-in] Registration of ", "[Accesso] Registrazione di ") + Clean((request as RegisterRequest)?.Username),
                _ => null
            };
        }

        if (controller != "Transfers")
            return null;

        // The transfer id is already in the logged path.
        return action switch
        {
            "CreateTransfer" when request is CreateTransferRequest transfer =>
                ServerText.L("[Transfer] Created ", "[Transfer] Creazione ") +
                $"{username} > {Clean(transfer.RecipientUsername)}, {transfer.Files?.Count ?? 0} file",
            "ReportTransferAsTampered" => ServerText.L("[Tampering] Reported and removed", "[Manomissione] Segnalazione e rimozione"),
            _ => null
        };
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return ServerText.L("unknown user", "utente non disponibile");
        string single = CompactConsoleFormatter.SingleLine(value.Trim());
        return single.Length <= 80 ? single : single[..80];
    }
}
