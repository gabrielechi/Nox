using System.Globalization;

namespace Client.Core.Services;

public static class TransferExpiryFormatter
{
    public static string Format(DateTime expiresAtUtc)
    {
        try { return Format(expiresAtUtc, TimeZoneInfo.Local); }
        catch (TimeZoneNotFoundException) { return Format(expiresAtUtc, null); }
        catch (InvalidTimeZoneException) { return Format(expiresAtUtc, null); }
        catch (System.Security.SecurityException) { return Format(expiresAtUtc, null); }
    }

    internal static string Format(DateTime expiresAtUtc, TimeZoneInfo? zone)
    {
        // SQLite can return Unspecified: this DTO field is UTC by contract.
        DateTime utc = expiresAtUtc.Kind == DateTimeKind.Local
            ? expiresAtUtc.ToUniversalTime()
            : DateTime.SpecifyKind(expiresAtUtc, DateTimeKind.Utc);
        DateTime value = zone is null ? utc : TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
        string suffix = zone is null || zone.Equals(TimeZoneInfo.Utc) ? " UTC" : string.Empty;
        return value.ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture) + suffix;
    }
}
