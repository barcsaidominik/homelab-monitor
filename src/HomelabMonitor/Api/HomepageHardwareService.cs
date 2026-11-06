using System.Globalization;
using System.Text.Json;
using HomelabMonitor.Probes;

namespace HomelabMonitor.Api;

/// <summary>LibreHardwareMonitor sensor ids of the hardware card. Sensor ids differ between LHM versions and machines: check them in /data.json.</summary>
public sealed class HardwareSensorOptions
{
    public string CpuTemp { get; set; } = "/amdcpu/0/temperature/2";
    public string CpuLoad { get; set; } = "/amdcpu/0/load/0";
    public string CpuPower { get; set; } = "/amdcpu/0/power/0";
    public string GpuTemp { get; set; } = "/gpu-nvidia/0/temperature/0";
    public string GpuLoad { get; set; } = "/gpu-nvidia/0/load/0";
    public string GpuVideo { get; set; } = "/gpu-nvidia/0/load/2";
    public string RamLoad { get; set; } = "/ram/load/0";
    public string RamUsed { get; set; } = "/ram/data/0";
    public string NvmeLoad { get; set; } = "/nvme/0/load/53";
    public string HddLoad { get; set; } = "/hdd/1/load/53";
}

public sealed class HomepageOptions
{
    /// <summary>Maps /api/homepage/disks and /api/homepage/hardware.</summary>
    public bool Enabled { get; set; }

    public string LhmUrl { get; set; } = "http://127.0.0.1:8085/data.json";
    public string HdsXmlUrl { get; set; } = "http://127.0.0.1:61220/xml";
    public HardwareSensorOptions Sensors { get; set; } = new();

    /// <summary>Culture of the formatted sizes (e.g. "1,5 TB" with hu-HU).</summary>
    public string Culture { get; set; } = "en-US";

    /// <summary>Text shown when a value is not available.</summary>
    public string NoDataText { get; set; } = "No data";

    /// <summary>Display text of the fan controller modes; unknown modes show <see cref="NoDataText"/>.</summary>
    public Dictionary<string, string> FanModeLabels { get; set; } = new(StringComparer.Ordinal) { ["max"] = "Max", ["auto"] = "Auto" };
}

/// <summary>
/// Always filled numbers: Homepage would render a null as NaN. A missing value is 0 (the card can remap a 0 temperature
/// to "n/a"; for loads 0 can be a real value). The fan values come from the fan controller state file.
/// </summary>
public sealed record HomepageHardware(
    double CpuTemp, double CpuLoad, double CpuPower,
    double GpuTemp, double GpuLoad, double GpuVideo,
    double RamLoad, double RamUsed,
    double NvmeLoad, double HddLoad,
    int Fan1, int Fan2, string FanMode,
    string LhmSource, DateTime UpdatedAt);

/// <summary>
/// One read-only snapshot for all hardware cards: a single LHM request instead of one per card, plus the fan speeds
/// (LHM cannot read them on some machines). Cached for 10 s.
/// </summary>
public sealed class HomepageHardwareService(
    HttpClient client,
    HomepageOptions options,
    FanStateProbeOptions fanState,
    Func<string?> readFanState,
    TimeProvider time,
    ILogger<HomepageHardwareService> logger)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private (HomepageHardware Value, DateTimeOffset At)? _cached;

    public async Task<HomepageHardware> GetAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            if (_cached is { } c && now - c.At < CacheDuration)
            {
                return c.Value;
            }

            var value = Build(await ReadLhmAsync(ct), readFanState(), now);
            _cached = (value, now);
            return value;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<Dictionary<string, double>?> ReadLhmAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await using var stream = await client.GetStreamAsync(options.LhmUrl, cts.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
            return ParseLhm(doc.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("LibreHardwareMonitor is not reachable: {Error}", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>The LHM data.json tree: { SensorId, Value: "56,6 °C", Children: [...] }.</summary>
    public static Dictionary<string, double> ParseLhm(JsonElement root)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        var stack = new Stack<JsonElement>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (n.TryGetProperty("SensorId", out var id) && id.ValueKind == JsonValueKind.String
                && n.TryGetProperty("Value", out var v) && v.ValueKind == JsonValueKind.String && ParseNumber(v.GetString()) is { } num)
            {
                result[id.GetString()!] = num;
            }

            if (n.TryGetProperty("Children", out var children) && children.ValueKind == JsonValueKind.Array)
            {
                foreach (var ch in children.EnumerateArray())
                {
                    stack.Push(ch);
                }
            }
        }

        return result;
    }

    private static double? ParseNumber(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var cleaned = new string(text.Where(ch => char.IsAsciiDigit(ch) || ch is ',' or '.' or '-').ToArray()).Replace(',', '.');
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? Math.Round(d, 1) : null;
    }

    public HomepageHardware Build(Dictionary<string, double>? lhm, string? fanJson, DateTimeOffset now)
    {
        double S(string id) => lhm is not null && lhm.TryGetValue(id, out var v) ? v : 0;
        var s = options.Sensors;
        var (fan1, fan2, mode) = ReadFan(fanJson, now);
        return new HomepageHardware(
            S(s.CpuTemp), S(s.CpuLoad), S(s.CpuPower),
            S(s.GpuTemp), S(s.GpuLoad), S(s.GpuVideo),
            S(s.RamLoad), S(s.RamUsed),
            S(s.NvmeLoad), S(s.HddLoad),
            fan1, fan2, mode,
            lhm is null ? "unavailable" : "ok", now.UtcDateTime);
    }

    private (int Fan1, int Fan2, string Mode) ReadFan(string? json, DateTimeOffset now)
    {
        var noData = options.NoDataText;
        if (json is null)
        {
            return (0, 0, noData);
        }

        try
        {
            using var doc = JsonDocument.Parse(json.TrimStart('\uFEFF'));
            var r = doc.RootElement;
            if (!r.TryGetProperty("At", out var at) || at.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var atTime)
                || now - atTime > TimeSpan.FromSeconds(fanState.StaleSeconds))
            {
                return (0, 0, noData);
            }

            int Rpm(string name) => r.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var i) ? i : 0;
            var mode = r.TryGetProperty("Mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            var label = mode is not null && options.FanModeLabels.TryGetValue(mode, out var l) ? l : noData;
            return (Rpm("Fan1Rpm"), Rpm("Fan2Rpm"), label);
        }
        catch (JsonException)
        {
            return (0, 0, noData);
        }
    }
}
