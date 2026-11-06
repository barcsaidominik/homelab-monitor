using HomelabMonitor.Domain;
using HomelabMonitor.Engine;
using HomelabMonitor.Heavy;
using HomelabMonitor.Store;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static HomelabMonitor.Tests.T;

namespace HomelabMonitor.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hm-test-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
        }
    }
}

public sealed class StoreTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private SqliteStore NewStore()
    {
        var s = new SqliteStore(Path.Combine(_dir.Path, "t.db"));
        s.Migrate();
        return s;
    }

    [Fact]
    public void Migrate_CreatesSchema_AndIsIdempotent()
    {
        var s = NewStore();
        Assert.Equal(SqliteStore.SchemaVersion, s.CurrentVersion());
        s.Migrate();
        Assert.Equal(SqliteStore.SchemaVersion, s.CurrentVersion());
        s.InsertSamples([new SampleRow(Start, "a", CheckStatus.Ok, 5, CheckSource.Light, false)]);
        Assert.Equal(1, s.GetUptimeCounts("a", Start.AddHours(-1), Start.AddHours(1)).Ok);
    }

    [Fact]
    public void Uptime_ExcludesMaintenanceAndUnknown()
    {
        var s = NewStore();
        var rows = new List<SampleRow>();
        for (var i = 0; i < 90; i++)
        {
            rows.Add(new SampleRow(Start.AddMinutes(i), "a", CheckStatus.Ok, 1, CheckSource.Light, false));
        }

        for (var i = 90; i < 95; i++)
        {
            rows.Add(new SampleRow(Start.AddMinutes(i), "a", CheckStatus.Error, 1, CheckSource.Light, false));
        }

        for (var i = 95; i < 100; i++)
        {
            rows.Add(new SampleRow(Start.AddMinutes(i), "a", CheckStatus.Unknown, 1, CheckSource.Light, false));
        }

        for (var i = 100; i < 110; i++)
        {
            rows.Add(new SampleRow(Start.AddMinutes(i), "a", CheckStatus.Error, 1, CheckSource.Light, true));
        }

        s.InsertSamples(rows);
        var u = UptimeCalculator.Compute(s.GetUptimeCounts("a", Start, Start.AddHours(3)));
        Assert.Equal(94.737, u.Percent);
        Assert.Equal(5, u.UnknownMinutes);
        Assert.Equal(10, u.MaintenanceMinutes);
        Assert.Null(UptimeCalculator.Compute(new UptimeCounts(0, 0, 0, 3, 0)).Percent);
    }

    [Fact]
    public void RollupAndPurge_Work()
    {
        var s = NewStore();
        var old = Start.AddDays(-40);
        s.InsertSamples([
            new SampleRow(old, "a", CheckStatus.Ok, 1, CheckSource.Light, false),
            new SampleRow(Start, "a", CheckStatus.Ok, 1, CheckSource.Light, false),
            new SampleRow(Start.AddMinutes(1), "a", CheckStatus.Error, 1, CheckSource.Light, false)]);
        s.Rollup(Start.AddMinutes(30));
        var r = Assert.Single(s.GetRollups("a"));
        Assert.Equal(1, r.Counts.Ok);
        Assert.Equal(1, r.Counts.Error);
        s.Purge(Start);
        Assert.Equal(0, s.GetUptimeCounts("a", old.AddHours(-1), old.AddHours(1)).Ok);
        Assert.Equal(1, s.GetUptimeCounts("a", Start.AddHours(-1), Start.AddHours(1)).Ok);
    }

    [Fact]
    public void Events_CreateAndResolveIncident_AndPersistEngineState()
    {
        var s = NewStore();
        var time = new ManualTime(Start);
        var cat = Catalog(("p", Def()), ("c", Def(parents: ["p"])));
        var e = new IncidentEngine(cat, time, Budapest);
        s.ApplyEvents(e.ProcessBatch([Err("p"), Err("c")]));
        s.SaveStates(e.Snapshot());
        var open = Assert.Single(s.GetOpenIncidents());
        Assert.Equal("p", open.CheckKey);
        Assert.Contains("c", open.Affected);

        var e2 = new IncidentEngine(cat, time, Budapest, s.LoadStates(), s.GetAllIncidentIds());
        time.Advance(TimeSpan.FromMinutes(5));
        s.ApplyEvents(e2.ProcessBatch([Ok("p"), Ok("c")]));
        Assert.Empty(s.GetOpenIncidents());
        var planning = Assert.Single(s.GetIncidentsForPlanning(Start));
        Assert.Equal(CheckState.Resolved, planning.State);
        Assert.NotNull(planning.ResolvedAt);
    }

    [Fact]
    public void Outbox_DedupesByKey_AndTracksMeta()
    {
        var s = NewStore();
        Assert.True(s.TryEnqueue("INC-1:open", "INC-1", "open", "urgent", "x", "t", Start));
        Assert.False(s.TryEnqueue("INC-1:open", "INC-1", "open", "urgent", "x", "t", Start));
        Assert.True(s.OutboxKeyExists("INC-1:open"));
        s.SetMeta("k", "v");
        Assert.Equal("v", s.GetMeta("k"));
        Assert.True(s.CheckWritable(Start));
    }

    [Fact]
    public void HeavyRuns_AreTrackedOnce()
    {
        var s = NewStore();
        Assert.False(s.IsRunProcessed("r1"));
        s.MarkRunProcessed("r1", Start, Start.AddMinutes(1), Start, true);
        Assert.True(s.IsRunProcessed("r1"));
        Assert.Equal(Start.AddMinutes(1), s.LastHeavyFinishedAt());
    }
}

public sealed class HeavyIngestTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly TempDir _heavy = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        _dir.Dispose();
        _heavy.Dispose();
    }

    private static string Sample => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "heavy-sample.json"));

    private (HeavyIngest ingest, SqliteStore store) Make()
    {
        var store = new SqliteStore(Path.Combine(_dir.Path, "h.db"));
        store.Migrate();
        var cat = Catalog(
            ("backup.daily", new CheckDefinition { Source = CheckSource.Heavy }),
            ("remote.access", new CheckDefinition { Source = CheckSource.Heavy, OnError = Severity.Crit }),
            ("disk.free.D", new CheckDefinition { Source = CheckSource.Heavy }),
            ("fw.profile", new CheckDefinition { Source = CheckSource.Heavy }),
            ("proc.media", new CheckDefinition { Source = CheckSource.Light }));
        return (new HeavyIngest(new HeavyOptions { Directory = _heavy.Path, MaxAgeHours = 12 }, store, cat, _time, NullLogger<HeavyIngest>.Instance), store);
    }

    [Fact]
    public void Parser_ReadsSampleFixture()
    {
        Assert.True(HeavyRunParser.TryParse(Sample, out var run, out _));
        Assert.Equal("eac3cd32-0000-4000-8000-000000000001", run!.RunId);
        Assert.Equal("HOMESERVER", run.Host);
        Assert.Equal(6, run.Results.Count);
        var remote = run.Results.Single(r => r.CheckId == "remote.access");
        Assert.Equal(CheckStatus.Error, remote.Status);
        Assert.Equal("Remote access: failed", Assert.Single(remote.Messages));
        Assert.Equal(CheckStatus.Unknown, run.Results.Single(r => r.CheckId == "fw.profile").Status);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"schema\":\"other/1\",\"runId\":\"x\"}")]
    [InlineData("{\"schema\":\"homelab.heavy/1\"}")]
    public void Parser_RejectsInvalid(string json) => Assert.False(HeavyRunParser.TryParse(json, out _, out _));

    [Fact]
    public void Poll_IngestsOnlyCatalogHeavyChecks_AndEachRunOnce()
    {
        var (ingest, _) = Make();
        File.WriteAllText(Path.Combine(_heavy.Path, "heavy-20261102T082721Z.json"), Sample);
        var batch = ingest.Poll();
        Assert.Equal(["backup.daily", "disk.free.D", "fw.profile", "remote.access"], batch.Results.Select(r => r.CheckId).Order().ToArray());
        Assert.All(batch.Results, r => Assert.Equal(CheckSource.Heavy, r.Source));
        ingest.MarkProcessed(batch);
        Assert.Empty(ingest.Poll().Results);
    }

    [Fact]
    public void Poll_StampsResultsWithRunFinishedAt_NotIngestTime()
    {
        var (ingest, _) = Make();
        Assert.True(HeavyRunParser.TryParse(Sample, out var run, out _));
        _time.Set(run!.FinishedAt.AddMinutes(40));
        File.WriteAllText(Path.Combine(_heavy.Path, "heavy-20261102T082721Z.json"), Sample);

        var batch = ingest.Poll();

        Assert.NotEmpty(batch.Results);
        Assert.All(batch.Results, r => Assert.Equal(run.FinishedAt, r.ObservedAt));
    }

    [Fact]
    public void Poll_SkipsTooOldRuns_ButMarksThem()
    {
        var (ingest, store) = Make();
        _time.Set(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));
        File.WriteAllText(Path.Combine(_heavy.Path, "heavy-old.json"), Sample);
        var batch = ingest.Poll();
        Assert.Empty(batch.Results);
        ingest.MarkProcessed(batch);
        Assert.True(store.IsRunProcessed("eac3cd32-0000-4000-8000-000000000001"));
    }

    [Fact]
    public void Poll_IgnoresMissingDirectoryAndBrokenFiles()
    {
        var (ingest, _) = Make();
        File.WriteAllText(Path.Combine(_heavy.Path, "heavy-bad.json"), "{ nope");
        Assert.Empty(ingest.Poll().Results);
        Directory.Delete(_heavy.Path, true);
        Assert.Empty(ingest.Poll().Results);
    }

    [Fact]
    public void Stale_FiresAfterSevenHours()
    {
        var (ingest, _) = Make();
        var now = _time.GetUtcNow();
        Assert.Equal(CheckStatus.Warn, ingest.EvaluateStale(null).Status);
        Assert.Equal(CheckStatus.Ok, ingest.EvaluateStale(now.AddHours(-6.9)).Status);
        Assert.Equal(CheckStatus.Warn, ingest.EvaluateStale(now.AddHours(-7.1)).Status);
    }
}

public sealed class MaintenanceTests
{
    private static MaintenanceCalendar Cal(DateTimeOffset boot) => new(new MaintenanceOptions
    {
        PostBootMinutes = 10,
        Windows =
        [
            new MaintenanceWindowOptions { Day = DayOfWeek.Wednesday, Start = "02:00", End = "03:30" },
            new MaintenanceWindowOptions { Day = DayOfWeek.Saturday, Start = "05:45", End = "06:30" },
        ],
    }, Budapest, boot);

    // 2026-11-04 is a Wednesday; CET = UTC+1
    private static DateTimeOffset Local(int day, int h, int m) => new DateTimeOffset(2026, 11, day, h, m, 0, TimeSpan.FromHours(1)).ToUniversalTime();

    [Theory]
    [InlineData(4, 1, 59, false)]
    [InlineData(4, 2, 0, true)]
    [InlineData(4, 3, 29, true)]
    [InlineData(4, 3, 30, false)]
    [InlineData(7, 5, 45, true)]
    [InlineData(7, 6, 30, false)]
    [InlineData(5, 2, 30, false)]
    public void Windows_AreHalfOpenInLocalTime(int day, int h, int m, bool expected) =>
        Assert.Equal(expected, Cal(Start.AddYears(-1)).IsScheduled(Local(day, h, m)));

    [Fact]
    public void PostBoot_TenMinutes()
    {
        var boot = Local(2, 12, 0);
        var cal = Cal(boot);
        Assert.True(cal.IsScheduled(boot.AddMinutes(9)));
        Assert.False(cal.IsScheduled(boot.AddMinutes(10)));
    }

    [Fact]
    public void Mute_MatchesExactWildcardAndExpires()
    {
        var now = Start;
        IReadOnlyList<MuteEntry> m = [new MuteEntry("http.*", now.AddHours(1), ""), new MuteEntry("dns.local", now.AddMinutes(-1), "")];
        Assert.True(MaintenanceCalendar.IsMuted(m, "http.media", now));
        Assert.False(MaintenanceCalendar.IsMuted(m, "dns.local", now));
        Assert.False(MaintenanceCalendar.IsMuted(m, "http.media", now.AddHours(1)));
        Assert.True(MaintenanceCalendar.IsMuted([new MuteEntry("*", now.AddHours(1), "")], "x", now));
    }

    [Fact]
    public void MuteFile_IsTolerant()
    {
        using var d = new TempDir();
        var p = Path.Combine(d.Path, "mute.json");
        Assert.Empty(MuteFile.Load(p));
        File.WriteAllText(p, "garbage");
        Assert.Empty(MuteFile.Load(p));
        File.WriteAllText(p, "[{\"check\":\"http.media\",\"until\":\"2030-01-01T00:00:00Z\",\"reason\":\"x\"},{\"nocheck\":1}]");
        Assert.Equal("http.media", Assert.Single(MuteFile.Load(p)).Check);
    }
}

