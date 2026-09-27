using FamilyLink.Mobile.Localization;

namespace FamilyLink.Mobile.Services;

/// <summary>Horas y duraciones como las diria una persona: «hace 3 min», «hasta mañana 08:00».</summary>
public static class TimeTexts
{
    public static string Ago(DateTimeOffset at)
    {
        var span = DateTimeOffset.Now - at;
        if (span < TimeSpan.FromMinutes(1))
            return Loc.Get("AgoNow");
        if (span < TimeSpan.FromHours(1))
            return Loc.Format("AgoMinutes", (int)span.TotalMinutes);
        if (span < TimeSpan.FromDays(1))
            return Loc.Format("AgoHours", (int)span.TotalHours);
        return Loc.Format("AgoDate", at.ToLocalTime().ToString("g", Loc.Culture));
    }

    /// <summary>Una hora futura: «18:30», «mañana 08:00» o la fecha si es mas adelante.</summary>
    public static string Until(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        var today = DateTime.Today;
        var time = local.ToString("HH:mm", Loc.Culture);
        if (local.Date == today)
            return time;
        if (local.Date == today.AddDays(1))
            return Loc.Format("TomorrowAt", time);
        return local.ToString("g", Loc.Culture);
    }

    /// <summary>«En pausa» o «En pausa (hasta 18:30)».</summary>
    public static string Paused(DateTimeOffset? until) =>
        until is { } u ? Loc.Format("PausedUntil", Until(u)) : Loc.Get("PausedIndefinite");

    public static string Battery(int percent) => Loc.Format("BatteryPercent", percent);
}
