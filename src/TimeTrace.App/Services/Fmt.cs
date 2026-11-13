using System.Globalization;
using TimeTrace.Core.Analytics;
using TimeTrace.Core.Settings;

namespace TimeTrace.App.Services;

/// <summary>Display formatting driven by the user's time/date format settings. All inputs are UTC; output is local time.</summary>
public static class Fmt
{
    private static AppSettings S => AppServices.Instance?.Settings.Current ?? new AppSettings();
    public static TimeZoneInfo Tz => TimeZoneInfo.Local;

    public static DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Tz);

    public static string Duration(double seconds) => AnalyticsEngine.FormatDuration(seconds);

    public static string TimeOfDay(DateTime utc, bool seconds = false)
    {
        var l = Local(utc);
        var fmt = S.TimeFormat == "12h" ? (seconds ? "h:mm:ss tt" : "h:mm tt") : (seconds ? "HH:mm:ss" : "HH:mm");
        return l.ToString(fmt, CultureInfo.InvariantCulture);
    }

    public static string Hour(int h)
    {
        if (S.TimeFormat == "12h") return (h % 12 == 0 ? 12 : h % 12) + (h < 12 ? "a" : "p");
        return h.ToString("00", CultureInfo.InvariantCulture);
    }

    public static string Date(DateTime utc) => Local(utc).ToString(S.DateFormat, CultureInfo.InvariantCulture);
    public static string Date(DateOnly d) => d.ToString(S.DateFormat, CultureInfo.InvariantCulture);
    public static string DateTimeShort(DateTime utc) => Date(utc) + " " + TimeOfDay(utc);
    public static string DayShort(DateOnly d) => d.ToString("ddd d", CultureInfo.CurrentCulture);
    public static string Percent(double p) => p.ToString("0.#", CultureInfo.InvariantCulture) + "%";
}
