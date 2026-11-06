using System.Globalization;
using HomelabMonitor.Domain;
using HomelabMonitor.Engine;
using HomelabMonitor.Store;

namespace HomelabMonitor.Api;

public sealed record HomepageSummary(
    string Status,
    int OpenIncidents,
    int OldestOpenMinutes,
    DateTimeOffset? LastLightProbeAt,
    DateTimeOffset? LastHeavyRunAt,
    IReadOnlyDictionary<string, double?> Uptime24h,
    IReadOnlyDictionary<string, double?> Uptime7d,
    DateTimeOffset? HeartbeatLastOkAt,
    DateTimeOffset? LastReportAt);

public sealed record ReportSummary(DateTimeOffset? LastDailyAt, string? LastDailyText, DateTimeOffset? LastWeeklyAt);

/// <summary>Data for a Homepage customapi card. Aggregate numbers only: no secrets, addresses or personal data.</summary>
public sealed class SummaryService(SqliteStore store, MonitorCatalog catalog, MonitorStatus status, MonitorOptions options, TimeProvider time)
{
    public HomepageSummary Build()
    {
        var now = time.GetUtcNow();
        var open = store.GetOpenIncidents();
        var worst = "OK";
        foreach (var inc in open)
        {
            var crit = inc.Severity == Severity.Crit || inc.Affected.Any(k => catalog.Get(k.Split('|')[0]).OnError == Severity.Crit);
            if (crit)
            {
                worst = "CRIT";
                break;
            }

            worst = "WARN";
        }

        var oldest = open.Count == 0 ? 0 : (int)Math.Max(0, (now - open.Min(i => i.OpenedAt)).TotalMinutes);
        return new HomepageSummary(
            worst, open.Count, oldest,
            status.LastLightProbeAt ?? store.LastSampleAt(CheckSource.Light),
            store.LastHeavyFinishedAt(),
            Uptime(now.AddHours(-24), now),
            Uptime(now.AddDays(-7), now),
            HeartbeatLastOk(),
            store.LastReportGeneratedAt());
    }

    public ReportSummary BuildReport()
    {
        var daily = store.LastReport("daily");
        var text = daily?.Summary;
        return new ReportSummary(daily?.GeneratedAt, text is { Length: > 500 } ? text[..500] : text, store.LastReport("weekly")?.GeneratedAt);
    }

    private Dictionary<string, double?> Uptime(DateTimeOffset from, DateTimeOffset to) =>
        options.Reports.Uptime.ToDictionary(c => c.Key, c => UptimeCalculator.Compute(store.GetUptimeCounts(c.Check, from, to.AddSeconds(1))).Percent);

    private DateTimeOffset? HeartbeatLastOk() =>
        long.TryParse(store.GetMeta("heartbeat_last_ok"), CultureInfo.InvariantCulture, out var t) ? DateTimeOffset.FromUnixTimeSeconds(t) : null;
}
