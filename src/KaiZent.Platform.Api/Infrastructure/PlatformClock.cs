namespace KaiZent.Platform.Api.Infrastructure;

/// <summary>Kalendertag der Plattform. Die Betriebe sitzen in Deutschland, deshalb zählt ein Datum (z. B. „Preis gilt ab“) in deutscher Zeit.</summary>
public static class PlatformClock
{
    private static readonly TimeZoneInfo Zone = Find();

    public static DateOnly Today(TimeProvider time) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), Zone).DateTime);

    private static TimeZoneInfo Find()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }   // Abbild ohne Zeitzonendaten
    }
}
