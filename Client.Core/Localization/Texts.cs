using System.Collections;
using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace Client.Core;

/// <summary>
/// Localized client strings (Italian default, English). Same keys and behaviour as the
/// Windows client, without any UI framework: the UI listens to <see cref="LanguageChanged"/>
/// and reads <see cref="Entries"/> to refresh its own resources.
/// </summary>
public static class Texts
{
    private static readonly ResourceManager Catalog = new("Client.Core.Localization.Strings", typeof(Texts).Assembly);
    private static CultureInfo _culture = CultureInfo.GetCultureInfo("it");
    private sealed record Message(string Key, object[] Arguments);
    // Retain the template of a displayed status without retaining old status strings.
    // Retranslation never parses or substitutes user-supplied names or file paths.
    private static readonly ConditionalWeakTable<string, Message> Messages = new();

    public static string Language => _culture.Name;

    public static event Action? LanguageChanged;

    public static void Apply(string? language)
    {
        _culture = CultureInfo.GetCultureInfo(language == "en" ? "en" : "it");
        LanguageChanged?.Invoke();
    }

    /// <summary>Every key with its text in the current language.</summary>
    public static IEnumerable<KeyValuePair<string, string>> Entries()
    {
        foreach (DictionaryEntry item in Catalog.GetResourceSet(CultureInfo.InvariantCulture, true, true)!)
        {
            string key = (string)item.Key;
            yield return new(key, Catalog.GetString(key, _culture) ?? string.Empty);
        }
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
