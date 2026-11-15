namespace Server.Logging;

/// <summary>
/// Language of what the server shows on screen and in its log ("Language" in appsettings.json):
/// "EN" (default) or "IT". API responses to the clients are not affected.
/// </summary>
public static class ServerText
{
    public static bool Italian { get; private set; }

    public static void Configure(string? language) =>
        Italian = string.Equals(language?.Trim(), "IT", StringComparison.OrdinalIgnoreCase);

    /// <summary>The English text, or the Italian one when "Language" is "IT".</summary>
    public static string L(string english, string italian) => Italian ? italian : english;
}
