using HomelabMonitor.Domain;
using HomelabMonitor.Engine;
using HomelabMonitor.Heavy;
using HomelabMonitor.Notify;
using HomelabMonitor.Probes;
using HomelabMonitor.Store;
using Microsoft.Extensions.Logging;

namespace HomelabMonitor;

/// <summary>Live run state for the API and the heartbeat.</summary>
public sealed class MonitorStatus
{
    private long _lastLightTicks;

    public DateTimeOffset? LastLightProbeAt
    {
        get
        {
            var t = Interlocked.Read(ref _lastLightTicks);
            return t == 0 ? null : new DateTimeOffset(t, TimeSpan.Zero);
        }
        set => Interlocked.Exchange(ref _lastLightTicks, value?.UtcTicks ?? 0);
    }
}

/// <summary>One monitor cycle: probes + heavy ingest → samples → state machine → store → Seq events → notification planning.</summary>
public sealed class MonitorCycle(
    ProbeRunner? probes,
    HeavyIngest heavy,
    SqliteStore store,
    IncidentEngine engine,
    NotificationPlanner planner,
    MaintenanceCalendar calendar,
    MonitorOptions options,
    TimeProvider time,
    MonitorStatus status,
    ILogger<MonitorCycle> log,
    HomelabMonitor.Reports.DiskSampler? diskSampler = null,
    HomelabMonitor.Reports.ReportService? reports = null)
{
    private readonly HashSet<string> _knownMutes = new(StringComparer.Ordinal);
    private DateTimeOffset _lastHousekeeping = DateTimeOffset.MinValue;

    public async Task RunAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var mutes = MuteFile.Load(options.MuteFilePath);
        TrackMutes(mutes, now);

        var light = probes is null ? [] : await probes.RunDueAsync(ct);
        if (light.Count > 0)
        {
            status.LastLightProbeAt = now;
        }

        foreach (var entry in options.Reports.Values)
        {
            var r = light.FirstOrDefault(r => r.CheckId == entry.Check && r.Status != CheckStatus.Unknown && r.Value is not null);
            if (r is not null)
            {
                store.SetMeta(ValueMetaKey(entry.Check), r.Value!.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        var batch = heavy.Poll();
        var lastHeavy = batch.NewRuns.Select(r => (DateTimeOffset?)r.FinishedAt).Concat([store.LastHeavyFinishedAt()]).Max();
        var all = light.Concat(batch.Results).Append(heavy.EvaluateStale(lastHeavy)).ToList();

        store.InsertSamples(all.Select(r => new SampleRow(r.Source == CheckSource.Heavy ? r.ObservedAt : now, r.Key, r.Status, r.DurationMs, r.Source,
            calendar.IsActiveFor(r.CheckId, now, mutes))));

        var events = engine.ProcessBatch(all);
        store.ApplyEvents(events);
        store.SaveStates(engine.Snapshot());
        heavy.MarkProcessed(batch);
        foreach (var e in events)
        {
            LogEvent(e);
        }

        planner.Plan(checkId => calendar.IsActiveFor(checkId, now, mutes));

        // Hourly disk sample and the daily/weekly report (a missed one is made once after startup); errors must not stop the cycle.
        try
        {
            diskSampler?.SampleIfDue();
            reports?.RunIfDue();
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        {
            log.LogError("Disk sample/report failed: {Error}", ex.GetType().Name);
        }

        if (now - _lastHousekeeping >= TimeSpan.FromHours(1))
        {
            _lastHousekeeping = now;
            store.Rollup(now);
            store.Purge(now);
        }
    }

    /// <summary>Meta key of the last numeric value of a check listed under Monitor:Reports:Values.</summary>
    public static string ValueMetaKey(string checkId) => $"value:{checkId}";

    private void TrackMutes(IReadOnlyList<MuteEntry> mutes, DateTimeOffset now)
    {
        foreach (var m in mutes)
        {
            var key = $"{m.Check}|{m.Until:O}";
            if (now < m.Until && _knownMutes.Add(key))
            {
                log.LogWarning("Manual mute active: {MuteCheck} until {MuteUntil} ({MuteReason})", m.Check, m.Until, m.Reason);
            }
            else if (now >= m.Until && _knownMutes.Remove(key))
            {
                log.LogInformation("Manual mute expired: {MuteCheck}", m.Check);
            }
        }
    }

    private void LogEvent(EngineEvent e)
    {
        var level = !e.Notable ? LogLevel.Debug
            : e.To == CheckState.Open && e.Severity == Severity.Crit ? LogLevel.Error
            : e.To == CheckState.Open && e.Severity == Severity.Warn ? LogLevel.Warning
            : LogLevel.Information;
        log.Log(level,
            "Incident {IncidentId}: {CheckId} {PreviousState} -> {State} [{Severity}] Since={Since} DurationMin={DurationMin} Affected={Affected} {EventKind}",
            e.IncidentId, e.CheckId, e.From, e.To, e.Severity, e.Since, e.DurationMin, string.Join(",", e.Affected), e.Kind);
    }
}

public sealed class MonitorWorker(MonitorCycle cycle, MonitorOptions options, ILogger<MonitorWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.CycleSeconds));
        do
        {
            try
            {
                await cycle.RunAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(ex, "The monitor cycle failed");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

public sealed class OutboxWorker(OutboxDispatcher dispatcher, ILogger<OutboxWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            do
            {
                try
                {
                    await dispatcher.DispatchAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    log.LogError(ex, "Dispatching the outbox failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }
}

public sealed class HeartbeatWorker(HeartbeatPinger pinger, HeartbeatOptions options, MonitorStatus status, ILogger<HeartbeatWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        // Wait for the first light probe at startup (at most 3 minutes); otherwise the self-check fails and the first ping
        // goes out only after IntervalMinutes, which can exceed the healthchecks.io grace period on frequent restarts.
        var startWait = DateTime.UtcNow.AddMinutes(3);
        while (status.LastLightProbeAt is null && DateTime.UtcNow < startWait && !stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch (OperationCanceledException) { return; }
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.IntervalMinutes));
        try
        {
            do
            {
                try
                {
                    await pinger.TickAsync(status.LastLightProbeAt, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    log.LogError("Heartbeat failed: {Error}", ex.GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }
}
