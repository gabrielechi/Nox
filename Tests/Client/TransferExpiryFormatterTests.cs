using Client.Windows.Services;

namespace Tests.Client;

public class TransferExpiryFormatterTests
{
    [Fact]
    public void Format_UsesLocalDayAndTime()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test+02", TimeSpan.FromHours(2), "Test", "Test");
        Assert.Equal("09-11-2026 01:30", TransferExpiryFormatter.Format(
            new DateTime(2026, 11, 8, 23, 30, 0, DateTimeKind.Utc), zone));
    }

    [Fact]
    public void Format_UnspecifiedDatabaseValueIsUtc()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test-05", TimeSpan.FromHours(-5), "Test", "Test");
        Assert.Equal("08-11-2026 19:30", TransferExpiryFormatter.Format(
            new DateTime(2026, 11, 9, 0, 30, 0), zone));
    }

    [Fact]
    public void Format_MissingZoneFallsBackToExplicitUtc()
    {
        Assert.Equal("08-11-2026 14:05 UTC", TransferExpiryFormatter.Format(
            new DateTime(2026, 11, 8, 14, 5, 0, DateTimeKind.Utc), null));
    }

    [Theory]
    [InlineData(1, "08-01-2026 13:00")]
    [InlineData(7, "08-07-2026 14:00")]
    public void Format_RespectsDaylightSaving(int month, string expected)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");
        Assert.Equal(expected, TransferExpiryFormatter.Format(
            new DateTime(2026, month, 8, 12, 0, 0, DateTimeKind.Utc), zone));
    }
}
