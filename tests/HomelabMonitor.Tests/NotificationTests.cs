using System.Net;
using HomelabMonitor.Domain;
using HomelabMonitor.Engine;
using HomelabMonitor.Notify;
using HomelabMonitor.Store;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static HomelabMonitor.Tests.T;

namespace HomelabMonitor.Tests;

public sealed class NotificationTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ManualTime _time = new(Start);
    private readonly SqliteStore _store;
    private readonly IncidentEngine _engine;
    private readonly NotificationPlanner _planner;
    private readonly NotifyOptions _opt = new()
    {
        Enabled = true,
        Rules = new()
        {
            ["unreachable"] = new() { Body = "{names} is unreachable", ReminderAfterHours = 6, WithDuration = true },
            ["backup"] = new() { Body = "no successful backup for 26+ hours", Daily = true },
            ["security"] = new() { Body = "IOC suspicion (details in Seq)" },
            ["disk"] = new() { Body = "low disk space or disk problem: {names}", Daily = true, WithDuration = true },
        },
    };
    private readonly HashSet<string> _muted = [];

    public NotificationTests()
    {
        _store = new SqliteStore(Path.Combine(_dir.Path, "n.db"));
        _store.Migrate();
        var crit = Severity.Crit;
        var cat = Catalog(
            ("dns.local", Def(err: crit, rule: "unreachable")),
            ("svc.proxy", Def(parents: ["proc.docker"])),
            ("proc.docker", Def()),
            ("http.media", Def(err: crit, rule: "unreachable", parents: ["dns.local", "svc.proxy"])),
            ("http.requests", Def(err: crit, rule: "unreachable", parents: ["dns.local"])),
            ("remote.access", Def(err: crit, rule: "unreachable")),
            ("backup.daily", Def(err: crit, rule: "backup")),
            ("security.ioc", Def(err: crit, rule: "security")),
            ("disk.free.D", Def(err: crit, rule: "disk")),
            ("http.dashboard", Def()),
            ("flap", Def(m: 3, err: crit, rule: "unreachable")),
            ("a1", Def(err: crit, rule: "unreachable")),
            ("a2", Def(err: crit, rule: "unreachable")),
            ("a3", Def(err: crit, rule: "unreachable")),
            ("a4", Def(err: crit, rule: "unreachable")),
            ("a5", Def(err: crit, rule: "unreachable")));
        _engine = new IncidentEngine(cat, _time, Budapest);
        _planner = new NotificationPlanner(cat, _store, _time, _opt);
    }

    public void Dispose() => _dir.Dispose();

    private int Step(params CheckResult[] results)
    {
        _store.ApplyEvents(_engine.ProcessBatch(results));
        return _planner.Plan(id => _muted.Contains(id));
    }

    private IReadOnlyList<OutboxMessage> Box(string? kind = null) => _store.GetOutbox(kind);

    [Fact]
    public void CritOpen_CreatesExactlyOneOpenMessage_WithMinimalText()
    {
        Step(Err("http.media"));
        Step(Err("http.media"));
        var m = Assert.Single(Box());
        Assert.Equal("open", m.Kind);
        Assert.Equal("urgent", m.Priority);
        Assert.StartsWith("[Homelab] CRITICAL: x is unreachable", m.Text, StringComparison.Ordinal);
        Assert.Contains("INC-20261102-001", m.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("192.", m.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void WarnOnlyIncident_DoesNotNotify()
    {
        Step(Err("http.dashboard"));
        Step(Err("proc.docker"));
        Assert.Empty(Box());
    }

    [Fact]
    public void Unreachable_ReminderAfter6Hours_OnlyOnce_ThenResolvedMessage()
    {
        Step(Err("remote.access"));
        _time.Advance(TimeSpan.FromHours(5.9));
        Step(Err("remote.access"));
        Assert.Single(Box());
        _time.Advance(TimeSpan.FromHours(0.2));
        Step(Err("remote.access"));
        Assert.Single(Box("reminder"));
        _time.Advance(TimeSpan.FromHours(20));
        Step(Err("remote.access"));
        Assert.Single(Box("reminder"));
        Step(Ok("remote.access"));
        var res = Assert.Single(Box("resolved"));
        Assert.Equal("default", res.Priority);
        Assert.Contains("Recovered", res.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Backup_RemindsDaily()
    {
        Step(Err("backup.daily"));
        for (var d = 1; d <= 3; d++)
        {
            _time.Advance(TimeSpan.FromHours(24));
            Step(Err("backup.daily"));
            Step(Err("backup.daily")); // same day again: dedupe
        }

        Assert.Equal(3, Box("reminder").Count);
    }

    [Fact]
    public void Security_NoReminder_Disk_Daily()
    {
        Step(Err("security.ioc"), Err("disk.free.D"));
        _time.Advance(TimeSpan.FromHours(48));
        Step(Err("security.ioc"), Err("disk.free.D"));
        Assert.Equal(1, Box("reminder").Count(m => m.Text.Contains("disk", StringComparison.Ordinal)));
        Assert.DoesNotContain(Box("reminder"), m => m.Text.Contains("IOC", StringComparison.Ordinal));
    }

    [Fact]
    public void ParentIncident_WithCritChildren_NotifiesOnce_AsFamilyService()
    {
        Step(Err("proc.docker"));
        Step(Err("svc.proxy"), Err("http.media"), Err("http.media"));
        _time.Advance(TimeSpan.FromMinutes(1));
        Step(Err("proc.docker"), Err("svc.proxy"), Err("http.media"), Err("http.media"));
        var open = Assert.Single(Box("open"));
        Assert.Contains("is unreachable", open.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void DnsIncident_IsOneNotification_ForAllDependentChecks()
    {
        Step(Err("dns.local"));
        Step(Err("http.media"), Err("http.requests"), Err("dns.local"));
        Assert.Single(Box("open"));
    }

    [Fact]
    public void Maintenance_NoOpenMessage_ButCreatedWhenWindowEndsAndStillFailing()
    {
        _muted.Add("http.requests");
        Step(Err("http.requests"));
        _time.Advance(TimeSpan.FromMinutes(30));
        Step(Err("http.requests"));
        Assert.Empty(Box());
        _muted.Clear(); // end of the window
        Step(Err("http.requests"));
        var m = Assert.Single(Box("open"));
        Assert.Contains("(for 30 min)", m.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ImmediateOpenMessage_HasNoZeroDuration()
    {
        Step(Err("http.media"));
        Step(Err("http.media"));
        var m = Assert.Single(Box("open"));
        Assert.DoesNotContain("(for", m.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Maintenance_IncidentResolvedInsideWindow_NeverNotifies()
    {
        _muted.Add("http.requests");
        Step(Err("http.requests"));
        Step(Ok("http.requests"));
        _muted.Clear();
        Step(Ok("http.requests"));
        Assert.Empty(Box());
    }

    [Fact]
    public void Flapping_DoesNotNotifyAgain()
    {
        Step(Err("flap"));
        for (var i = 0; i < 4; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            Step(i % 2 == 0 ? Ok("flap") : Err("flap"));
        }

        Assert.Equal(CheckState.Flapping, _engine.Get("flap")!.State);
        _time.Advance(TimeSpan.FromHours(7));
        _planner.Plan(_ => false);
        Assert.Empty(Box("reminder"));
        Assert.Single(Box("open"));
    }

    [Fact]
    public void Dedupe_SamePlanTwice_DoesNotDuplicate()
    {
        Step(Err("remote.access"));
        Assert.Equal(0, _planner.Plan(_ => false));
        Assert.Single(Box());
    }

    [Fact]
    public void Storm_MoreThanThreeCritOpensIn10Minutes_ProduceOneDigest()
    {
        foreach (var id in new[] { "a1", "a2", "a3" })
        {
            Step(Err(id));
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal(3, Box("open").Count(m => m.Status == "pending"));
        Step(Err("a4"));
        Step(Err("a5"));
        var digest = Assert.Single(Box("digest"));
        Assert.Contains("4 critical", digest.Text, StringComparison.Ordinal);
        Assert.Equal(5, Box("open").Count(m => m.Status == "merged"));
        Assert.Equal(0, Box("open").Count(m => m.Status == "pending"));
    }

    [Fact]
    public async Task Dispatcher_RateLimits_ToSixPerHour_AndRetriesWithBackoff()
    {
        for (var i = 0; i < 8; i++)
        {
            _store.TryEnqueue($"k{i}", null, "reminder", "urgent", "bell", "t", _time.GetUtcNow());
        }

        var sender = new FakeSender();
        var d = new OutboxDispatcher(_store, sender, _time, _opt, NullLogger<OutboxDispatcher>.Instance);
        Assert.Equal(6, await d.DispatchAsync(CancellationToken.None));
        Assert.Equal(0, await d.DispatchAsync(CancellationToken.None));
        _time.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(2, await d.DispatchAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Dispatcher_Failure_SchedulesExponentialRetry_AndNeverSendsWhenDisabled()
    {
        _store.TryEnqueue("k", null, "open", "urgent", "x", "t", _time.GetUtcNow());
        var sender = new FakeSender { Fail = true };
        var d = new OutboxDispatcher(_store, sender, _time, _opt, NullLogger<OutboxDispatcher>.Instance);
        await d.DispatchAsync(CancellationToken.None);
        Assert.Equal(1, sender.Calls);
        await d.DispatchAsync(CancellationToken.None); // not due yet
        Assert.Equal(1, sender.Calls);
        _time.Advance(TimeSpan.FromMinutes(1));
        await d.DispatchAsync(CancellationToken.None);
        Assert.Equal(2, sender.Calls);
        _time.Advance(TimeSpan.FromMinutes(1.5)); // after the 2nd failure the wait is 2 minutes
        await d.DispatchAsync(CancellationToken.None);
        Assert.Equal(2, sender.Calls);
        _time.Advance(TimeSpan.FromMinutes(1));
        sender.Fail = false;
        await d.DispatchAsync(CancellationToken.None);
        Assert.Equal(3, sender.Calls);
        Assert.Equal("sent", Box().Single().Status);

        var off = new NotifyOptions { Enabled = false };
        _store.TryEnqueue("k2", null, "open", "urgent", "x", "t", _time.GetUtcNow());
        var sender2 = new FakeSender();
        await new OutboxDispatcher(_store, sender2, _time, off, NullLogger<OutboxDispatcher>.Instance).DispatchAsync(CancellationToken.None);
        Assert.Equal(0, sender2.Calls);
    }

    [Fact]
    public async Task Dispatcher_ExpiresStaleMessages()
    {
        _store.TryEnqueue("k", null, "open", "urgent", "x", "t", _time.GetUtcNow());
        _time.Advance(TimeSpan.FromMinutes(121));
        var sender = new FakeSender();
        await new OutboxDispatcher(_store, sender, _time, _opt, NullLogger<OutboxDispatcher>.Instance).DispatchAsync(CancellationToken.None);
        Assert.Equal(0, sender.Calls);
        Assert.Equal("expired", Box().Single().Status);
    }

    [Fact]
    public async Task NtfySender_PostsTextWithHeaders_FromTopicFile()
    {
        var topic = Path.Combine(_dir.Path, "ntfy.topic");
        await File.WriteAllTextAsync(topic, "secret-topic-123\n");
        HttpRequestMessage? seen = null;
        string? body = null;
        var client = new HttpClient(new FakeHandler(r =>
        {
            seen = r;
            body = r.Content!.ReadAsStringAsync().Result;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var s = new NtfySender(client, new NotifyOptions { TopicFile = topic, BaseUrl = "https://ntfy.example" });
        var msg = new OutboxMessage(1, "k", "INC-1", "open", "urgent", "rotating_light", "café ✓", Start, "pending", 0, Start);
        await s.SendAsync(msg, CancellationToken.None);
        Assert.Equal(HttpMethod.Post, seen!.Method);
        Assert.Equal("https://ntfy.example/secret-topic-123", seen.RequestUri!.ToString());
        Assert.Equal("urgent", seen.Headers.GetValues("Priority").Single());
        Assert.Equal("rotating_light", seen.Headers.GetValues("Tags").Single());
        Assert.Equal("café ✓", body); // UTF-8 body
    }

    [Fact]
    public async Task NtfySender_ThrowsOnHttpError_WithoutLeakingUrl()
    {
        var topic = Path.Combine(_dir.Path, "ntfy.topic");
        await File.WriteAllTextAsync(topic, "secret-topic-123");
        var s = new NtfySender(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))), new NotifyOptions { TopicFile = topic });
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => s.SendAsync(
            new OutboxMessage(1, "k", null, "open", "urgent", "x", "t", Start, "pending", 0, Start), CancellationToken.None));
        Assert.DoesNotContain("secret", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, true, true, true)]
    [InlineData(4, true, true, false)]   // the last probe is too old
    [InlineData(1, false, true, false)]  // database not writable
    public void SelfCheck_Conditions(int probeAgeMin, bool dbOk, bool notifyEnabled, bool expected) =>
        Assert.Equal(expected, SelfCheck.IsHealthy(Start, Start.AddMinutes(-probeAgeMin), dbOk, null, notifyEnabled, 30));

    [Fact]
    public void SelfCheck_StuckOutbox_OnlyMattersWhenNotifyEnabled()
    {
        var oldDue = Start.AddMinutes(-31);
        Assert.False(SelfCheck.IsHealthy(Start, Start.AddMinutes(-1), true, oldDue, true, 30));
        Assert.True(SelfCheck.IsHealthy(Start, Start.AddMinutes(-1), true, oldDue, false, 30));
        Assert.False(SelfCheck.IsHealthy(Start, null, true, null, true, 30));
    }

    [Fact]
    public async Task Heartbeat_PingsOnlyWhenEnabledAndHealthy_AndRecordsSuccess()
    {
        var urlFile = Path.Combine(_dir.Path, "hb.url");
        await File.WriteAllTextAsync(urlFile, "https://hc.example/ping/abc");
        var calls = new List<string>();
        var client = new HttpClient(new FakeHandler(r =>
        {
            calls.Add(r.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var off = new HeartbeatPinger(new HeartbeatOptions { Enabled = false, UrlFile = urlFile }, _opt, client, _store, _time, NullLogger<HeartbeatPinger>.Instance);
        Assert.False(await off.TickAsync(_time.GetUtcNow(), CancellationToken.None));

        var on = new HeartbeatPinger(new HeartbeatOptions { Enabled = true, UrlFile = urlFile }, _opt, client, _store, _time, NullLogger<HeartbeatPinger>.Instance);
        Assert.False(await on.TickAsync(_time.GetUtcNow().AddMinutes(-10), CancellationToken.None));
        Assert.Empty(calls);
        Assert.True(await on.TickAsync(_time.GetUtcNow().AddSeconds(-30), CancellationToken.None));
        Assert.Equal("https://hc.example/ping/abc", Assert.Single(calls));
        Assert.NotNull(_store.GetMeta("heartbeat_last_ok"));
    }

    [Fact]
    public async Task Heartbeat_MissingUrlFile_DoesNotThrow()
    {
        var p = new HeartbeatPinger(new HeartbeatOptions { Enabled = true, UrlFile = Path.Combine(_dir.Path, "nope") }, _opt,
            new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))), _store, _time, NullLogger<HeartbeatPinger>.Instance);
        Assert.False(await p.TickAsync(_time.GetUtcNow(), CancellationToken.None));
    }

    private sealed class FakeSender : INotificationSender
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }

        public Task SendAsync(OutboxMessage message, CancellationToken ct)
        {
            Calls++;
            return Fail ? throw new HttpRequestException("x") : Task.CompletedTask;
        }
    }
}
