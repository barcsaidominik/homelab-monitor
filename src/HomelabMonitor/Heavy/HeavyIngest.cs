using System.Text.Json;
using HomelabMonitor.Domain;
using HomelabMonitor.Store;
using Microsoft.Extensions.Logging;

namespace HomelabMonitor.Heavy;

public sealed record HeavyEntry(string CheckId, CheckStatus Status, IReadOnlyList<string> Messages);

public sealed record HeavyRun(string RunId, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, string Host, IReadOnlyList<HeavyEntry> Results);

public static class HeavyRunParser
{
    public const string Schema = "homelab.heavy/1";

    public static bool TryParse(string json, out HeavyRun? run, out string error)
    {
        run = null;
        error = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema", out var schema) || schema.GetString() != Schema)
            {
                error = "unknown schema";
                return false;
            }

            var runId = root.GetProperty("runId").GetString();
            if (string.IsNullOrWhiteSpace(runId))
            {
                error = "missing runId";
                return false;
            }

            var started = root.GetProperty("startedAt").GetDateTimeOffset();
            var finished = root.GetProperty("finishedAt").GetDateTimeOffset();
            var host = root.TryGetProperty("host", out var h) ? h.GetString() ?? "" : "";
            var results = new List<HeavyEntry>();
            if (root.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var id = item.TryGetProperty("checkId", out var idEl) ? idEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }

                    var status = CheckStatusParser.Parse(item.TryGetProperty("status", out var st) ? st.GetString() : null);
                    var messages = new List<string>();
                    if (item.TryGetProperty("messages", out var ms) && ms.ValueKind == JsonValueKind.Array)
                    {
                        messages.AddRange(ms.EnumerateArray().Select(m => m.GetString() ?? "").Where(m => m.Length > 0));
                    }

                    results.Add(new HeavyEntry(id, status, messages));
                }
            }

            run = new HeavyRun(runId, started, finished, host, results);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            error = ex.GetType().Name;
            return false;
        }
    }
}

public sealed record HeavyBatch(IReadOnlyList<CheckResult> Results, IReadOnlyList<HeavyRun> NewRuns, IReadOnlyList<HeavyRun> SkippedRuns);

/// <summary>Loads the heavy-*.json files of the external heavy checks: every runId once.</summary>
public sealed class HeavyIngest(HeavyOptions options, SqliteStore store, MonitorCatalog catalog, TimeProvider time, ILogger<HeavyIngest> log)
{
    private readonly HashSet<string> _badFiles = new(StringComparer.OrdinalIgnoreCase);

    public HeavyBatch Poll()
    {
        var results = new List<CheckResult>();
        var runs = new List<HeavyRun>();
        var skipped = new List<HeavyRun>();
        if (!Directory.Exists(options.Directory))
        {
            return new HeavyBatch(results, runs, skipped);
        }

        var now = time.GetUtcNow();
        foreach (var file in Directory.EnumerateFiles(options.Directory, "heavy-*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            string json;
            try
            {
                json = File.ReadAllText(file);
            }
            catch (IOException)
            {
                continue; // being written; retried in the next cycle
            }

            if (!HeavyRunParser.TryParse(json, out var run, out var error) || run is null)
            {
                if (_badFiles.Add(file))
                {
                    log.LogWarning("Invalid heavy file: {File} ({Error})", Path.GetFileName(file), error);
                }

                continue;
            }

            if (store.IsRunProcessed(run.RunId))
            {
                continue;
            }

            if (now - run.FinishedAt > TimeSpan.FromHours(options.MaxAgeHours))
            {
                skipped.Add(run);
                continue;
            }

            runs.Add(run);
            foreach (var e in run.Results)
            {
                if (!catalog.TryGet(e.CheckId, out var def) || def.Source != CheckSource.Heavy)
                {
                    continue; // unknown or light check: a heavy result must not override it
                }

                var msg = CheckResult.Trim(string.Join("; ", e.Messages));
                // The observation time is the end of the heavy run, not the ingest time (the report's "last measured" comes from it).
                results.Add(new CheckResult(e.CheckId, e.Status, run.FinishedAt, msg, Source: CheckSource.Heavy, RunId: run.RunId));
            }
        }

        return new HeavyBatch(results, runs, skipped);
    }

    public void MarkProcessed(HeavyBatch batch)
    {
        var now = time.GetUtcNow();
        foreach (var r in batch.NewRuns)
        {
            store.MarkRunProcessed(r.RunId, r.StartedAt, r.FinishedAt, now, applied: true);
        }

        foreach (var r in batch.SkippedRuns)
        {
            store.MarkRunProcessed(r.RunId, r.StartedAt, r.FinishedAt, now, applied: false);
        }
    }

    /// <summary>monitor.heavy.stale: the latest run is older than the threshold (7 hours by default), or there is no run.</summary>
    public CheckResult EvaluateStale(DateTimeOffset? lastFinished)
    {
        var now = time.GetUtcNow();
        if (lastFinished is null)
        {
            return new CheckResult("monitor.heavy.stale", CheckStatus.Warn, now, "No heavy result file yet");
        }

        var age = now - lastFinished.Value;
        return age > TimeSpan.FromHours(options.StaleHours)
            ? new CheckResult("monitor.heavy.stale", CheckStatus.Warn, now, $"The latest heavy run is {(int)age.TotalHours} hours old", Value: age.TotalHours)
            : new CheckResult("monitor.heavy.stale", CheckStatus.Ok, now, "", Value: age.TotalHours);
    }
}
