using HomelabMonitor.Domain;

namespace HomelabMonitor.Tests;

public sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
    public void Set(DateTimeOffset at) => _now = at;
}

public static class T
{
    public static readonly DateTimeOffset Start = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
    public static readonly TimeZoneInfo Budapest = TimeZoneInfo.FindSystemTimeZoneById("Europe/Budapest");

    public static CheckDefinition Def(int n = 1, int m = 1, Severity err = Severity.Warn, string[]? parents = null, string? rule = null) =>
        new() { DisplayName = "x", FailThreshold = n, RecoverThreshold = m, OnError = err, Parents = parents ?? [], NotifyRule = rule };

    public static MonitorCatalog Catalog(params (string id, CheckDefinition def)[] items)
    {
        var d = new Dictionary<string, CheckDefinition>();
        foreach (var (id, def) in items)
        {
            d[id] = def;
        }

        d.TryAdd(MonitorCatalog.BrokenCheckId, Def());
        return new MonitorCatalog(d);
    }

    public static CheckResult R(string id, CheckStatus s, string? target = null) => new(id, s, DateTimeOffset.UtcNow, "msg", target);
    public static CheckResult Ok(string id) => R(id, CheckStatus.Ok);
    public static CheckResult Err(string id) => R(id, CheckStatus.Error);
    public static CheckResult Unk(string id) => R(id, CheckStatus.Unknown);
}
