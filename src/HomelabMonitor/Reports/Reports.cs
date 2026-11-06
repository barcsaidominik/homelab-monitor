using System.Globalization;
using System.Text;
using HomelabMonitor.Domain;
using HomelabMonitor.Engine;
using HomelabMonitor.Store;
using Microsoft.Extensions.Logging;

namespace HomelabMonitor.Reports;

public enum ReportType { Daily, Weekly }

public sealed record DiskInfo(string Volume, double FreeGb, double TotalGb);

public interface IDiskSpaceReader
{
    IReadOnlyList<DiskInfo> Read(IEnumerable<string> volumes);
}

/// <summary>DriveInfo-based reader: works for a non-admin account. Missing or not-ready volumes are skipped.</summary>
public sealed class DriveInfoReader : IDiskSpaceReader
{
    private const double Gb = 1024d * 1024 * 1024;

    public IReadOnlyList<DiskInfo> Read(IEnumerable<string> volumes)
    {
        var list = new List<DiskInfo>();
        foreach (var v in volumes)
        {
            try
            {
                var d = new DriveInfo(v);
                if (d.IsReady)
                {
                    list.Add(new DiskInfo(v.ToUpperInvariant(), Math.Round(d.AvailableFreeSpace / Gb, 2), Math.Round(d.TotalSize / Gb, 2)));
                }
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                // skipped: a missing volume is not a monitor error
            }
        }

        return list;
    }
}

/// <summary>One disk sample per volume and hour (kept for a year).</summary>
public sealed class DiskSampler(SqliteStore store, IDiskSpaceReader reader, ReportOptions options, TimeProvider time)
{
    public bool SampleIfDue()
    {
        var now = time.GetUtcNow();
        var last = store.LastDiskSampleAt();
        if (last is not null && now - last.Value < TimeSpan.FromMinutes(options.DiskSampleMinutes))
        {
            return false;
        }

        var infos = reader.Read(options.Disks);
        foreach (var d in infos)
        {
            store.InsertDiskSample(now, d.Volume, d.FreeGb, d.TotalGb);
        }

        return infos.Count > 0;
    }
}

public sealed record DiskTrend(string Volume, double FreeGb, double TotalGb, double? Delta7dGb, double? PerDayGb);

public static class DiskTrendCalculator
{
    /// <summary>Difference between the first and last sample of the window; a trend needs at least one day between them.</summary>
    public static DiskTrend? Compute(IReadOnlyList<DiskSample> samples)
    {
        if (samples.Count == 0)
        {
            return null;
        }

        var first = samples[0];
        var last = samples[^1];
        var span = last.At - first.At;
        if (samples.Count < 2 || span < TimeSpan.FromDays(1))
        {
            return new DiskTrend(last.Volume, last.FreeGb, last.TotalGb, null, null);
        }

        var delta = Math.Round(last.FreeGb - first.FreeGb, 2);
        return new DiskTrend(last.Volume, last.FreeGb, last.TotalGb, delta, Math.Round(delta / span.TotalDays, 2));
    }
}

public static class ReportSchedule
{
    public static TimeSpan ParseAt(string at) => TimeSpan.Parse(at, CultureInfo.InvariantCulture);

    /// <summary>The latest due time (&lt;= now). Daily: every day at `at`; weekly: on the given weekday at `at`. Local time, DST-safe.</summary>
    public static DateTimeOffset LastDue(ReportType type, DateTimeOffset now, TimeZoneInfo tz, TimeSpan at, DayOfWeek weekday)
    {
        var local = TimeZoneInfo.ConvertTime(now, tz).DateTime;
        var date = local.Date;
        var step = type == ReportType.Daily ? 1 : 7;
        if (type == ReportType.Weekly)
        {
            date = date.AddDays(-(((int)date.DayOfWeek - (int)weekday + 7) % 7));
        }

        var due = ToUtc(date + at, tz);
        return due > now ? ToUtc(date.AddDays(-step) + at, tz) : due;
    }

    /// <summary>Start of the period: one / seven calendar days back in local time (23 or 25 hours on DST days).</summary>
    public static DateTimeOffset PeriodStart(ReportType type, DateTimeOffset end, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTime(end, tz).DateTime;
        return ToUtc(local.AddDays(type == ReportType.Daily ? -1 : -7), tz);
    }

    private static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo tz)
    {
        if (tz.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, tz), TimeSpan.Zero);
    }
}

public sealed record GeneratedReport(ReportType Type, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, string Text, string Summary, IReadOnlyDictionary<string, object?> Properties);

/// <summary>Deterministic daily/weekly report: a Seq event (Component=report) and, weekly, a Markdown file. It never notifies.</summary>
public sealed class ReportService(
    SqliteStore store, MonitorCatalog catalog, MonitorOptions options, TimeZoneInfo tz, TimeProvider time, ILogger<ReportService> log)
{
    private static string T(DateTimeOffset t, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTime(t, tz).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Pct(double? v) => v is null ? "n/a" : Num(v.Value) + "%";

    /// <summary>Seq property name of an uptime entry: "Uptime" + the key with its first letter upper-cased (media → UptimeMedia).</summary>
    public static string UptimeProperty(string key) =>
        "Uptime" + (key.Length == 0 ? "" : char.ToUpperInvariant(key[0]) + key[1..]);

    /// <summary>Creates the missing report (at most one per type: missed periods are not backfilled one by one).</summary>
    public IReadOnlyList<ReportType> RunIfDue()
    {
        var generated = new List<ReportType>();
        if (!options.Reports.Enabled)
        {
            return generated;
        }

        var now = time.GetUtcNow();
        var at = ReportSchedule.ParseAt(options.Reports.At);
        foreach (var type in new[] { ReportType.Daily, ReportType.Weekly })
        {
            var due = ReportSchedule.LastDue(type, now, tz, at, options.Reports.WeeklyDay);
            var last = store.LastReport(type.ToString().ToLowerInvariant());
            if (last is not null && last.PeriodEnd >= due)
            {
                continue;
            }

            try
            {
                Publish(Generate(type, due, now));
                generated.Add(type);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                log.LogError("Creating the report failed ({ReportType}): {Error}", type, ex.GetType().Name);
            }
        }

        return generated;
    }

    private void Publish(GeneratedReport r)
    {
        var now = time.GetUtcNow();
        var type = r.Type.ToString().ToLowerInvariant();
        store.InsertReport(type, now, r.PeriodEnd, r.Summary, r.Text);
        if (r.Type == ReportType.Weekly)
        {
            WriteWeeklyFile(r);
        }

        // The per-check uptime properties depend on the configuration, so they go through the log scope.
        var p = r.Properties;
        var scope = p.Where(kv => kv.Key.StartsWith("Uptime", StringComparison.Ordinal)).ToDictionary(kv => kv.Key, kv => kv.Value);
        using (log.BeginScope(scope))
        {
            log.LogInformation(
                "Report ({Component}) {ReportType}: {ReportSummary} PeriodStart={PeriodStart} PeriodEnd={PeriodEnd} IncidentsOpened={IncidentsOpened} IncidentsResolved={IncidentsResolved} OpenNow={OpenNow}\n{ReportText}",
                "report", type, r.Summary, r.PeriodStart, r.PeriodEnd, p["IncidentsOpened"], p["IncidentsResolved"], p["OpenNow"], r.Text);
        }
    }

    private void WriteWeeklyFile(GeneratedReport r)
    {
        var dir = Path.Combine(options.DataDir, "reports");
        Directory.CreateDirectory(dir);
        var date = TimeZoneInfo.ConvertTime(r.PeriodEnd, tz).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(dir, $"weekly-{date}.md"), r.Text, new UTF8Encoding(false));
        PruneWeeklyFiles(dir, options.Reports.KeepWeeklyFiles);
    }

    public static void PruneWeeklyFiles(string dir, int keep)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "weekly-*.md").OrderByDescending(f => f, StringComparer.Ordinal).Skip(Math.Max(0, keep)))
        {
            File.Delete(f);
        }
    }

    private sealed record Stats(int Opened, int Resolved, int Total, TimeSpan Longest, string? LongestName, List<IncidentRecord> Incidents);

    private Stats Collect(DateTimeOffset from, DateTimeOffset to, DateTimeOffset now)
    {
        var all = store.GetIncidentsInPeriod(from, to);
        var opened = all.Count(i => i.OpenedAt >= from && i.OpenedAt < to);
        var resolved = all.Count(i => i.State == CheckState.Resolved && i.ResolvedAt is { } r && r >= from && r < to);
        TimeSpan longest = TimeSpan.Zero;
        string? longestName = null;
        foreach (var i in all)
        {
            var d = Duration(i, now);
            if (d > longest)
            {
                longest = d;
                longestName = catalog.Get(i.CheckId).DisplayName;
            }
        }

        return new Stats(opened, resolved, all.Count, longest, longestName, all.ToList());
    }

    private static TimeSpan Duration(IncidentRecord i, DateTimeOffset now) => (i.ResolvedAt ?? now) - i.OpenedAt;

    public GeneratedReport Generate(ReportType type, DateTimeOffset periodEnd, DateTimeOffset now)
    {
        var start = ReportSchedule.PeriodStart(type, periodEnd, tz);
        var stats = Collect(start, periodEnd, now);
        var open = store.GetOpenIncidents();
        var props = new Dictionary<string, object?>
        {
            ["IncidentsOpened"] = stats.Opened,
            ["IncidentsResolved"] = stats.Resolved,
            ["OpenNow"] = open.Count,
        };

        var sb = new StringBuilder();
        var title = type == ReportType.Daily ? "Daily summary" : "Weekly summary";
        var span = type == ReportType.Daily ? "last 24 hours" : "last 7 days";
        sb.Append("# ").Append(title).Append(" – ").Append(T(periodEnd, tz)).Append(" (").Append(span).AppendLine(")").AppendLine();

        sb.AppendLine("## Incidents");
        sb.Append("- Opened in the period: ").Append(stats.Opened).Append(", resolved: ").Append(stats.Resolved);
        if (stats.LongestName is not null)
        {
            sb.Append(", longest: ").Append(stats.LongestName).Append(' ').Append(FormatDuration(stats.Longest));
        }

        sb.AppendLine();
        foreach (var i in stats.Incidents.OrderByDescending(i => Duration(i, now)).Take(10))
        {
            var state = i.State == CheckState.Resolved ? "resolved" : "open";
            sb.Append("- ").Append(i.Id).Append(' ').Append(catalog.Get(i.CheckId).DisplayName).Append(" [").Append(i.Severity.ToString().ToUpperInvariant())
                .Append("] ").Append(FormatDuration(Duration(i, now))).Append(" (").Append(state).AppendLine(")");
        }

        if (stats.Total > 10)
        {
            sb.Append("- … and ").Append(stats.Total - 10).AppendLine(" more");
        }

        if (type == ReportType.Weekly)
        {
            var prev = Collect(start.AddDays(-7), start, now);
            sb.Append("- Compared to the previous week: opened ").Append(Delta(stats.Opened - prev.Opened)).Append(" (previous: ").Append(prev.Opened)
                .Append("), resolved ").Append(Delta(stats.Resolved - prev.Resolved)).Append(" (previous: ").Append(prev.Resolved).AppendLine(")");
        }

        sb.AppendLine().AppendLine("## Currently open incidents");
        if (open.Count == 0)
        {
            sb.AppendLine("- none");
        }

        foreach (var i in open.Take(10))
        {
            sb.Append("- ").Append(i.Id).Append(' ').Append(catalog.Get(i.CheckId).DisplayName).Append(" [").Append(i.Severity.ToString().ToUpperInvariant())
                .Append("] ").AppendLine(FormatDuration(now - i.OpenedAt));
        }

        var uptimeText = new List<string>();
        if (options.Reports.Uptime.Count > 0)
        {
            sb.AppendLine().AppendLine("## Availability (excluding maintenance)");
        }

        foreach (var entry in options.Reports.Uptime)
        {
            var u = UptimeCalculator.Compute(store.GetUptimeCounts(entry.Check, start, periodEnd));
            props[UptimeProperty(entry.Key)] = u.Percent;
            sb.Append("- ").Append(entry.Name).Append(": ").Append(Pct(u.Percent)).Append(" (UNKNOWN: ").Append(u.UnknownMinutes).Append(" min, maintenance: ")
                .Append(u.MaintenanceMinutes).Append(" min)");
            if (type == ReportType.Weekly)
            {
                var pu = UptimeCalculator.Compute(store.GetUptimeCounts(entry.Check, start.AddDays(-7), start));
                sb.Append(", previous week: ").Append(Pct(pu.Percent));
            }

            sb.AppendLine();
            uptimeText.Add($"{entry.Name} {Pct(u.Percent)}");
        }

        var backups = new List<string>();
        if (options.Reports.Backups.Count > 0)
        {
            sb.AppendLine().AppendLine("## Backup");
        }

        foreach (var entry in options.Reports.Backups)
        {
            var s = store.GetLastSample(entry.Check);
            var text = s is null ? "no data" : $"{s.Value.Status.ToText()} (last measured: {T(s.Value.At, tz)})";
            sb.Append("- ").Append(entry.Name).Append(": ").AppendLine(text);
            backups.Add(s is null ? "n/a" : s.Value.Status.ToText());
        }

        sb.AppendLine().AppendLine("## Disk space (7-day trend)");
        var diskText = new List<string>();
        foreach (var vol in options.Reports.Disks)
        {
            var t = DiskTrendCalculator.Compute(store.GetDiskSamples(vol.ToUpperInvariant(), now.AddDays(-7), now));
            if (t is null)
            {
                sb.Append("- ").Append(vol.ToUpperInvariant()).AppendLine(": no data");
                continue;
            }

            sb.Append("- ").Append(t.Volume).Append(": ").Append(Num(t.FreeGb)).Append(" GB free");
            if (t.Delta7dGb is { } d)
            {
                sb.Append(", ").Append(d >= 0 ? "+" : "").Append(Num(d)).Append(" GB in 7 days (").Append(t.PerDayGb >= 0 ? "+" : "").Append(Num(t.PerDayGb ?? 0)).Append(" GB/day)");
            }
            else
            {
                sb.Append(", trend: not enough data yet");
            }

            sb.AppendLine();
            diskText.Add($"{t.Volume} {Num(t.FreeGb)} GB");
        }

        if (options.Reports.Values.Count > 0)
        {
            sb.AppendLine().AppendLine("## Metrics");
        }

        foreach (var entry in options.Reports.Values)
        {
            sb.Append("- ").Append(entry.Name).Append(": ").AppendLine(store.GetMeta(MonitorCycle.ValueMetaKey(entry.Check)) ?? "no data");
        }

        sb.AppendLine().AppendLine("## Monitor");
        long.TryParse(store.GetMeta("heartbeat_last_ok"), CultureInfo.InvariantCulture, out var hb);
        sb.Append("- Last successful heartbeat ping: ").AppendLine(hb > 0 ? T(DateTimeOffset.FromUnixTimeSeconds(hb), tz) : "none (disabled or not run yet)");
        var flapping = store.GetFlappingChecks(start, periodEnd);
        sb.Append("- Flapping checks: ").AppendLine(flapping.Count == 0 ? "none" : string.Join(", ", flapping.Select(k => catalog.Get(k.Split('|')[0]).DisplayName)));

        var summary = $"{(type == ReportType.Daily ? "Daily" : "Weekly")} {T(periodEnd, tz)}: incidents opened {stats.Opened}/resolved {stats.Resolved}, open now {open.Count}"
            + (uptimeText.Count > 0 ? "; " + string.Join(", ", uptimeText) : "")
            + (backups.Count > 0 ? "; backup " + string.Join("/", backups) : "")
            + (diskText.Count > 0 ? "; free: " + string.Join(", ", diskText) : "");
        if (summary.Length > 500)
        {
            summary = summary[..500];
        }

        return new GeneratedReport(type, start, periodEnd, sb.ToString(), summary, props);
    }

    private static string Delta(int d) => d >= 0 ? "+" + d.ToString(CultureInfo.InvariantCulture) : d.ToString(CultureInfo.InvariantCulture);

    private static string FormatDuration(TimeSpan d)
    {
        var m = (int)Math.Max(0, d.TotalMinutes);
        return m < 60 ? $"{m} min" : m < 1440 ? $"{m / 60} h {m % 60} min" : $"{m / 1440} d {m % 1440 / 60} h";
    }
}
