using System.Diagnostics;
using System.Net;
using System.Text.Json;
using HomelabMonitor.Domain;
using HomelabMonitor.Probes;
using Xunit;

namespace HomelabMonitor.Tests;

public sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(respond(request));
}

public sealed class ThrowingHandler(Exception ex) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw ex;
}

public sealed class DnsTests
{
    [Fact]
    public void EncodeQuery_ProducesExpectedWireFormat()
    {
        var bytes = DnsMessage.EncodeQuery(0x1234, "home.example.net");
        Assert.Equal(0x12, bytes[0]);
        Assert.Equal(0x34, bytes[1]);
        Assert.Equal(0x01, bytes[2]); // RD
        Assert.Equal(1, bytes[5]);    // QDCOUNT
        Assert.Equal(4, bytes[12]);
        Assert.Equal("home", System.Text.Encoding.ASCII.GetString(bytes, 13, 4));
        Assert.Equal(0, bytes[^5]);
        Assert.Equal(1, bytes[^3]);   // QTYPE A
        Assert.Equal(1, bytes[^1]);   // QCLASS IN
    }

    [Fact]
    public void EncodeQuery_RejectsInvalidLabel() => Assert.Throws<ArgumentException>(() => DnsMessage.EncodeQuery(1, "a..b"));

    [Fact]
    public void Decode_ParsesARecord_WithCompressionPointer()
    {
        var q = DnsMessage.EncodeQuery(0xBEEF, "a.example");
        var resp = q.ToList();
        resp[2] = 0x81;
        resp[3] = 0x80; // QR, RD, RA, RCODE 0
        resp[7] = 1;    // ANCOUNT
        resp.AddRange([0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 192, 0, 2, 10]);
        var r = DnsMessage.Decode(resp.ToArray());
        Assert.Equal(0xBEEF, r.Id);
        Assert.True(r.IsResponse);
        Assert.Equal(0, r.ResponseCode);
        Assert.Equal(IPAddress.Parse("192.0.2.10"), Assert.Single(r.Addresses));
    }

    [Fact]
    public void Decode_ReportsNxDomain_AndRejectsTruncatedPackets()
    {
        var q = DnsMessage.EncodeQuery(1, "a.example");
        q[2] = 0x81;
        q[3] = 0x83; // NXDOMAIN
        Assert.Equal(3, DnsMessage.Decode(q).ResponseCode);
        Assert.Throws<FormatException>(() => DnsMessage.Decode(new byte[5]));
        var bad = q.ToList();
        bad[7] = 1;
        Assert.Throws<FormatException>(() => DnsMessage.Decode(bad.ToArray()));
    }
}

public sealed class ProbeTests
{
    private static readonly ManualTime Time = new(T.Start);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static HttpEndpointProbe Http(HttpProbeOptions o, Func<HttpRequestMessage, HttpResponseMessage> f) =>
        new(o, new HttpClient(new FakeHandler(f)), Time, Timeout);

    private static HttpResponseMessage Resp(HttpStatusCode code, string body = "") => new(code) { Content = new StringContent(body) };

    [Theory]
    [InlineData(HttpStatusCode.OK, "Healthy", CheckStatus.Ok)]
    [InlineData(HttpStatusCode.OK, "Unhealthy", CheckStatus.Error)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Healthy", CheckStatus.Error)]
    public async Task HealthEndpoint_RequiresExactBody(HttpStatusCode code, string body, CheckStatus expected)
    {
        var p = Http(new HttpProbeOptions { CheckId = "http.media", Url = "https://m/health", ExpectBody = "Healthy" }, _ => Resp(code, body));
        var r = Assert.Single(await p.RunAsync(CancellationToken.None));
        Assert.Equal(expected, r.Status);
        Assert.Equal("http.media", r.CheckId);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, CheckStatus.Ok)]
    [InlineData(HttpStatusCode.Found, CheckStatus.Ok)]
    [InlineData(HttpStatusCode.InternalServerError, CheckStatus.Error)]
    public async Task StatusRange_IsHonoured(HttpStatusCode code, CheckStatus expected)
    {
        var p = Http(new HttpProbeOptions { CheckId = "http.dashboard", Url = "https://h/", MinStatus = 200, MaxStatus = 399 }, _ => Resp(code));
        Assert.Equal(expected, (await p.RunAsync(CancellationToken.None))[0].Status);
    }

    [Fact]
    public async Task ConnectionFailure_IsError_NotException()
    {
        var p = new HttpEndpointProbe(new HttpProbeOptions { CheckId = "x", Url = "https://h/" },
            new HttpClient(new ThrowingHandler(new HttpRequestException("boom"))), Time, Timeout);
        Assert.Equal(CheckStatus.Error, (await p.RunAsync(CancellationToken.None))[0].Status);
    }

    [Fact]
    public async Task DnsProbe_OkOnlyWhenAllNamesResolve()
    {
        var ok = new DnsResponse(1, true, 0, false, [IPAddress.Loopback]);
        var nx = new DnsResponse(1, true, 3, false, []);
        var server = new IPEndPoint(IPAddress.Loopback, 53);
        var good = new DnsProbe("dns.local", server, ["a", "b", "c"], new FakeDns(_ => ok), Time, Timeout);
        Assert.Equal(CheckStatus.Ok, (await good.RunAsync(CancellationToken.None))[0].Status);
        var bad = new DnsProbe("dns.local", server, ["a", "b", "c"], new FakeDns(n => n == "b" ? nx : ok), Time, Timeout);
        var r = (await bad.RunAsync(CancellationToken.None))[0];
        Assert.Equal(CheckStatus.Error, r.Status);
        Assert.Contains("1/3", r.Message);
        var timeout = new DnsProbe("dns.local", server, ["a"], new FakeDns(_ => throw new System.Net.Sockets.SocketException()), Time, Timeout);
        Assert.Equal(CheckStatus.Error, (await timeout.RunAsync(CancellationToken.None))[0].Status);
    }

    [Fact]
    public async Task ServiceProbe_MapsStatus_AndMissingServiceIsUnknown()
    {
        var map = new Dictionary<string, string> { ["svc.a"] = "A", ["svc.b"] = "B", ["svc.c"] = "C" };
        var reader = new FakeServices(new() { ["A"] = "Running", ["B"] = "Stopped" });
        var res = (await new ServiceProbe(map, reader, Time).RunAsync(CancellationToken.None)).ToDictionary(r => r.CheckId);
        Assert.Equal(CheckStatus.Ok, res["svc.a"].Status);
        Assert.Equal(CheckStatus.Error, res["svc.b"].Status);
        Assert.Equal(CheckStatus.Unknown, res["svc.c"].Status);
    }

    [Fact]
    public async Task ProcessProbe_ReportsPresence()
    {
        var map = new Dictionary<string, string> { ["proc.a"] = "a", ["proc.b"] = "b" };
        var res = (await new ProcessProbe(map, new FakeProcs(["a"]), Time).RunAsync(CancellationToken.None)).ToDictionary(r => r.CheckId);
        Assert.Equal(CheckStatus.Ok, res["proc.a"].Status);
        Assert.Equal(CheckStatus.Error, res["proc.b"].Status);
    }

    [Fact]
    public async Task TlsProbe_OkAndFailure_UseTheConfiguredCheckId()
    {
        var o = new TlsProbeOptions { CheckId = "svc.proxy", Address = "127.0.0.1", Sni = "home.example.net" };
        var ok = new TlsHandshakeProbe(o, new FakeTls(null), Time, Timeout);
        var r = Assert.Single(await ok.RunAsync(CancellationToken.None));
        Assert.Equal(("svc.proxy", CheckStatus.Ok), (r.CheckId, r.Status));
        var bad = new TlsHandshakeProbe(o, new FakeTls(new System.Net.Sockets.SocketException()), Time, Timeout);
        Assert.Equal(CheckStatus.Error, (await bad.RunAsync(CancellationToken.None))[0].Status);
    }

    [Fact]
    public async Task ProbeRunner_TurnsExceptionsIntoUnknown_AndHonoursIntervals()
    {
        var time = new ManualTime(T.Start);
        var boom = new BoomProbe();
        var runner = new ProbeRunner([boom], time, Timeout);
        var r = Assert.Single(await runner.RunDueAsync(CancellationToken.None));
        Assert.Equal(CheckStatus.Unknown, r.Status);
        Assert.Empty(await runner.RunDueAsync(CancellationToken.None)); // a 5-minute probe is not due yet
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Single(await runner.RunDueAsync(CancellationToken.None));
    }

    private static JsonApiProbeOptions JobsOptions(int stuckMinutes = 30) => new()
    {
        Url = "http://127.0.0.1:5555/api/status",
        Thresholds = [new JsonThresholdRule { CheckId = "jobs.failures", Field = "failed24h", WarnAtOrAbove = 3, Message = "{value} failed jobs in 24 h" }],
        Stall = new JsonStallRule
        {
            CheckId = "jobs.stuck", ActiveField = "activeJobs", ProgressField = "currentJob.progressPercent",
            KeyFields = ["currentJob.name", "currentJob.worker"], PausedField = "status", PausedValue = "paused", Minutes = stuckMinutes,
        },
    };

    private static JsonApiProbe Jobs(ManualTime t, int stuckMinutes = 30) =>
        new(JobsOptions(stuckMinutes), new HttpClient(new FakeHandler(_ => Resp(HttpStatusCode.OK))), t, Timeout);

    private static IReadOnlyList<CheckResult> Eval(JsonApiProbe p, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return p.Evaluate(doc.RootElement.Clone(), Stopwatch.StartNew());
    }

    [Fact]
    public void JsonApi_ThresholdRule_WarnsAtThreshold_WithFormattedMessage()
    {
        var p = Jobs(new ManualTime(T.Start));
        Assert.Equal(CheckStatus.Ok, Eval(p, "{\"failed24h\":2,\"activeJobs\":0}")[0].Status);
        var warn = Eval(p, "{\"failed24h\":3,\"activeJobs\":0}")[0];
        Assert.Equal((CheckStatus.Warn, "3 failed jobs in 24 h", 3d), (warn.Status, warn.Message, warn.Value!.Value));
    }

    [Fact]
    public void JsonApi_MissingFields_AreUnknown()
    {
        var p = Jobs(new ManualTime(T.Start));
        var r = Eval(p, "{\"status\":\"ok\"}");
        Assert.All(r, x => Assert.Equal(CheckStatus.Unknown, x.Status));
        var r2 = Eval(p, "{\"failed24h\":0,\"activeJobs\":1,\"currentJob\":{}}");
        Assert.Equal(CheckStatus.Unknown, r2.Single(x => x.CheckId == "jobs.stuck").Status);
    }

    [Fact]
    public void JsonApi_StuckAfter30MinutesUnchangedProgress_ResetsOnChange()
    {
        var t = new ManualTime(T.Start);
        var p = Jobs(t);
        const string Job = "{\"status\":\"encoding\",\"failed24h\":0,\"activeJobs\":1,\"currentJob\":{\"name\":\"S02E01\",\"worker\":\"worker-1\",\"progressPercent\":41,\"etaSeconds\":169,\"speed\":\"3.41×\",\"isStale\":false}}";
        Assert.Equal(CheckStatus.Ok, Eval(p, Job)[1].Status);
        t.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(CheckStatus.Ok, Eval(p, Job)[1].Status);
        t.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(CheckStatus.Warn, Eval(p, Job)[1].Status);
        Assert.Equal(CheckStatus.Ok, Eval(p, Job.Replace("\"progressPercent\":41", "\"progressPercent\":42"))[1].Status);
        t.Advance(TimeSpan.FromMinutes(40));
        Assert.Equal(CheckStatus.Ok, Eval(p, "{\"failed24h\":0,\"activeJobs\":0}")[1].Status);
    }

    [Fact]
    public void JsonApi_OtherJobOrWorker_ResetsStuckTimer()
    {
        var t = new ManualTime(T.Start);
        var p = Jobs(t);
        const string Job = "{\"failed24h\":0,\"activeJobs\":1,\"currentJob\":{\"name\":\"A\",\"worker\":\"worker-1\",\"progressPercent\":10}}";
        Assert.Equal(CheckStatus.Ok, Eval(p, Job)[1].Status);
        t.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(CheckStatus.Ok, Eval(p, Job.Replace("\"A\"", "\"B\""))[1].Status);
        t.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(CheckStatus.Ok, Eval(p, Job.Replace("\"A\"", "\"B\"").Replace("worker-1", "worker-2"))[1].Status);
    }

    [Fact]
    public void JsonApi_NullProgressOrPaused_IsOkNotUnknown()
    {
        var t = new ManualTime(T.Start);
        var p = Jobs(t);
        // waiting for another step: progressPercent is null on purpose
        Assert.Equal(CheckStatus.Ok, Eval(p, "{\"failed24h\":0,\"activeJobs\":1,\"currentJob\":{\"name\":\"A\",\"worker\":\"w\",\"progressPercent\":null}}")[1].Status);
        // paused: the progress rightly stands still, no WARN even after 60 minutes
        const string Paused = "{\"status\":\"paused\",\"failed24h\":0,\"activeJobs\":1,\"currentJob\":{\"name\":\"A\",\"worker\":\"w\",\"progressPercent\":50}}";
        Assert.Equal(CheckStatus.Ok, Eval(p, Paused)[1].Status);
        t.Advance(TimeSpan.FromMinutes(60));
        Assert.Equal(CheckStatus.Ok, Eval(p, Paused)[1].Status);
    }

    [Fact]
    public void JsonApi_ContractChange_IsUnknown()
    {
        var p = Jobs(new ManualTime(T.Start));
        Assert.Equal(CheckStatus.Unknown, Eval(p, "{\"failed24h\":0,\"activeJobs\":1,\"currentJob\":{\"id\":\"j1\",\"progress\":41.5}}")[1].Status);
    }

    [Fact]
    public async Task JsonApi_UnreachableApi_IsUnknownForAllChecks()
    {
        var p = new JsonApiProbe(JobsOptions(), new HttpClient(new ThrowingHandler(new HttpRequestException())), Time, Timeout);
        var results = await p.RunAsync(CancellationToken.None);
        Assert.Equal(["jobs.failures", "jobs.stuck"], results.Select(r => r.CheckId));
        Assert.All(results, r => Assert.Equal(CheckStatus.Unknown, r.Status));
    }

    [Fact]
    public void JsonApi_WithoutStallRule_ProducesOnlyThresholdChecks()
    {
        var o = JobsOptions();
        o.Stall = null;
        var p = new JsonApiProbe(o, new HttpClient(new FakeHandler(_ => Resp(HttpStatusCode.OK))), Time, Timeout);
        Assert.Equal(["jobs.failures"], p.CheckIds);
    }

    [Fact]
    public void ProbeHttpClient_ForceLanNeedsLanAddress()
    {
        var http = new List<HttpProbeOptions> { new() { CheckId = "x", Url = "https://a.example/", ForceLan = true } };
        using var c = ProbeHttpClientFactory.Create(new ProbeOptions { LanAddress = "192.0.2.10", Http = http });
        Assert.NotNull(c);
        Assert.Throws<InvalidOperationException>(() => ProbeHttpClientFactory.Create(new ProbeOptions { Http = http }));
    }

    [Fact]
    public void ProbeFactory_EmptyConfiguration_BuildsNoProbes()
    {
        var runner = ProbeFactory.Build(new ProbeOptions(), Time);
        Assert.Empty(runner.Probes);
    }

    [Fact]
    public void ProbeFactory_BuildsEveryConfiguredKind()
    {
        var o = new ProbeOptions
        {
            Http = [new HttpProbeOptions { CheckId = "http.a", Url = "http://127.0.0.1/" }],
            Dns = [new DnsProbeOptions { CheckId = "dns.a", Server = "127.0.0.1", Names = ["a.example"] }],
            Tls = [new TlsProbeOptions { CheckId = "tls.a", Address = "127.0.0.1", Sni = "a.example" }],
            Services = new() { ["svc.a"] = "A" },
            Processes = new() { ["proc.a"] = "a" },
            JsonApi = [JobsOptions()],
            FanState = new FanStateProbeOptions { Enabled = true, StatePath = "x.json" },
        };
        var ids = ProbeFactory.Build(o, Time).Probes.SelectMany(p => p.CheckIds).Order(StringComparer.Ordinal);
        Assert.Equal(["dns.a", "http.a", "jobs.failures", "jobs.stuck", "proc.a", "svc.a", "thermal.cpu", "thermal.fan", "tls.a"], ids);
    }

    [Fact]
    public void ProbeFactory_RejectsDuplicateCheckIds()
    {
        var o = new ProbeOptions
        {
            Http = [new HttpProbeOptions { CheckId = "dup", Url = "http://127.0.0.1/" }],
            Processes = new() { ["dup"] = "a" },
        };
        var ex = Assert.Throws<InvalidOperationException>(() => ProbeFactory.Build(o, Time));
        Assert.Contains("dup", ex.Message);
    }

    private sealed class FakeDns(Func<string, DnsResponse> f) : IDnsClient
    {
        public Task<DnsResponse> QueryAAsync(IPEndPoint server, string name, TimeSpan timeout, CancellationToken ct) => Task.FromResult(f(name));
    }

    private sealed class FakeServices(Dictionary<string, string> map) : IServiceStatusReader
    {
        public string? GetStatus(string serviceName) => map.GetValueOrDefault(serviceName);
    }

    private sealed class FakeProcs(string[] running) : IProcessLister
    {
        public bool IsRunning(string processName) => running.Contains(processName);
    }

    private sealed class FakeTls(Exception? ex) : ITlsConnector
    {
        public Task HandshakeAsync(IPAddress address, int port, string sni, CancellationToken ct) => ex is null ? Task.CompletedTask : Task.FromException(ex);
    }

    private sealed class BoomProbe : IProbe
    {
        public IReadOnlyCollection<string> CheckIds { get; } = ["x"];
        public TimeSpan Interval => TimeSpan.FromMinutes(5);
        public Task<IReadOnlyList<CheckResult>> RunAsync(CancellationToken ct) => throw new InvalidOperationException("boom");
    }
}
