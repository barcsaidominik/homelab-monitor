using System.Globalization;
using System.Text.Json;
using HomelabMonitor.Store;

namespace HomelabMonitor.Engine;

public sealed record MuteEntry(string Check, DateTimeOffset Until, string Reason);

/// <summary>
/// Maintenance windows (local time, [start, end)) and a grace period after boot. Manual muting is the mute.json file in
/// the data directory (there is no write endpoint on the network; the owner edits the file).
/// </summary>
public sealed class MaintenanceCalendar(MaintenanceOptions options, TimeZoneInfo tz, DateTimeOffset bootTime)
{
    public bool IsScheduled(DateTimeOffset nowUtc)
    {
        if (nowUtc >= bootTime && nowUtc < bootTime.AddMinutes(options.PostBootMinutes))
        {
            return true;
        }

        var local = TimeZoneInfo.ConvertTime(nowUtc, tz);
        var t = local.TimeOfDay;
        foreach (var w in options.Windows)
        {
            var start = TimeSpan.Parse(w.Start, CultureInfo.InvariantCulture);
            var end = TimeSpan.Parse(w.End, CultureInfo.InvariantCulture);
            if (end > start)
            {
                if (local.DayOfWeek == w.Day && t >= start && t < end)
                {
                    return true;
                }
            }
            else
            {
                // window spanning midnight
                if ((local.DayOfWeek == w.Day && t >= start) || ((int)local.DayOfWeek == ((int)w.Day + 1) % 7 && t < end))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool IsMuted(IReadOnlyList<MuteEntry> mutes, string checkId, DateTimeOffset now) =>
        mutes.Any(m => now < m.Until && Matches(m.Check, checkId));

    private static bool Matches(string pattern, string checkId) =>
        pattern == "*" || pattern == checkId || (pattern.EndsWith('*') && checkId.StartsWith(pattern[..^1], StringComparison.Ordinal));

    /// <summary>Mutes and scheduled windows together: true if the results of the check count as maintenance now.</summary>
    public bool IsActiveFor(string checkId, DateTimeOffset now, IReadOnlyList<MuteEntry> mutes) =>
        IsScheduled(now) || IsMuted(mutes, checkId, now);
}

public static class MuteFile
{
    /// <summary>Tolerant load: a missing or broken file means no mutes. Format: [{"check":"http.app","until":"2026-11-03T10:00:00Z","reason":"..."}]; "check" may end with "*".</summary>
    public static IReadOnlyList<MuteEntry> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var list = new List<MuteEntry>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var check = el.TryGetProperty("check", out var c) ? c.GetString() : null;
                if (string.IsNullOrWhiteSpace(check) || !el.TryGetProperty("until", out var u) || !u.TryGetDateTimeOffset(out var until))
                {
                    continue;
                }

                list.Add(new MuteEntry(check, until, el.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : ""));
            }

            return list;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

public sealed record Uptime(double? Percent, long OkMinutes, long TotalMinutes, long UnknownMinutes, long MaintenanceMinutes);

public static class UptimeCalculator
{
    /// <summary>uptime% = OK / (all − maintenance − UNKNOWN) × 100. UNKNOWN is reported separately.</summary>
    public static Uptime Compute(UptimeCounts c)
    {
        var denominator = c.Ok + c.Warn + c.Error;
        double? pct = denominator == 0 ? null : Math.Round(100.0 * c.Ok / denominator, 3);
        return new Uptime(pct, c.Ok, denominator, c.Unknown, c.Maintenance);
    }
}
