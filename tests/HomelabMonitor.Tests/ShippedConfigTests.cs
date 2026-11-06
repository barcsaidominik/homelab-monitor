using HomelabMonitor.Domain;
using HomelabMonitor.Probes;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HomelabMonitor.Tests;

/// <summary>A configuration file bound the same way as in Program.cs.</summary>
public sealed record LoadedConfig(MonitorCatalog Catalog, ProbeOptions Probes, MonitorOptions Monitor, NotifyOptions Notify, IConfiguration Raw)
{
    public static LoadedConfig From(params string[] files)
    {
        var builder = new ConfigurationBuilder();
        foreach (var f in files)
        {
            builder.AddJsonFile(f);
        }

        var cfg = builder.Build();
        return new LoadedConfig(
            new MonitorCatalog((cfg.GetSection("Catalog").Get<CatalogOptions>() ?? new CatalogOptions()).Checks),
            cfg.GetSection("Probes").Get<ProbeOptions>() ?? new ProbeOptions(),
            cfg.GetSection("Monitor").Get<MonitorOptions>() ?? new MonitorOptions(),
            cfg.GetSection("Notify").Get<NotifyOptions>() ?? new NotifyOptions(),
            cfg);
    }

    public static string Shipped => Path.Combine(AppContext.BaseDirectory, "shipped-appsettings.json");

    public static string Example => Path.Combine(AppContext.BaseDirectory, "example-appsettings.json");
}

public sealed class ShippedConfigTests
{
    [Fact]
    public void ShippedConfig_IsSafeByDefault_AndValid()
    {
        var c = LoadedConfig.From(LoadedConfig.Shipped);

        Assert.Equal("False", c.Raw["Notify:Enabled"], ignoreCase: true);
        Assert.Equal("False", c.Raw["Heartbeat:Enabled"], ignoreCase: true);
        Assert.Equal("False", c.Raw["Homepage:Enabled"], ignoreCase: true);
        Assert.Empty(ConfigValidation.Validate(c.Catalog, c.Probes, c.Monitor, c.Notify));
        Assert.Empty(ProbeFactory.Build(c.Probes, TimeProvider.System).Probes);
    }

    [Fact]
    public void ShippedConfig_HasNoLists_SoASiteConfigCannotMergeIntoThem()
    {
        // The configuration binder merges arrays by index across files: any list here would mix into appsettings.Production.json.
        var c = LoadedConfig.From(LoadedConfig.Shipped);

        Assert.Empty(c.Probes.Http);
        Assert.Empty(c.Monitor.Reports.Disks);
        Assert.Empty(c.Monitor.Maintenance.Windows);
        Assert.Empty(c.Catalog.Ids);
    }
}

public sealed class ExampleConfigTests
{
    private static LoadedConfig Load() => LoadedConfig.From(LoadedConfig.Shipped, LoadedConfig.Example);

    [Fact]
    public void ExampleConfig_IsConsistent()
    {
        var c = Load();

        Assert.Empty(ConfigValidation.Validate(c.Catalog, c.Probes, c.Monitor, c.Notify));
        Assert.True(c.Catalog.TryGet(MonitorCatalog.BrokenCheckId, out _));
    }

    [Fact]
    public void ExampleConfig_BuildsAllProbes()
    {
        var c = Load();

        var built = ProbeFactory.Build(c.Probes, TimeProvider.System).Probes.SelectMany(p => p.CheckIds).Order(StringComparer.Ordinal);

        Assert.Equal(ConfigValidation.ProbeCheckIds(c.Probes).Order(StringComparer.Ordinal), built);
    }

    [Theory]
    [InlineData("Notify:TopicFile", "ntfy.topic")]
    [InlineData("Heartbeat:UrlFile", "heartbeat.url")]
    [InlineData("Monitor:Heavy:Directory", "heavy")]
    public void SecretAndDataPaths_HaveNoControlCharacters_AndExpectedLeaf(string key, string leaf)
    {
        // Regression: "...\monitor\ntfy.topic" in JSON once meant a line break ("\n"), so the secret file path was wrong.
        var value = Load().Raw[key];

        Assert.False(string.IsNullOrWhiteSpace(value));
        Assert.DoesNotContain(value!, char.IsControl);
        Assert.Equal(leaf, Path.GetFileName(value!.TrimEnd('\\')));
    }

    [Fact]
    public void ReportDisks_BindExactlyOnce()
    {
        // Regression: a non-empty array default plus the configuration produced C,D,C,D in the report.
        Assert.Equal(["C", "D"], Load().Monitor.Reports.Disks);
    }

    [Fact]
    public void ExampleConfig_DecidesCritInTheCatalog_AndDeclaresDependencies()
    {
        var cat = Load().Catalog;

        Assert.Equal(Severity.Crit, cat.Get("http.media").OnError);
        Assert.Equal(Severity.Warn, cat.Get("http.dashboard").OnError);
        Assert.Equal(["dns.local", "svc.proxy", "proc.media"], cat.Get("http.media").Parents);
        Assert.Equal(CheckSource.Heavy, cat.Get("backup.daily").Source);
    }
}

public sealed class ConfigValidationTests
{
    private static MonitorCatalog Cat(params (string Id, CheckDefinition Def)[] checks) => T.Catalog(checks);

    [Fact]
    public void UnknownNotifyRule_IsAnError()
    {
        var errors = ConfigValidation.Validate(Cat(("a", T.Def(rule: "missing"))), new ProbeOptions(), new MonitorOptions(), new NotifyOptions());

        Assert.Contains(errors, e => e.Contains("'missing'", StringComparison.Ordinal));
    }

    [Fact]
    public void ProbeCheckMissingFromCatalog_OrHeavyInCatalog_IsAnError()
    {
        var probes = new ProbeOptions { Processes = new() { ["proc.a"] = "a", ["proc.b"] = "b" } };
        var errors = ConfigValidation.Validate(Cat(("proc.a", new CheckDefinition { Source = CheckSource.Heavy })), probes, new MonitorOptions(), new NotifyOptions());

        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void ReportEntries_MustReferToCatalogChecks_AndUptimeKeysMustBeValid()
    {
        var monitor = new MonitorOptions();
        monitor.Reports.Uptime = [new UptimeEntry { Key = "a.b", Check = "x" }, new UptimeEntry { Key = "ok", Check = "a" }];
        monitor.Reports.Values = [new ReportCheckEntry { Check = "y" }];

        var errors = ConfigValidation.Validate(Cat(("a", T.Def())), new ProbeOptions(), monitor, new NotifyOptions());

        Assert.Equal(3, errors.Count); // dotted key, unknown uptime check, unknown value check
    }

    [Fact]
    public void UnknownTimeZone_IsAnError()
    {
        var errors = ConfigValidation.Validate(Cat(), new ProbeOptions(), new MonitorOptions { TimeZone = "Europe/Nowhere" }, new NotifyOptions());

        Assert.Contains(errors, e => e.Contains("Europe/Nowhere", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidProbeSettings_AreErrors()
    {
        var probes = new ProbeOptions
        {
            Http = [new HttpProbeOptions { CheckId = "http.a", Url = "not a url", ForceLan = true }],
            Dns = [new DnsProbeOptions { CheckId = "dns.a", Server = "dns.example" }],
            Tls = [new TlsProbeOptions { CheckId = "http.a", Address = "192.0.2.1" }],
            FanState = new FanStateProbeOptions { Enabled = true },
        };
        var cat = Cat(("http.a", T.Def()), ("dns.a", T.Def()), ("thermal.fan", T.Def()), ("thermal.cpu", T.Def()));

        var errors = ConfigValidation.Validate(cat, probes, new MonitorOptions(), new NotifyOptions());

        // bad URL, ForceLan without LanAddress, DNS server not an IP, empty fan state path, duplicate http.a
        Assert.Equal(5, errors.Count);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(6, true)]
    public void ReminderAfterHours_MustBePositive_AndNotCombinedWithDaily(double hours, bool daily)
    {
        var notify = new NotifyOptions { Rules = new() { ["r"] = new NotifyRuleOptions { ReminderAfterHours = hours, Daily = daily } } };

        Assert.Single(ConfigValidation.Validate(Cat(), new ProbeOptions(), new MonitorOptions(), notify));
    }

    /// <summary>
    /// Opt-in check of a real site configuration that is not part of the repository:
    /// set HOMELAB_MONITOR_SITE_CONFIG to the path of an appsettings.Production.json.
    /// </summary>
    [Fact]
    public void SiteConfig_WhenProvided_IsConsistent_AndBuilds()
    {
        var path = Environment.GetEnvironmentVariable("HOMELAB_MONITOR_SITE_CONFIG");
        if (string.IsNullOrWhiteSpace(path))
        {
            Assert.Skip("HOMELAB_MONITOR_SITE_CONFIG is not set.");
        }

        var c = LoadedConfig.From(LoadedConfig.Shipped, path);

        Assert.Empty(ConfigValidation.Validate(c.Catalog, c.Probes, c.Monitor, c.Notify));
        Assert.NotEmpty(ProbeFactory.Build(c.Probes, TimeProvider.System).Probes);
    }
}
