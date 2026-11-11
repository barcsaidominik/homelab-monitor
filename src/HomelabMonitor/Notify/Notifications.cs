using System.Globalization;
using HomelabMonitor.Domain;
using HomelabMonitor.Store;
using Microsoft.Extensions.Logging;

namespace HomelabMonitor.Notify;

/// <summary>
/// Plans notifications from the incident table: one message when an incident opens, reminders, one when it resolves;
/// the dedupe key is IncidentId + kind. The incident table is the source of truth, so an incident that is still failing
/// at the end of a maintenance window gets its notification automatically. Only CRIT incidents of checks with a
/// <c>NotifyRule</c> (or with such checks among the affected ones) are notified.
/// </summary>
public sealed class NotificationPlanner(MonitorCatalog catalog, SqliteStore store, TimeProvider time, NotifyOptions options)
{
    /// <summary>Creates the missing outbox rows. Returns the number of new rows (merged ones included).</summary>
    public int Plan(Func<string, bool> isMuted)
    {
        var now = time.GetUtcNow();
        var created = 0;
        var incidents = store.GetIncidentsForPlanning(now.AddDays(-7));
        foreach (var inc in incidents)
        {
            if (inc.State == CheckState.Suppressed)
            {
                created += PlanMoved(inc, incidents, isMuted, now);
                continue;
            }

            var subject = Resolve(inc);
            if (subject is null)
            {
                continue;
            }

            var (ruleName, subjectId, names) = subject.Value;
            var rule = options.Rules[ruleName];
            var openKey = $"{inc.Id}:open";
            var hasOpen = store.OutboxKeyExists(openKey);

            if (inc.State == CheckState.Resolved)
            {
                if (hasOpen && inc.ResolvedAt is not null)
                {
                    var dur = FormatDuration(inc.ResolvedAt.Value - inc.OpenedAt);
                    created += store.TryEnqueue($"{inc.Id}:resolved", inc.Id, "resolved", "default", "white_check_mark",
                        $"{options.Prefix} Recovered: {string.Join(", ", names)} (after {dur}). {inc.Id}", now) ? 1 : 0;
                }

                continue;
            }

            if (inc.State == CheckState.Flapping || isMuted(inc.CheckId) || isMuted(subjectId))
            {
                continue;
            }

            var elapsed = now - inc.OpenedAt;
            if (!hasOpen)
            {
                created += EnqueueOpen(inc, rule, names, elapsed, now);
                continue;
            }

            var reminderKey = ReminderKey(inc.Id, rule, elapsed);
            if (reminderKey is not null)
            {
                var text = $"{options.Prefix} CRITICAL (reminder): {Body(rule, names)}, still open (for {FormatDuration(elapsed)}). {inc.Id}";
                created += store.TryEnqueue(reminderKey, inc.Id, "reminder", "urgent", "bell", text, now) ? 1 : 0;
            }
        }

        return created;
    }

    private int EnqueueOpen(IncidentRecord inc, NotifyRuleOptions rule, IReadOnlyList<string> names, TimeSpan elapsed, DateTimeOffset now)
    {
        // The duration only means something when the first message is late (e.g. at the end of a maintenance window);
        // right after the incident opens it would always read "for 0 min".
        var withDuration = rule.WithDuration && elapsed >= TimeSpan.FromMinutes(1);
        var text = $"{options.Prefix} CRITICAL: {Body(rule, names)}{(withDuration ? $" (for {FormatDuration(elapsed)})" : "")}. {inc.Id}";
        var windowStart = now - TimeSpan.FromMinutes(options.StormWindowMinutes);
        var recent = store.CountOutbox("open", windowStart);
        if (recent < options.StormCount)
        {
            return store.TryEnqueue($"{inc.Id}:open", inc.Id, "open", "urgent", "rotating_light", text, now) ? 1 : 0;
        }

        // storm: one digest message; opens not sent yet are merged into it
        var created = 0;
        if (store.GetOutbox("digest", null, windowStart).Count == 0)
        {
            var pending = store.GetOutbox("open", "pending", windowStart);
            foreach (var p in pending)
            {
                store.SetOutboxStatus(p.Id, "merged");
            }

            var ids = pending.Select(p => p.IncidentId).Append(inc.Id).Where(i => i is not null);
            var digest = $"{options.Prefix} CRITICAL: {recent + 1} critical incidents opened within {options.StormWindowMinutes} min ({string.Join(", ", ids)}). Details in Seq";
            created += store.TryEnqueue($"digest:{inc.Id}", inc.Id, "digest", "urgent", "rotating_light", digest, now) ? 1 : 0;
        }

        created += store.TryEnqueue($"{inc.Id}:open", inc.Id, "open", "urgent", "rotating_light", text, now, status: "merged") ? 1 : 0;
        return created;
    }

    /// <summary>
    /// An incident that already notified and was then taken over by a parent's incident gets one closing message, so every
    /// "CRITICAL" message has a follow-up. Without its own open message (or if that expired unsent) there is nothing to close.
    /// </summary>
    private int PlanMoved(IncidentRecord inc, IReadOnlyList<IncidentRecord> incidents, Func<string, bool> isMuted, DateTimeOffset now)
    {
        var openStatus = store.OutboxStatus($"{inc.Id}:open");
        if (openStatus is null or "expired" || isMuted(inc.CheckId))
        {
            return 0;
        }

        var names = Resolve(inc)?.Names ?? [catalog.Get(inc.CheckId).DisplayName];
        var owner = incidents.FirstOrDefault(o => o.State != CheckState.Suppressed && o.State != CheckState.Resolved
            && o.Affected.Contains(inc.CheckKey, StringComparer.Ordinal));
        var target = owner is null
            ? "the incident of a service it depends on"
            : $"{owner.Id} ({catalog.Get(owner.CheckId).DisplayName})";
        var text = $"{options.Prefix} Update: {string.Join(", ", names)} is now tracked under {target}; no separate recovery message. {inc.Id}";
        return store.TryEnqueue($"{inc.Id}:moved", inc.Id, "moved", "default", "link", text, now) ? 1 : 0;
    }

    private static string? ReminderKey(string incidentId, NotifyRuleOptions rule, TimeSpan elapsed)
    {
        if (rule.Daily)
        {
            var days = (int)(elapsed.TotalHours / 24);
            return days >= 1 ? $"{incidentId}:reminder-d{days}" : null;
        }

        return rule.ReminderAfterHours is { } after && elapsed >= TimeSpan.FromHours(after) ? $"{incidentId}:reminder-1" : null;
    }

    private static string Body(NotifyRuleOptions rule, IReadOnlyList<string> names) =>
        rule.Body.Replace("{names}", string.Join(", ", names), StringComparison.Ordinal);

    private (string Rule, string SubjectId, List<string> Names)? Resolve(IncidentRecord inc)
    {
        var own = catalog.Get(inc.CheckId);
        if (own.NotifyRule is not null && options.Rules.ContainsKey(own.NotifyRule) && inc.Severity == Severity.Crit)
        {
            return (own.NotifyRule, inc.CheckId, [own.DisplayName]);
        }

        var children = inc.Affected
            .Select(k => k.Split('|')[0])
            .Select(id => (id, def: catalog.Get(id)))
            .Where(x => x.def.NotifyRule is not null && options.Rules.ContainsKey(x.def.NotifyRule))
            .ToList();
        if (children.Count == 0)
        {
            return null;
        }

        return (children[0].def.NotifyRule!, children[0].id, children.Select(c => c.def.DisplayName).Distinct().ToList());
    }

    public static string FormatDuration(TimeSpan d)
    {
        var minutes = (int)Math.Max(0, d.TotalMinutes);
        return minutes < 60
            ? string.Create(CultureInfo.InvariantCulture, $"{minutes} min")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes / 60} h");
    }
}

public interface INotificationSender
{
    Task SendAsync(OutboxMessage message, CancellationToken ct);
}

/// <summary>Drains the outbox: rate limit (at most N per hour), exponential retry (1, 2, 4… min, max 60), expiry.</summary>
public sealed class OutboxDispatcher(SqliteStore store, INotificationSender sender, TimeProvider time, NotifyOptions options, ILogger<OutboxDispatcher> log)
{
    public async Task<int> DispatchAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var expired = store.ExpirePending(now.AddMinutes(-options.MaxAgeMinutes));
        if (expired > 0)
        {
            log.LogInformation("Expired notifications in the outbox: {Count}", expired);
        }

        if (!options.Enabled)
        {
            return 0; // shadow mode: rows stay (expiry cleans them up)
        }

        var budget = options.RatePerHour - store.CountSentSince(now.AddHours(-1));
        if (budget <= 0)
        {
            return 0;
        }

        var sent = 0;
        foreach (var m in store.GetDue(now, budget))
        {
            try
            {
                await sender.SendAsync(m, ct);
                store.MarkSent(m.Id, time.GetUtcNow());
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var attempts = m.Attempts + 1;
                var delay = TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, attempts - 1)));
                store.MarkFailed(m.Id, time.GetUtcNow() + delay, ex.GetType().Name);
                log.LogWarning("Sending a notification failed ({Error}), retry in {Delay} min", ex.GetType().Name, delay.TotalMinutes);
            }
        }

        return sent;
    }
}

/// <summary>ntfy sender. The topic comes from a file and is never logged. Off by default (Notify:Enabled=false).</summary>
public sealed class NtfySender(HttpClient client, NotifyOptions options) : INotificationSender
{
    public async Task SendAsync(OutboxMessage message, CancellationToken ct)
    {
        var topic = (await File.ReadAllTextAsync(options.TopicFile, ct)).Trim();
        if (topic.Length == 0)
        {
            throw new InvalidOperationException("Empty topic");
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(topic)}")
        {
            Content = new StringContent(message.Text, System.Text.Encoding.UTF8, "text/plain"),
        };
        req.Headers.Add("Priority", message.Priority);
        req.Headers.Add("Tags", message.Tags);
        using var resp = await client.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"ntfy HTTP {(int)resp.StatusCode}");
        }
    }
}

public static class SelfCheck
{
    /// <summary>Healthy when the last probe is younger than 3 minutes, the database is writable and the outbox is not stuck.</summary>
    public static bool IsHealthy(DateTimeOffset now, DateTimeOffset? lastProbe, bool dbWritable, DateTimeOffset? oldestDuePending,
        bool notifyEnabled, int stuckMinutes) =>
        lastProbe is not null && now - lastProbe.Value < TimeSpan.FromMinutes(3)
        && dbWritable
        && !(notifyEnabled && oldestDuePending is not null && now - oldestDuePending.Value > TimeSpan.FromMinutes(stuckMinutes));
}

/// <summary>Dead man's switch: a healthchecks.io ping every few minutes, only while the self-check passes. The URL is a secret: read from a file, never logged.</summary>
public sealed class HeartbeatPinger(HeartbeatOptions options, NotifyOptions notify, HttpClient client, SqliteStore store, TimeProvider time, ILogger<HeartbeatPinger> log)
{
    public async Task<bool> TickAsync(DateTimeOffset? lastLightProbe, CancellationToken ct)
    {
        if (!options.Enabled)
        {
            return false;
        }

        var now = time.GetUtcNow();
        var healthy = SelfCheck.IsHealthy(now, lastLightProbe, store.CheckWritable(now), store.OldestDuePending(now), notify.Enabled, options.StuckOutboxMinutes);
        if (!healthy)
        {
            log.LogWarning("Heartbeat skipped: the self-check is not OK");
            return false;
        }

        try
        {
            var url = (await File.ReadAllTextAsync(options.UrlFile, ct)).Trim();
            using var resp = await client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("Heartbeat HTTP {Status}", (int)resp.StatusCode);
                return false;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or UnauthorizedAccessException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Heartbeat failed: {Error}", ex.GetType().Name);
            return false;
        }

        store.SetMeta("heartbeat_last_ok", now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        return true;
    }
}
