using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HomelabMonitor.Domain;
using HomelabMonitor.Engine;
using HomelabMonitor.Heavy;
using HomelabMonitor.Notify;
using HomelabMonitor.Probes;
using HomelabMonitor.Store;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static HomelabMonitor.Tests.T;

namespace HomelabMonitor.Tests;

/// <summary>Test host: temporary data directory, no background workers and no Seq (it cannot touch a real server or ProgramData).</summary>
public sealed class TestApp : IDisposable
{
    private readonly TempDir _dir = new();

    public WebApplicationFactory<Program> Factory { get; }

    public TestApp() => Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseSetting("Monitor:DataDir", _dir.Path);
        b.UseSetting("Monitor:RunBackground", "false");
        b.UseSetting("Seq:Url", "");
        b.UseSetting("Catalog:Checks:http.media:DisplayName", "Media server");
        b.UseSetting("Monitor:Reports:Uptime:0:Key", "media");
        b.UseSetting("Monitor:Reports:Uptime:0:Name", "Media server");
        b.UseSetting("Monitor:Reports:Uptime:0:Check", "http.media");
    });

    public string DataDir => _dir.Path;

    public void Dispose()
    {
        Factory.Dispose();
        _dir.Dispose();
    }
}

public sealed class HealthEndpointTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Health_ReturnsOk()
    {
        using var client = _app.Factory.CreateClient();
        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(File.Exists(Path.Combine(_app.DataDir, "homelab-monitor.db")));
    }

    [Fact]
    public async Task Summary_EmptyDb_IsOkWithNullUptime()
    {
        using var client = _app.Factory.CreateClient();
        var json = await client.GetFromJsonAsync<JsonElement>("/api/homepage/summary", TestContext.Current.CancellationToken);
        Assert.Equal("OK", json.GetProperty("status").GetString());
        Assert.Equal(0, json.GetProperty("openIncidents").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("uptime24h").GetProperty("media").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("heartbeatLastOkAt").ValueKind);
    }

    [Fact]
    public async Task Summary_ReflectsCritIncidentAndUptime_AndContainsNoSecrets()
    {
        using var client = _app.Factory.CreateClient();
        var store = _app.Factory.Services.GetRequiredService<SqliteStore>();
        var now = DateTimeOffset.UtcNow;
        store.InsertSamples(
        [
            new SampleRow(now.AddHours(-2), "http.media", CheckStatus.Ok, 1, CheckSource.Light, false),
            new SampleRow(now.AddHours(-1), "http.media", CheckStatus.Error, 1, CheckSource.Light, false),
        ]);
        var engine = new IncidentEngine(Catalog(("http.media", Def(err: Severity.Crit))), TimeProvider.System, Budapest);
        store.ApplyEvents(engine.Process(Err("http.media")));
        var text = await client.GetStringAsync("/api/homepage/summary", TestContext.Current.CancellationToken);
        var json = JsonDocument.Parse(text).RootElement;
        Assert.Equal("CRIT", json.GetProperty("status").GetString());
        Assert.Equal(1, json.GetProperty("openIncidents").GetInt32());
        Assert.Equal(50.0, json.GetProperty("uptime24h").GetProperty("media").GetDouble());
        Assert.DoesNotContain("192.168", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Report_EmptyThenSeeded_AndSummaryHasLastReportAt()
    {
        using var client = _app.Factory.CreateClient();
        var empty = await client.GetFromJsonAsync<JsonElement>("/api/homepage/report", TestContext.Current.CancellationToken);
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("lastDailyAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("lastWeeklyAt").ValueKind);

        var store = _app.Factory.Services.GetRequiredService<SqliteStore>();
        var now = DateTimeOffset.UtcNow;
        store.InsertReport("daily", now, now, new string('x', 700), "full text");
        store.InsertReport("weekly", now, now, "weekly", "full text");
        var r = await client.GetFromJsonAsync<JsonElement>("/api/homepage/report", TestContext.Current.CancellationToken);
        Assert.NotEqual(JsonValueKind.Null, r.GetProperty("lastDailyAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, r.GetProperty("lastWeeklyAt").ValueKind);
        Assert.Equal(500, r.GetProperty("lastDailyText").GetString()!.Length);
        Assert.DoesNotContain("full text", r.ToString(), StringComparison.Ordinal);
        var summary = await client.GetFromJsonAsync<JsonElement>("/api/homepage/summary", TestContext.Current.CancellationToken);
        Assert.NotEqual(JsonValueKind.Null, summary.GetProperty("lastReportAt").ValueKind);
        var post = await client.PostAsync("/api/homepage/report", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
    }

    [Fact]
    public async Task Api_IsReadOnly()
    {
        using var client = _app.Factory.CreateClient();
        var r = await client.PostAsync("/api/homepage/summary", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, r.StatusCode);
    }
}

public sealed class MonitorCycleTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly TempDir _heavyDir = new();
    private readonly ManualTime _time = new(Start);

    public void Dispose()
    {
        _dir.Dispose();
        _heavyDir.Dispose();
    }

    private sealed class FakeProbe : IProbe
    {
        public CheckStatus Status { get; set; } = CheckStatus.Error;
        public IReadOnlyCollection<string> CheckIds { get; } = ["http.media"];
        public TimeSpan Interval => TimeSpan.FromMinutes(1);

        public Task<IReadOnlyList<CheckResult>> RunAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CheckResult>>([new CheckResult("http.media", Status, DateTimeOffset.UtcNow)]);
    }

    [Fact]
    public async Task FullCycle_OpensIncident_WritesOutboxShadowRows_AndResolves()
    {
        var store = new SqliteStore(Path.Combine(_dir.Path, "c.db"));
        store.Migrate();
        var cat = Catalog(
            ("http.media", Def(n: 3, m: 3, err: Severity.Crit, rule: "unreachable")),
            ("monitor.heavy.stale", Def()));
        var engine = new IncidentEngine(cat, _time, Budapest);
        var notify = new NotifyOptions { Enabled = false, Rules = new() { ["unreachable"] = new() { Body = "{names} is unreachable" } } };
        var opts = new MonitorOptions { DataDir = _dir.Path };
        var probe = new FakeProbe();
        var runner = new ProbeRunner([probe], _time, TimeSpan.FromSeconds(5));
        var heavy = new HeavyIngest(new HeavyOptions { Directory = _heavyDir.Path }, store, cat, _time, NullLogger<HeavyIngest>.Instance);
        var status = new MonitorStatus();
        var cycle = new MonitorCycle(runner, heavy, store, engine, new NotificationPlanner(cat, store, _time, notify),
            new MaintenanceCalendar(new MaintenanceOptions(), Budapest, Start.AddYears(-1)), opts, _time, status, NullLogger<MonitorCycle>.Instance);

        for (var i = 0; i < 3; i++)
        {
            await cycle.RunAsync(CancellationToken.None);
            _time.Advance(TimeSpan.FromSeconds(60));
        }

        Assert.Single(store.GetOpenIncidents(), i => i.CheckId == "http.media"); // + monitor.heavy.stale (no heavy file)
        Assert.NotNull(status.LastLightProbeAt);
        var row = Assert.Single(store.GetOutbox("open"));
        Assert.Equal("pending", row.Status); // shadow mode: the row exists, but the dispatcher does not send it (Enabled=false)

        probe.Status = CheckStatus.Ok;
        for (var i = 0; i < 3; i++)
        {
            await cycle.RunAsync(CancellationToken.None);
            _time.Advance(TimeSpan.FromSeconds(60));
        }

        Assert.DoesNotContain(store.GetOpenIncidents(), i => i.CheckId == "http.media");
        Assert.Single(store.GetOutbox("resolved"));
        Assert.NotEmpty(store.LoadStates());
    }
}
