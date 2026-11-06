// HomelabMonitor: deterministic, rule-based monitoring for a single Windows home server.
// Light probes + heavy-check ingest -> incident state machine -> SQLite, Seq events, read-only API, notification outbox.

using System.Net;
using HomelabMonitor;
using HomelabMonitor.Api;
using HomelabMonitor.Domain;
using HomelabMonitor.Engine;
using HomelabMonitor.Heavy;
using HomelabMonitor.Notify;
using HomelabMonitor.Probes;
using HomelabMonitor.Reports;
using HomelabMonitor.Store;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(o => o.ServiceName = "HomelabMonitor");

// Console always; the Seq sink only when Seq:Url is set. Application/Component let Seq filters select monitor events.
builder.Host.UseSerilog((context, lc) =>
{
    var application = context.Configuration["Seq:Application"];
    lc.MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
        .MinimumLevel.Override("System.Net.Http", Serilog.Events.LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", string.IsNullOrWhiteSpace(application) ? "HomelabMonitor" : application)
        .Enrich.WithProperty("Component", "monitor")
        .WriteTo.Console();
    var seq = context.Configuration["Seq:Url"];
    if (!string.IsNullOrWhiteSpace(seq))
    {
        lc.WriteTo.Seq(seq);
    }
});

var cfg = builder.Configuration;
var monitor = cfg.GetSection("Monitor").Get<MonitorOptions>() ?? new MonitorOptions();
var probeOptions = cfg.GetSection("Probes").Get<ProbeOptions>() ?? new ProbeOptions();
var notify = cfg.GetSection("Notify").Get<NotifyOptions>() ?? new NotifyOptions();
var heartbeat = cfg.GetSection("Heartbeat").Get<HeartbeatOptions>() ?? new HeartbeatOptions();
var catalogOptions = cfg.GetSection("Catalog").Get<CatalogOptions>() ?? new CatalogOptions();

var catalog = new MonitorCatalog(catalogOptions.Checks); // fails at startup on a dependency cycle or an unknown parent
var configErrors = ConfigValidation.Validate(catalog, probeOptions, monitor, notify);
if (configErrors.Count > 0)
{
    throw new InvalidOperationException("Invalid configuration:" + Environment.NewLine + string.Join(Environment.NewLine, configErrors));
}
var tz = FindTimeZone(monitor.TimeZone);
var store = new SqliteStore(monitor.DbPath);
store.Migrate();
var time = TimeProvider.System;
var engine = new IncidentEngine(catalog, time, tz, store.LoadStates(), store.GetAllIncidentIds());
var bootTime = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
var calendar = new MaintenanceCalendar(monitor.Maintenance, tz, bootTime);
var status = new MonitorStatus();
var heavyOptions = monitor.Heavy;
ProbeRunner? runner = probeOptions.Enabled && monitor.RunBackground ? ProbeFactory.Build(probeOptions, time) : null;

builder.Services.AddSingleton(monitor);
builder.Services.AddSingleton(notify);
builder.Services.AddSingleton(heartbeat);
builder.Services.AddSingleton(catalog);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton(engine);
builder.Services.AddSingleton(calendar);
builder.Services.AddSingleton(status);
builder.Services.AddSingleton(time);
builder.Services.AddSingleton(heavyOptions);
builder.Services.AddSingleton<HeavyIngest>();
builder.Services.AddSingleton<NotificationPlanner>();
builder.Services.AddSingleton<SummaryService>();

// Optional data endpoints for a Homepage dashboard: disks (free space + Hard Disk Sentinel) and hardware
// (LibreHardwareMonitor + the fan controller state file).
var homepageOptions = cfg.GetSection("Homepage").Get<HomepageOptions>() ?? new HomepageOptions();
var localClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) }; // local, secret-free URLs only
builder.Services.AddSingleton(sp => new HomepageDisksService(localClient, homepageOptions, HomepageDisksService.ReadLocalDrives, time,
    sp.GetRequiredService<ILogger<HomepageDisksService>>()));
builder.Services.AddSingleton(sp => new HomepageHardwareService(localClient, homepageOptions, probeOptions.FanState,
    () => string.IsNullOrWhiteSpace(probeOptions.FanState.StatePath) ? null : FanStateProbe.ReadFile(probeOptions.FanState.StatePath),
    time, sp.GetRequiredService<ILogger<HomepageHardwareService>>()));
builder.Services.AddSingleton(sp => new ReportService(store, catalog, monitor, tz, time, sp.GetRequiredService<ILogger<ReportService>>()));
builder.Services.AddSingleton(new DiskSampler(store, new DriveInfoReader(), monitor.Reports, time));
builder.Services.AddSingleton(sp => new MonitorCycle(
    runner, sp.GetRequiredService<HeavyIngest>(), store, engine, sp.GetRequiredService<NotificationPlanner>(), calendar,
    monitor, time, status, sp.GetRequiredService<ILogger<MonitorCycle>>(),
    sp.GetRequiredService<DiskSampler>(), sp.GetRequiredService<ReportService>()));

// Deliberately no IHttpClientFactory: its handlers log request URLs (the ntfy topic and the ping URL are secrets),
// and these clients are singletons anyway.
var ntfyClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
var heartbeatClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
builder.Services.AddSingleton<INotificationSender>(new NtfySender(ntfyClient, notify));
builder.Services.AddSingleton<OutboxDispatcher>();
builder.Services.AddSingleton(sp => new HeartbeatPinger(heartbeat, notify, heartbeatClient, store, time,
    sp.GetRequiredService<ILogger<HeartbeatPinger>>()));

if (monitor.RunBackground)
{
    builder.Services.AddHostedService<MonitorWorker>();
    builder.Services.AddHostedService<OutboxWorker>();
    builder.Services.AddHostedService<HeartbeatWorker>();
}

var app = builder.Build();

// GET only: nothing on the network can change state (muting goes through mute.json in the data directory).
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/homepage/summary", (SummaryService s) => Results.Ok(s.Build()));
app.MapGet("/api/homepage/report", (SummaryService s) => Results.Ok(s.BuildReport()));
if (homepageOptions.Enabled)
{
    app.MapHomepageDataEndpoints();
}

app.Run();

static TimeZoneInfo FindTimeZone(string id)
{
    try
    {
        return TimeZoneInfo.FindSystemTimeZoneById(id);
    }
    catch (TimeZoneNotFoundException)
    {
        return TimeZoneInfo.Utc;
    }
}

public partial class Program;
