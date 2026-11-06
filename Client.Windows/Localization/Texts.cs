using System.Collections;
using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Windows;

namespace Client.Windows;

internal static class Texts
{
    private static readonly ResourceManager Catalog = new("Client.Windows.Localization.Strings", typeof(Texts).Assembly);
    private static CultureInfo _culture = CultureInfo.GetCultureInfo("it");
    private sealed record Message(string Key, object[] Arguments);
    // Retain the template of a displayed status without retaining old status strings.
    // Retranslation never parses or substitutes user-supplied names or file paths.
    private static readonly ConditionalWeakTable<string, Message> Messages = new();
    public static string Language => _culture.Name;

    public static void Apply(string? language)
    {
        _culture = CultureInfo.GetCultureInfo(language == "en" ? "en" : "it");
        var resources = Application.Current.Resources;
        foreach (DictionaryEntry item in Catalog.GetResourceSet(CultureInfo.InvariantCulture, true, true)!)
            resources["NoxText." + item.Key] = Catalog.GetString((string)item.Key, _culture);
    }

    public static string T(string key, params object[] args)
    {
        string format = Catalog.GetString(key, _culture) ?? throw new MissingManifestResourceException(key);
        string result = args.Length == 0 ? format : string.Format(_culture, format, args);
        Messages.GetValue(result, _ => new Message(key, args));
        return result;
    }

    public static string Refresh(string value) => Messages.TryGetValue(value, out var message)
        ? T(message.Key, message.Arguments.Select(arg => arg is string s ? Refresh(s) : arg).ToArray())
        : value;

    public static string ErrorOr(string message, string fallbackKey) =>
        Messages.TryGetValue(message, out _) ? Refresh(message) : T(fallbackKey);
}
