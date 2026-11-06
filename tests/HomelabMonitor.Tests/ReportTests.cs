using HomelabMonitor.Domain;
using HomelabMonitor.Engine;
using HomelabMonitor.Reports;
using HomelabMonitor.Store;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static HomelabMonitor.Tests.T;

namespace HomelabMonitor.Tests;

public sealed class ReportScheduleTests
{
    private static readonly TimeSpan SevenAm = TimeSpan.FromHours(7);

    private static DateTimeOffset Utc(int y, int m, int d, int h, int min = 0, int sec = 0) => new(y, m, d, h, min, sec, TimeSpan.Zero);

    [Fact]
    public void Daily_BoundaryAt0700Local()
    {
        // 2026-11-03 CET (UTC+1): 07:00 local = 06:00Z
        var before = ReportSchedule.LastDue(ReportType.Daily, Utc(2026, 11, 3, 5, 59, 59), Budapest, SevenAm, DayOfWeek.Monday);
        var exactly = ReportSchedule.LastDue(ReportType.Daily, Utc(2026, 11, 3, 6, 0, 0), Budapest, SevenAm, DayOfWeek.Monday);
        Assert.Equal(Utc(2026, 11, 2, 6), before);
        Assert.Equal(Utc(2026, 11, 3, 6), exactly);
    }

    [Fact]
    public void Weekly_IsMondayAt0700Local()
    {
        // 2026-11-02 is a Monday
        var tueNoon = ReportSchedule.LastDue(ReportType.Weekly, Utc(2026, 11, 3, 12), Budapest, SevenAm, DayOfWeek.Monday);
        var monBefore = ReportSchedule.LastDue(ReportType.Weekly, Utc(2026, 11, 2, 5, 59), Budapest, SevenAm, DayOfWeek.Monday);
        var monAfter = ReportSchedule.LastDue(ReportType.Weekly, Utc(2026, 11, 2, 6, 1), Budapest, SevenAm, DayOfWeek.Monday);
        Assert.Equal(Utc(2026, 11, 2, 6), tueNoon);
        Assert.Equal(Utc(2026, 10, 26, 6), monBefore); // 10-26 is already CET
        Assert.Equal(Utc(2026, 11, 2, 6), monAfter);
    }

    [Fact]
    public void Dst_DueTimesFollowLocalTime_AndPeriodsAre23Or25Hours()
    {
        // 2026-10-25 is the DST change (CEST -> CET)
        var dueSat = ReportSchedule.LastDue(ReportType.Daily, Utc(2026, 10, 24, 12), Budapest, SevenAm, DayOfWeek.Monday);
        var dueMon = ReportSchedule.LastDue(ReportType.Daily, Utc(2026, 10, 26, 12), Budapest, SevenAm, DayOfWeek.Monday);
        Assert.Equal(Utc(2026, 10, 24, 5), dueSat); // CEST: 07:00 = 05:00Z
        Assert.Equal(Utc(2026, 10, 26, 6), dueMon); // CET: 07:00 = 06:00Z
        var end = Utc(2026, 10, 26, 6);
        Assert.Equal(Utc(2026, 10, 25, 6), ReportSchedule.PeriodStart(ReportType.Daily, end, Budapest));
        var sunEnd = Utc(2026, 10, 25, 6);
        Assert.Equal(TimeSpan.FromHours(25), sunEnd - ReportSchedule.PeriodStart(ReportType.Daily, sunEnd, Budapest));
    }
}

public sealed class DiskTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private sealed class FakeDisks(double free) : IDiskSpaceReader
    {
        public double Free { get; set; } = free;
        public IReadOnlyList<DiskInfo> Read(IEnumerable<string> volumes) => volumes.Select(v => new DiskInfo(v.ToUpperInvariant(), Free, 500)).ToList();
    }

    [Fact]
    public void Sampler_RecordsHourly_AndSkipsInBetween()
    {
        var store = new SqliteStore(Path.Combine(_dir.Path, "d.db"));
        store.Migrate();
        var time = new ManualTime(Start);
        var sampler = new DiskSampler(store, new FakeDisks(100), new ReportOptions { Disks = ["c", "d"] }, time);
        Assert.True(sampler.SampleIfDue());
        time.Advance(TimeSpan.FromMinutes(59));
        Assert.False(sampler.SampleIfDue());
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(sampler.SampleIfDue());
        Assert.Equal(2, store.GetDiskSamples("C", Start.AddHours(-1), Start.AddHours(2)).Count);
    }

    [Fact]
    public void Trend_NeedsAtLeastOneDaySpan()
    {
        DiskSample S(double days, double free) => new(Start.AddDays(days), "D", free, 500);
        Assert.Null(DiskTrendCalculator.Compute([]));
        Assert.Null(DiskTrendCalculator.Compute([S(0, 100)])!.Delta7dGb);
        Assert.Null(DiskTrendCalculator.Compute([S(0, 100), S(0.5, 99)])!.Delta7dGb);
        var t = DiskTrendCalculator.Compute([S(0, 100), S(2, 96), S(4, 92)])!;
        Assert.Equal(-8, t.Delta7dGb);
        Assert.Equal(-2, t.PerDayGb);
        Assert.Equal(92, t.FreeGb);
    }

    [Fact]
    public void DriveInfoReader_SkipsInvalidVolumes()
    {
        var r = new DriveInfoReader().Read(["?", "NOSUCHDRIVE:"]);
        Assert.Empty(r);
    }
}

public sealed class ReportServiceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 3, 6, 30, 0, TimeSpan.Zero)); // 07:30 local
    private readonly SqliteStore _store;
    private readonly MonitorCatalog _catalog;
    private readonly MonitorOptions _opt;
    private readonly ReportService _svc;

    public ReportServiceTests()
    {
        _store = new SqliteStore(Path.Combine(_dir.Path, "r.db"));
        _store.Migrate();
        _catalog = Catalog(
            ("http.media", new CheckDefinition { DisplayName = "Media server", OnError = Severity.Crit }),
            ("remote.access", new CheckDefinition { DisplayName = "Remote access", OnError = Severity.Crit }));
        _opt = new MonitorOptions
        {
            DataDir = _dir.Path,
            Reports = new ReportOptions
            {
                Disks = ["D"],
                Uptime = [new UptimeEntry { Key = "media", Name = "Media server", Check = "http.media" }],
                Backups = [new ReportCheckEntry { Name = "Nightly backup", Check = "backup.daily" }, new ReportCheckEntry { Name = "Restore test", Check = "backup.restoretest" }],
                Values = [new ReportCheckEntry { Name = "Failed jobs (24 h)", Check = "jobs.failures" }],
            },
        };
        _svc = new ReportService(_store, _catalog, _opt, Budapest, _time, NullLogger<ReportService>.Instance);
    }

    public void Dispose() => _dir.Dispose();

    private static readonly DateTimeOffset Day = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private void Seed()
    {
        var rows = new List<SampleRow>();
        for (var i = 0; i < 90; i++)
        {
            rows.Add(new SampleRow(Day.AddMinutes(i), "http.media", CheckStatus.Ok, 1, CheckSource.Light, false));
        }

        for (var i = 90; i < 100; i++)
        {
            rows.Add(new SampleRow(Day.AddMinutes(i), "http.media", CheckStatus.Error, 1, CheckSource.Light, false));
        }

        for (var i = 100; i < 105; i++)
        {
            rows.Add(new SampleRow(Day.AddMinutes(i), "http.media", CheckStatus.Unknown, 1, CheckSource.Light, false));
        }

        for (var i = 105; i < 108; i++)
        {
            rows.Add(new SampleRow(Day.AddMinutes(i), "http.media", CheckStatus.Error, 1, CheckSource.Light, true));
        }

        rows.Add(new SampleRow(Day, "backup.daily", CheckStatus.Ok, 0, CheckSource.Heavy, false));
        rows.Add(new SampleRow(Day, "backup.restoretest", CheckStatus.Warn, 0, CheckSource.Heavy, false));
        _store.InsertSamples(rows);

        var tm = new ManualTime(Day.AddHours(1));
        var engine = new IncidentEngine(_catalog, tm, Budapest);
        _store.ApplyEvents(engine.Process(Err("http.media")));
        tm.Advance(TimeSpan.FromMinutes(20));
        _store.ApplyEvents(engine.Process(Ok("http.media")));
        tm.Advance(TimeSpan.FromHours(1));
        _store.ApplyEvents(engine.Process(Err("remote.access"))); // stays open

        _store.InsertDiskSample(_time.GetUtcNow().AddDays(-6), "D", 300, 500);
        _store.InsertDiskSample(_time.GetUtcNow().AddHours(-1), "D", 290, 500);
        _store.SetMeta(MonitorCycle.ValueMetaKey("jobs.failures"), "2");
    }

    [Fact]
    public void DailyReport_ContainsExpectedNumbers()
    {
        Seed();
        var due = ReportSchedule.LastDue(ReportType.Daily, _time.GetUtcNow(), Budapest, TimeSpan.FromHours(7), DayOfWeek.Monday);
        var r = _svc.Generate(ReportType.Daily, due, _time.GetUtcNow());
        Assert.Contains("Opened in the period: 2, resolved: 1", r.Text, StringComparison.Ordinal);
        Assert.Contains("Media server: 90% (UNKNOWN: 5 min, maintenance: 3 min)", r.Text, StringComparison.Ordinal);
        Assert.Contains("Nightly backup: OK", r.Text, StringComparison.Ordinal);
        Assert.Contains("Restore test: WARN", r.Text, StringComparison.Ordinal);
        Assert.Contains("D: 290 GB free, -10 GB in 7 days", r.Text, StringComparison.Ordinal);
        Assert.Contains("Failed jobs (24 h): 2", r.Text, StringComparison.Ordinal);
        Assert.Contains("INC-20261102-001 Media server [CRIT] 20 min (resolved)", r.Text, StringComparison.Ordinal);
        Assert.Contains("Remote access [CRIT]", r.Text, StringComparison.Ordinal);
        Assert.Equal(1, r.Properties["OpenNow"]);
        Assert.Equal(2, r.Properties["IncidentsOpened"]);
        Assert.Equal(90.0, r.Properties["UptimeMedia"]);
        Assert.StartsWith("Daily 2026-11-03 07:00: incidents opened 2/resolved 1, open now 1; Media server 90%; backup OK/WARN; free: D 290 GB", r.Summary, StringComparison.Ordinal);
        Assert.InRange(r.Summary.Length, 1, 500);
        Assert.DoesNotContain("192.168", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void WeeklyReport_HasDeltaVsPreviousWeek()
    {
        Seed();
        var tm = new ManualTime(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero)); // previous week
        var engine = new IncidentEngine(_catalog, tm, Budapest);
        _store.ApplyEvents(engine.Process(Err("remote.access")));
        var end = new DateTimeOffset(2026, 11, 9, 6, 0, 0, TimeSpan.Zero);
        // the weekly period is 11-02 06:00Z .. 11-09 06:00Z (previous: 10-26 .. 11-02, with 1 incident)
        var r = _svc.Generate(ReportType.Weekly, end, end.AddMinutes(30));
        Assert.Contains("Compared to the previous week: opened +1 (previous: 1)", r.Text, StringComparison.Ordinal);
        Assert.Contains("previous week:", r.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void RunIfDue_GeneratesOnce_CatchesUpOnce_AndWritesWeeklyFile()
    {
        var first = _svc.RunIfDue();
        Assert.Equal([ReportType.Daily, ReportType.Weekly], first);
        Assert.Empty(_svc.RunIfDue());
        Assert.True(File.Exists(Path.Combine(_dir.Path, "reports", "weekly-2026-11-02.md")));
        Assert.NotNull(_store.LastReportGeneratedAt());

        _time.Advance(TimeSpan.FromHours(24)); // Tuesday 07:30 local: a new daily is due, no weekly
        Assert.Equal([ReportType.Daily], _svc.RunIfDue());

        _time.Advance(TimeSpan.FromDays(10)); // the service was down: one daily + one weekly catch-up, not 10
        Assert.Equal([ReportType.Daily, ReportType.Weekly], _svc.RunIfDue());
        Assert.Empty(_svc.RunIfDue());
        Assert.Equal(2, new[] { "daily", "weekly" }.Count(t => _store.LastReport(t) is not null));
    }

    [Fact]
    public void RunIfDue_DisabledDoesNothing()
    {
        _opt.Reports.Enabled = false;
        Assert.Empty(_svc.RunIfDue());
        Assert.Null(_store.LastReport("daily"));
    }

    [Fact]
    public void WeeklyFiles_AreRetainedAt26()
    {
        var dir = Path.Combine(_dir.Path, "reports");
        Directory.CreateDirectory(dir);
        for (var i = 0; i < 30; i++)
        {
            File.WriteAllText(Path.Combine(dir, $"weekly-2026-01-{i + 1:00}.md"), "x");
        }

        File.WriteAllText(Path.Combine(dir, "other.md"), "x");
        ReportService.PruneWeeklyFiles(dir, 26);
        var left = Directory.GetFiles(dir, "weekly-*.md").Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(26, left.Length);
        Assert.Equal("weekly-2026-01-05.md", left[0]);
        Assert.True(File.Exists(Path.Combine(dir, "other.md")));
    }

    [Fact]
    public void Migration2_AddsTablesToExistingV1Database()
    {
        Assert.Equal(SqliteStore.SchemaVersion, _store.CurrentVersion());
        Assert.True(SqliteStore.SchemaVersion >= 2);
        Assert.Null(_store.LastReport("daily"));
        Assert.Empty(_store.GetDiskSamples("C", Start, Start.AddDays(1)));
    }
}
