using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Text.Json;
using HomelabMonitor.Domain;

namespace HomelabMonitor.Probes;

public sealed class HttpProbeOptions
{
    public string CheckId { get; set; } = "";
    public string Url { get; set; } = "";
    public string? ExpectBody { get; set; }
    public int MinStatus { get; set; } = 200;
    public int MaxStatus { get; set; } = 299;

    /// <summary>Connect to <see cref="ProbeOptions.LanAddress"/> instead of resolving the host name (URL, SNI and Host stay unchanged).</summary>
    public bool ForceLan { get; set; }
}

public sealed class DnsProbeOptions
{
    public string CheckId { get; set; } = "";
    public string Server { get; set; } = "";
    public int Port { get; set; } = 53;
    public string[] Names { get; set; } = [];
}

public sealed class TlsProbeOptions
{
    public string CheckId { get; set; } = "";
    public string Address { get; set; } = "";
    public int Port { get; set; } = 443;
    public string Sni { get; set; } = "";
}

/// <summary>Raises WARN when a numeric field of the JSON response reaches a threshold.</summary>
public sealed class JsonThresholdRule
{
    public string CheckId { get; set; } = "";

    /// <summary>Dotted path into the response, e.g. <c>stats.failed24h</c>.</summary>
    public string Field { get; set; } = "";

    public double WarnAtOrAbove { get; set; }

    /// <summary>WARN message; <c>{value}</c> and <c>{threshold}</c> are replaced.</summary>
    public string Message { get; set; } = "{value} (threshold {threshold})";
}

/// <summary>
/// Raises WARN when a running job's progress has not changed for <see cref="Minutes"/>. OK when no job is active,
/// when the job is paused, or when the progress field is null (the job is waiting for another step).
/// </summary>
public sealed class JsonStallRule
{
    public string CheckId { get; set; } = "";
    public string ActiveField { get; set; } = "";
    public string ProgressField { get; set; } = "";

    /// <summary>Fields that identify the job; a change in any of them (or in the progress) resets the timer.</summary>
    public string[] KeyFields { get; set; } = [];

    public string? PausedField { get; set; }
    public string? PausedValue { get; set; }
    public int Minutes { get; set; } = 30;
}

public sealed class JsonApiProbeOptions
{
    public string Url { get; set; } = "";
    public int IntervalMinutes { get; set; } = 5;
    public List<JsonThresholdRule> Thresholds { get; set; } = [];
    public JsonStallRule? Stall { get; set; }
}

public sealed class ProbeOptions
{
    public bool Enabled { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 8;

    /// <summary>Target address for <see cref="HttpProbeOptions.ForceLan"/>. Required only if a probe uses it.</summary>
    public string LanAddress { get; set; } = "";

    public List<HttpProbeOptions> Http { get; set; } = [];
    public List<DnsProbeOptions> Dns { get; set; } = [];
    public List<TlsProbeOptions> Tls { get; set; } = [];

    /// <summary>check id → Windows service name.</summary>
    public Dictionary<string, string> Services { get; set; } = [];

    /// <summary>check id → process name (without .exe).</summary>
    public Dictionary<string, string> Processes { get; set; } = [];

    public List<JsonApiProbeOptions> JsonApi { get; set; } = [];
    public FanStateProbeOptions FanState { get; set; } = new();
}

/// <summary>A light probe: read-only and without secrets. An exception means UNKNOWN (handled by the runner); a failing target means ERROR.</summary>
public interface IProbe
{
    IReadOnlyCollection<string> CheckIds { get; }

    TimeSpan Interval { get; }

    Task<IReadOnlyList<CheckResult>> RunAsync(CancellationToken ct);
}

public static class ProbeResult
{
    public static CheckResult Make(string checkId, CheckStatus status, string message, TimeProvider time, long ms) =>
        new(checkId, status, time.GetUtcNow(), CheckResult.Trim(message), DurationMs: ms);
}

/// <summary>Runs the probes that are due, each with a timeout; an exception or timeout yields UNKNOWN.</summary>
public sealed class ProbeRunner(IEnumerable<IProbe> probes, TimeProvider time, TimeSpan timeout)
{
    private readonly List<IProbe> _probes = probes.ToList();
    private readonly Dictionary<IProbe, DateTimeOffset> _lastRun = [];

    public IReadOnlyList<IProbe> Probes => _probes;

    public async Task<IReadOnlyList<CheckResult>> RunDueAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var due = _probes.Where(p => !_lastRun.TryGetValue(p, out var last) || now - last >= p.Interval - TimeSpan.FromSeconds(5)).ToList();
        foreach (var p in due)
        {
            _lastRun[p] = now;
        }

        var tasks = due.Select(p => RunOneAsync(p, ct));
        return (await Task.WhenAll(tasks)).SelectMany(r => r).ToList();
    }

    private async Task<IReadOnlyList<CheckResult>> RunOneAsync(IProbe probe, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout + TimeSpan.FromSeconds(2));
        try
        {
            return await probe.RunAsync(cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var msg = $"Probe failed: {ex.GetType().Name}";
            return probe.CheckIds.Select(id => new CheckResult(id, CheckStatus.Unknown, time.GetUtcNow(), msg)).ToList();
        }
    }
}

public sealed class HttpEndpointProbe(HttpProbeOptions options, HttpClient client, TimeProvider time, TimeSpan timeout) : IProbe
{
    public IReadOnlyCollection<string> CheckIds { get; } = [options.CheckId];

    public TimeSpan Interval => TimeSpan.FromMinutes(1);

    public async Task<IReadOnlyList<CheckResult>> RunAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        CheckStatus status;
        string msg;
        try
        {
            using var resp = await client.GetAsync(options.Url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var code = (int)resp.StatusCode;
            if (code < options.MinStatus || code > options.MaxStatus)
            {
                (status, msg) = (CheckStatus.Error, $"HTTP {code}");
            }
            else if (options.ExpectBody is not null)
            {
                var body = (await resp.Content.ReadAsStringAsync(cts.Token)).Trim();
                (status, msg) = body == options.ExpectBody ? (CheckStatus.Ok, "") : (CheckStatus.Error, "Unexpected response body");
            }
            else
            {
                (status, msg) = (CheckStatus.Ok, "");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            (status, msg) = (CheckStatus.Error, "Timeout");
        }
        catch (HttpRequestException ex)
        {
            (status, msg) = (CheckStatus.Error, $"Connection error: {ex.HttpRequestError}");
        }

        return [ProbeResult.Make(options.CheckId, status, msg, time, sw.ElapsedMilliseconds)];
    }
}

public static class ProbeHttpClientFactory
{
    /// <summary>
    /// One client for all HTTP probes. For ForceLan hosts the TCP connection goes to the LAN address, while the URL
    /// (SNI/Host) stays the same and TLS is validated normally.
    /// </summary>
    public static HttpClient Create(ProbeOptions options)
    {
        var forced = new HashSet<string>(
            options.Http.Where(h => h.ForceLan).Select(h => new Uri(h.Url).Host), StringComparer.OrdinalIgnoreCase);
        if (forced.Count > 0 && string.IsNullOrWhiteSpace(options.LanAddress))
        {
            throw new InvalidOperationException("Probes:LanAddress is required when an HTTP probe uses ForceLan.");
        }

        var lan = forced.Count > 0 ? IPAddress.Parse(options.LanAddress) : IPAddress.None;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
            ConnectCallback = async (ctx, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    if (forced.Contains(ctx.DnsEndPoint.Host))
                    {
                        await socket.ConnectAsync(new IPEndPoint(lan, ctx.DnsEndPoint.Port), ct);
                    }
                    else
                    {
                        await socket.ConnectAsync(ctx.DnsEndPoint, ct);
                    }

                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds + 2) };
    }
}

public sealed class DnsProbe(string checkId, IPEndPoint server, string[] names, IDnsClient dns, TimeProvider time, TimeSpan timeout) : IProbe
{
    public IReadOnlyCollection<string> CheckIds { get; } = [checkId];

    public TimeSpan Interval => TimeSpan.FromMinutes(1);

    public async Task<IReadOnlyList<CheckResult>> RunAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var failed = 0;
        foreach (var name in names)
        {
            try
            {
                var r = await dns.QueryAAsync(server, name, timeout, ct);
                if (r.ResponseCode != 0 || r.Addresses.Count == 0)
                {
                    failed++;
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or FormatException)
            {
                if (ct.IsCancellationRequested)
                {
                    throw;
                }

                failed++;
            }
        }

        var status = failed == 0 ? CheckStatus.Ok : CheckStatus.Error;
        return [ProbeResult.Make(checkId, status, failed == 0 ? "" : $"{failed}/{names.Length} names did not resolve", time, sw.ElapsedMilliseconds)];
    }
}

public interface IServiceStatusReader
{
    /// <summary>The service status as text ("Running" etc.), or null if there is no such service.</summary>
    string? GetStatus(string serviceName);
}

public sealed class WindowsServiceStatusReader : IServiceStatusReader
{
    public string? GetStatus(string serviceName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return Read(serviceName);
    }

    [SupportedOSPlatform("windows")]
    private static string? Read(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            return sc.Status.ToString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}

public sealed class ServiceProbe(IReadOnlyDictionary<string, string> services, IServiceStatusReader reader, TimeProvider time) : IProbe
{
    public IReadOnlyCollection<string> CheckIds { get; } = services.Keys.ToList();

    public TimeSpan Interval => TimeSpan.FromMinutes(1);

    public Task<IReadOnlyList<CheckResult>> RunAsync(CancellationToken ct)
    {
        var list = new List<CheckResult>();
        foreach (var (checkId, name) in services)
        {
            var sw = Stopwatch.StartNew();
            var status = reader.GetStatus(name);
            list.Add(status switch
            {
                null => ProbeResult.Make(checkId, CheckStatus.Unknown, "Service not found", time, sw.ElapsedMilliseconds),
                "Running" => ProbeResult.Make(checkId, CheckStatus.Ok, "", time, sw.ElapsedMilliseconds),
                _ => ProbeResult.Make(checkId, CheckStatus.Error, $"Status: {status}", time, sw.ElapsedMilliseconds),
            });
        }

        return Task.FromResult<IReadOnlyList<CheckResult>>(list);
    }
}

public interface IProcessLister
{
    bool IsRunning(string processName);
}

public sealed class SystemProcessLister : IProcessLister
{
    public bool IsRunning(string processName)
    {
        var procs = Process.GetProcessesByName(processName);
        try
        {
            return procs.Length > 0;
        }
        finally
        {
            foreach (var p in procs)
            {
                p.Dispose();
            }
        }
    }
}

public sealed class ProcessProbe(IReadOnlyDictionary<string, string> processes, IProcessLister lister, TimeProvider time) : IProbe
{
    public IReadOnlyCollection<string> CheckIds { get; } = processes.Keys.ToList();

    public TimeSpan Interval => TimeSpan.FromMinutes(1);

    public Task<IReadOnlyList<CheckResult>> RunAsync(CancellationToken ct)
    {
        var list = processes.Select(kv =>
        {
            var running = lister.IsRunning(kv.Value);
            return ProbeResult.Make(kv.Key, running ? CheckStatus.Ok : CheckStatus.Error, running ? "" : "Process is not running", time, 0);
        }).ToList();
        return Task.FromResult<IReadOnlyList<CheckResult>>(list);
    }
}

public interface ITlsConnector
{
    Task HandshakeAsync(IPAddress address, int port, string sni, CancellationToken ct);
}

public sealed class TlsConnector : ITlsConnector
{
    public async Task HandshakeAsync(IPAddress address, int port, string sni, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(address, port, ct);
        await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = sni }, ct);
    }
}

/// <summary>TCP connect + TLS handshake with the given SNI (e.g. a reverse proxy). The certificate is validated normally.</summary>
public sealed class TlsHandshakeProbe(TlsProbeOptions options, ITlsConnector connector, TimeProvider time, TimeSpan timeout) : IProbe
{
    private readonly IPAddress _address = IPAddress.Parse(options.Address);

    public IReadOnlyCollection<string> CheckIds { get; } = [options.CheckId];

    public TimeSpan Interval => TimeSpan.FromMinutes(1);

    public async Task<IReadOnlyList<CheckResult>> RunAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        CheckStatus status;
        var msg = "";
        try
        {
            await connector.HandshakeAsync(_address, options.Port, options.Sni, cts.Token);
            status = CheckStatus.Ok;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            (status, msg) = (CheckStatus.Error, "Timeout");
        }
        catch (Exception ex) when (ex is SocketException or IOException or System.Security.Authentication.AuthenticationException)
        {
            (status, msg) = (CheckStatus.Error, $"TLS/TCP error: {ex.GetType().Name}");
        }

        return [ProbeResult.Make(options.CheckId, status, msg, time, sw.ElapsedMilliseconds)];
    }
}

/// <summary>
/// Reads a JSON status endpoint and evaluates threshold and stall rules on it. A missing field means UNKNOWN, so a
/// contract change of the target shows up as a broken check instead of a silent OK.
/// </summary>
public sealed class JsonApiProbe(JsonApiProbeOptions options, HttpClient client, TimeProvider time, TimeSpan timeout) : IProbe
{
    private string? _jobKey;
    private DateTimeOffset _jobChangedAt;

    public IReadOnlyCollection<string> CheckIds { get; } =
        options.Thresholds.Select(t => t.CheckId).Concat(options.Stall is null ? [] : [options.Stall.CheckId]).ToList();

    public TimeSpan Interval => TimeSpan.FromMinutes(options.IntervalMinutes);

    public async Task<IReadOnlyList<CheckResult>> RunAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        string json;
        try
        {
            json = await client.GetStringAsync(options.Url, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return All(CheckStatus.Unknown, "The API is not reachable", sw);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return Evaluate(doc.RootElement, sw);
        }
        catch (JsonException)
        {
            return All(CheckStatus.Unknown, "Invalid API response", sw);
        }
    }

    public IReadOnlyList<CheckResult> Evaluate(JsonElement root, Stopwatch sw)
    {
        var now = time.GetUtcNow();
        var ms = sw.ElapsedMilliseconds;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return All(CheckStatus.Unknown, "Unexpected API response shape", sw);
        }

        var results = new List<CheckResult>();
        foreach (var rule in options.Thresholds)
        {
            if (TryGet(root, rule.Field, out var f) && f.TryGetDouble(out var value))
            {
                var bad = value >= rule.WarnAtOrAbove;
                var msg = bad ? rule.Message.Replace("{value}", Num(value), StringComparison.Ordinal).Replace("{threshold}", Num(rule.WarnAtOrAbove), StringComparison.Ordinal) : "";
                results.Add(new CheckResult(rule.CheckId, bad ? CheckStatus.Warn : CheckStatus.Ok, now, msg, DurationMs: ms, Value: value, Threshold: rule.WarnAtOrAbove));
            }
            else
            {
                results.Add(new CheckResult(rule.CheckId, CheckStatus.Unknown, now, $"Missing field {rule.Field}", DurationMs: ms));
            }
        }

        if (options.Stall is { } stall)
        {
            results.Add(EvaluateStall(stall, root, now, ms));
        }

        return results;
    }

    private CheckResult EvaluateStall(JsonStallRule rule, JsonElement root, DateTimeOffset now, long ms)
    {
        var id = rule.CheckId;
        if (!TryGet(root, rule.ActiveField, out var a) || !a.TryGetDouble(out var active))
        {
            return new CheckResult(id, CheckStatus.Unknown, now, $"Missing field {rule.ActiveField}", DurationMs: ms);
        }

        if (active <= 0)
        {
            _jobKey = null;
            return new CheckResult(id, CheckStatus.Ok, now, "", DurationMs: ms);
        }

        if (!TryGet(root, rule.ProgressField, out var p))
        {
            return new CheckResult(id, CheckStatus.Unknown, now, $"Missing field {rule.ProgressField}", DurationMs: ms);
        }

        var paused = rule.PausedField is not null && TryGet(root, rule.PausedField, out var st)
            && st.ValueKind == JsonValueKind.String && st.GetString() == rule.PausedValue;
        if (paused || p.ValueKind == JsonValueKind.Null)
        {
            _jobKey = null;
            return new CheckResult(id, CheckStatus.Ok, now, "", DurationMs: ms);
        }

        if (!p.TryGetDouble(out var progress))
        {
            return new CheckResult(id, CheckStatus.Unknown, now, $"Invalid {rule.ProgressField}", DurationMs: ms);
        }

        var parts = rule.KeyFields.Select(k => TryGet(root, k, out var e) ? e.ToString() : "")
            .Append(progress.ToString("R", CultureInfo.InvariantCulture));
        var key = string.Join("|", parts);
        if (key != _jobKey)
        {
            _jobKey = key;
            _jobChangedAt = now;
            return new CheckResult(id, CheckStatus.Ok, now, "", DurationMs: ms);
        }

        var stuck = now - _jobChangedAt;
        return stuck >= TimeSpan.FromMinutes(rule.Minutes)
            ? new CheckResult(id, CheckStatus.Warn, now, $"Job progress unchanged for {(int)stuck.TotalMinutes} min", DurationMs: ms, Value: stuck.TotalMinutes)
            : new CheckResult(id, CheckStatus.Ok, now, "", DurationMs: ms, Value: stuck.TotalMinutes);
    }

    /// <summary>Resolves a dotted path (<c>a.b.c</c>) through nested objects.</summary>
    public static bool TryGet(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        foreach (var segment in path.Split('.'))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
            {
                return false;
            }
        }

        return true;
    }

    private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private IReadOnlyList<CheckResult> All(CheckStatus status, string msg, Stopwatch sw) =>
        CheckIds.Select(id => new CheckResult(id, status, time.GetUtcNow(), msg, DurationMs: sw.ElapsedMilliseconds)).ToList();
}

public sealed class FanStateProbeOptions
{
    public bool Enabled { get; set; }
    public string StatePath { get; set; } = "";
    public string FanCheckId { get; set; } = "thermal.fan";
    public string CpuCheckId { get; set; } = "thermal.cpu";
    public int StaleSeconds { get; set; } = 180;
    public double CpuWarnC { get; set; } = 95;

    /// <summary>Above this temperature both fans at 0 RPM is a problem. Below it 0 RPM can be intended (silent fan curve).</summary>
    public double FanStoppedAboveC { get; set; } = 80;

    /// <summary>After this many failed recovery attempts (StallRecoveryTries) a stopped fan is ERROR.</summary>
    public int StallErrorTries { get; set; } = 3;
}

/// <summary>
/// Reads the state file of an external fan controller (written every few seconds by a privileged helper, because reading
/// the fan through the firmware needs admin rights, and the monitor runs without them). JSON contract:
/// <c>{ "At": ISO-8601, "CpuC": number, "Fan1Rpm": number, "Fan2Rpm": number, "Mode": "auto"|"max", "StallRecoveryTries": number }</c>.
/// A stale or missing file yields only a fan result (WARN); the CPU check gets no new sample then.
/// </summary>
public sealed class FanStateProbe(FanStateProbeOptions options, Func<string?> readState, TimeProvider time) : IProbe
{
    private string Fan => options.FanCheckId;

    private string Cpu => options.CpuCheckId;

    public IReadOnlyCollection<string> CheckIds { get; } = [options.FanCheckId, options.CpuCheckId];

    public TimeSpan Interval => TimeSpan.FromMinutes(1);

    public static string? ReadFile(string path)
    {
        // The writer replaces the file every few seconds; retry once on a sharing violation.
        for (var i = 0; ; i++)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (IOException) when (i == 0)
            {
                Thread.Sleep(200);
            }
        }
    }

    public Task<IReadOnlyList<CheckResult>> RunAsync(CancellationToken ct) => Task.FromResult(Evaluate(readState()));

    public IReadOnlyList<CheckResult> Evaluate(string? json)
    {
        var now = time.GetUtcNow();
        if (json is null)
        {
            return [new CheckResult(Fan, CheckStatus.Warn, now, "Fan controller state file is missing (not running?)")];
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json.TrimStart('\uFEFF'));
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return [new CheckResult(Fan, CheckStatus.Unknown, now, "Invalid fan controller state file")];
        }

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("At", out var atEl) || atEl.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(atEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
        {
            return [new CheckResult(Fan, CheckStatus.Unknown, now, "Missing At field in the fan controller state")];
        }

        var age = now - at;
        if (age > TimeSpan.FromSeconds(options.StaleSeconds))
        {
            return [new CheckResult(Fan, CheckStatus.Warn, now, $"Fan controller state not updated for {(int)age.TotalMinutes} min (not running?)", Value: age.TotalSeconds)];
        }

        double? Num(string name) => root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var v) ? v : null;
        var cpu = Num("CpuC");
        var fan1 = Num("Fan1Rpm");
        var fan2 = Num("Fan2Rpm");
        var mode = root.TryGetProperty("Mode", out var m) ? m.ToString() : "";
        var rpm = fan1 is null && fan2 is null ? (double?)null : Math.Max(fan1 ?? 0, fan2 ?? 0);

        var stallTries = Num("StallRecoveryTries") ?? 0;

        CheckResult guard;
        if (rpm is null)
        {
            guard = new CheckResult(Fan, CheckStatus.Unknown, now, "The fan controller cannot read the fan speed");
        }
        else if (rpm == 0 && stallTries >= options.StallErrorTries)
        {
            guard = new CheckResult(Fan, CheckStatus.Error, now,
                string.Create(CultureInfo.InvariantCulture, $"Both fans stopped, CPU {cpu:0} °C; {stallTries:0} recovery attempts did not help"), Value: 0);
        }
        else if (rpm == 0 && (cpu >= options.FanStoppedAboveC || mode == "max"))
        {
            guard = new CheckResult(Fan, CheckStatus.Warn, now, string.Create(CultureInfo.InvariantCulture, $"Both fans stopped, CPU {cpu:0} °C"), Value: 0);
        }
        else
        {
            guard = new CheckResult(Fan, CheckStatus.Ok, now, string.Create(CultureInfo.InvariantCulture, $"{mode}: {fan1:0}/{fan2:0} RPM"), Value: rpm);
        }

        if (cpu is null)
        {
            return [guard, new CheckResult(Cpu, CheckStatus.Unknown, now, "No CPU temperature")];
        }

        var hot = cpu >= options.CpuWarnC;
        return [guard, new CheckResult(Cpu, hot ? CheckStatus.Warn : CheckStatus.Ok, now,
            hot ? string.Create(CultureInfo.InvariantCulture, $"CPU {cpu:0.0} °C (fan: {mode}, {fan1:0}/{fan2:0} RPM)") : "", Value: cpu, Threshold: options.CpuWarnC)];
    }
}
