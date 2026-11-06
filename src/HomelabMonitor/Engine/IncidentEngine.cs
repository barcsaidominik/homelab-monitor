using System.Globalization;
using HomelabMonitor.Domain;

namespace HomelabMonitor.Engine;

public enum EngineEventKind { Transition, Escalation, Affected }

/// <summary>A state transition (or a severity change / change of the affected list). Becomes one Seq event and one incident_events row.</summary>
public sealed record EngineEvent(
    EngineEventKind Kind,
    string CheckKey,
    string CheckId,
    string? IncidentId,
    string? OwnerKey,
    bool NewIncident,
    CheckState From,
    CheckState To,
    Severity Severity,
    DateTimeOffset At,
    DateTimeOffset? Since,
    int DurationMin,
    string Message,
    IReadOnlyList<string> Affected)
{
    /// <summary>A PENDING that went back to OK, or suppressed → OK: no incident of its own, not stored, debug only.</summary>
    public bool Notable => IncidentId is not null;
}

/// <summary>Runtime state of one fingerprint (checkId + target). Persisted as JSON.</summary>
public sealed class CheckRuntime
{
    public string Key { get; set; } = "";
    public string CheckId { get; set; } = "";
    public CheckState State { get; set; } = CheckState.Ok;
    public int FailStreak { get; set; }
    public int OkStreak { get; set; }
    public int UnknownStreak { get; set; }
    public string? IncidentId { get; set; }
    public DateTimeOffset? OpenedAt { get; set; }
    public Severity Severity { get; set; }
    public Severity LastFailSeverity { get; set; }
    public string LastMessage { get; set; } = "";
    public List<string> Affected { get; set; } = [];
    public string? SuppressedBy { get; set; }
    public List<DateTimeOffset> Flips { get; set; } = [];
    public DateTimeOffset? StableSince { get; set; }
    public bool StableIsOk { get; set; }
}

/// <summary>Pure, deterministic incident state machine. Time comes from the injected TimeProvider.</summary>
public sealed class IncidentEngine
{
    public static readonly TimeSpan FlapWindow = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan FlapStable = TimeSpan.FromMinutes(30);
    public const int FlapFlips = 4;
    public const int BrokenAfterUnknown = 3;

    private readonly MonitorCatalog _catalog;
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _tz;
    private readonly Dictionary<string, CheckRuntime> _states = new(StringComparer.Ordinal);
    private readonly List<CheckRuntime> _resolvedRoots = [];
    private readonly Dictionary<string, int> _seq = new(StringComparer.Ordinal);

    public IncidentEngine(
        MonitorCatalog catalog,
        TimeProvider time,
        TimeZoneInfo tz,
        IEnumerable<CheckRuntime>? restored = null,
        IEnumerable<string>? existingIncidentIds = null)
    {
        _catalog = catalog;
        _time = time;
        _tz = tz;
        foreach (var s in restored ?? [])
        {
            _states[s.Key] = s;
        }

        foreach (var id in existingIncidentIds ?? [])
        {
            // INC-yyyyMMdd-NNN
            var parts = id.Split('-');
            if (parts.Length == 3 && int.TryParse(parts[2], CultureInfo.InvariantCulture, out var n))
            {
                var day = parts[1];
                _seq[day] = Math.Max(_seq.GetValueOrDefault(day), n);
            }
        }
    }

    public IReadOnlyCollection<CheckRuntime> Snapshot() => _states.Values.ToList();

    public CheckRuntime? Get(string key) => _states.GetValueOrDefault(key);

    /// <summary>Parents are processed before children, so the order of results within a cycle does not matter.</summary>
    public IReadOnlyList<EngineEvent> ProcessBatch(IEnumerable<CheckResult> results)
    {
        var events = new List<EngineEvent>();
        foreach (var r in results.OrderBy(r => _catalog.OrderOf(r.CheckId)).ThenBy(r => r.Key, StringComparer.Ordinal))
        {
            Process(r, events);
        }

        FlushResolvedRoots(events);
        return events;
    }

    public IReadOnlyList<EngineEvent> Process(CheckResult result)
    {
        var events = new List<EngineEvent>();
        Process(result, events);
        FlushResolvedRoots(events);
        return events;
    }

    private CheckRuntime StateOf(string key, string checkId)
    {
        if (!_states.TryGetValue(key, out var s))
        {
            s = new CheckRuntime { Key = key, CheckId = checkId };
            _states[key] = s;
        }

        return s;
    }

    private void Process(CheckResult r, List<EngineEvent> ev)
    {
        var st = StateOf(r.Key, r.CheckId);
        var isBroken = r.CheckId == MonitorCatalog.BrokenCheckId;

        if (r.Status == CheckStatus.Unknown)
        {
            st.UnknownStreak++;
            if (!isBroken && st.UnknownStreak == BrokenAfterUnknown)
            {
                Process(new CheckResult(MonitorCatalog.BrokenCheckId, CheckStatus.Warn, r.ObservedAt,
                    $"The check does not run ({BrokenAfterUnknown}× UNKNOWN)", Target: r.Key), ev);
            }

            return;
        }

        if (!isBroken && st.UnknownStreak >= BrokenAfterUnknown)
        {
            Process(new CheckResult(MonitorCatalog.BrokenCheckId, CheckStatus.Ok, r.ObservedAt, "", Target: r.Key), ev);
        }

        st.UnknownStreak = 0;

        var def = _catalog.Get(r.CheckId);
        if (r.Status == CheckStatus.Ok)
        {
            OnOk(st, def, ev);
        }
        else
        {
            OnFailure(st, def, r, ev);
        }
    }

    // ---- failure ----

    private void OnFailure(CheckRuntime st, CheckDefinition def, CheckResult r, List<EngineEvent> ev)
    {
        var now = _time.GetUtcNow();
        var sev = def.SeverityFor(r.Status);
        st.LastFailSeverity = sev;
        st.LastMessage = CheckResult.Trim(r.Message);
        st.OkStreak = 0;

        switch (st.State)
        {
            case CheckState.Ok:
            case CheckState.Resolved:
                Emit(ev, st, CheckState.Ok, CheckState.Pending, sev, incidentId: null);
                st.State = CheckState.Pending;
                st.FailStreak = 1;
                TryOpen(st, def, ev);
                break;
            case CheckState.Pending:
                st.FailStreak++;
                TryOpen(st, def, ev);
                break;
            case CheckState.Suppressed:
                st.FailStreak++;
                var root = FindRoot(def, depth: 0);
                if (root is null)
                {
                    st.SuppressedBy = null;
                    OpenNew(st, ev, CheckState.Suppressed);
                }
                else
                {
                    st.SuppressedBy = root.Key;
                }

                break;
            case CheckState.Open:
                if (sev != st.Severity)
                {
                    st.Severity = sev;
                    ev.Add(MakeEvent(EngineEventKind.Escalation, st, CheckState.Open, CheckState.Open, st.IncidentId));
                }

                break;
            case CheckState.Recovering:
                st.Severity = sev;
                st.OkStreak = 0;
                RecordFlip(st, now);
                if (IsFlapping(st))
                {
                    EnterFlapping(st, ev, CheckState.Recovering, stableOk: false);
                }
                else
                {
                    Emit(ev, st, CheckState.Recovering, CheckState.Open, sev, st.IncidentId);
                    st.State = CheckState.Open;
                }

                break;
            case CheckState.Flapping:
                st.Severity = sev;
                if (st.StableIsOk || st.StableSince is null)
                {
                    st.StableIsOk = false;
                    st.StableSince = now;
                }
                else if (now - st.StableSince.Value >= FlapStable)
                {
                    st.Flips.Clear();
                    st.StableSince = null;
                    Emit(ev, st, CheckState.Flapping, CheckState.Open, sev, st.IncidentId);
                    st.State = CheckState.Open;
                }

                break;
        }
    }

    private void TryOpen(CheckRuntime st, CheckDefinition def, List<EngineEvent> ev)
    {
        if (st.FailStreak < def.FailThreshold)
        {
            return;
        }

        var root = FindRoot(def, 0);
        if (root is not null)
        {
            SuppressUnder(st, root, ev);
        }
        else
        {
            OpenNew(st, ev, CheckState.Pending);
        }
    }

    private void OpenNew(CheckRuntime st, List<EngineEvent> ev, CheckState from)
    {
        var now = _time.GetUtcNow();
        st.IncidentId = NextId(now);
        st.OpenedAt = now;
        st.Severity = st.LastFailSeverity;
        st.Affected = [];
        st.SuppressedBy = null;
        st.Flips = [];
        ev.Add(MakeEvent(EngineEventKind.Transition, st, from, CheckState.Open, st.IncidentId, newIncident: true));
        st.State = CheckState.Open;
        SweepSuppress(ev);
    }

    private string NextId(DateTimeOffset now)
    {
        var day = TimeZoneInfo.ConvertTime(now, _tz).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var n = _seq.GetValueOrDefault(day) + 1;
        _seq[day] = n;
        return $"INC-{day}-{n:000}";
    }

    // ---- suppression ----

    private static bool IsActive(CheckState s) => s is CheckState.Open or CheckState.Recovering or CheckState.Flapping;

    /// <summary>The first active incident owner among the parents (through SUPPRESSED parents up to the root).</summary>
    private CheckRuntime? FindRoot(CheckDefinition def, int depth)
    {
        if (depth > 16)
        {
            return null;
        }

        foreach (var parent in def.Parents)
        {
            if (!_states.TryGetValue(parent, out var ps))
            {
                continue;
            }

            if (IsActive(ps.State))
            {
                return ps;
            }

            if (ps.State == CheckState.Suppressed && ps.SuppressedBy is not null
                && _states.TryGetValue(ps.SuppressedBy, out var rs) && IsActive(rs.State))
            {
                return rs;
            }

            if (ps.State == CheckState.Pending)
            {
                var up = FindRoot(_catalog.Get(parent), depth + 1);
                if (up is not null)
                {
                    return up;
                }
            }
        }

        return null;
    }

    private void SuppressUnder(CheckRuntime st, CheckRuntime root, List<EngineEvent> ev)
    {
        var from = st.State;
        if (from == CheckState.Open)
        {
            // close the own incident: the suppressing parent's incident takes over
            ev.Add(MakeEvent(EngineEventKind.Transition, st, CheckState.Open, CheckState.Suppressed, st.IncidentId));
            foreach (var child in _states.Values.Where(c => c.SuppressedBy == st.Key))
            {
                child.SuppressedBy = root.Key;
                AddAffected(root, child.Key);
            }

            st.IncidentId = null;
            st.OpenedAt = null;
            st.Affected = [];
        }
        else
        {
            ev.Add(MakeEvent(EngineEventKind.Transition, st, from, CheckState.Suppressed, root.IncidentId, ownerKey: root.Key));
        }

        st.State = CheckState.Suppressed;
        st.SuppressedBy = root.Key;
        AddAffected(root, st.Key);
        ev.Add(MakeEvent(EngineEventKind.Affected, root, root.State, root.State, root.IncidentId));
    }

    private static void AddAffected(CheckRuntime root, string key)
    {
        if (!root.Affected.Contains(key, StringComparer.Ordinal))
        {
            root.Affected.Add(key);
        }
    }

    /// <summary>After a parent opens, children that are already OPEN get suppressed (in topological order).</summary>
    private void SweepSuppress(List<EngineEvent> ev)
    {
        foreach (var s in _states.Values.Where(s => s.State == CheckState.Open).OrderBy(s => _catalog.OrderOf(s.CheckId)).ToList())
        {
            var root = FindRoot(_catalog.Get(s.CheckId), 0);
            if (root is not null && root != s)
            {
                SuppressUnder(s, root, ev);
            }
        }
    }

    /// <summary>Suppressed children of resolved parents are decided at the end of the batch, so that the child's OK result in the same batch does not open a false incident.</summary>
    private void FlushResolvedRoots(List<EngineEvent> ev)
    {
        var roots = _resolvedRoots.ToList();
        _resolvedRoots.Clear();
        foreach (var r in roots)
        {
            OnRootResolved(r, ev);
        }
    }

    private void OnRootResolved(CheckRuntime root, List<EngineEvent> ev)
    {
        foreach (var s in _states.Values.Where(s => s.State == CheckState.Suppressed && s.SuppressedBy == root.Key)
                     .OrderBy(s => _catalog.OrderOf(s.CheckId)).ToList())
        {
            s.SuppressedBy = null;
            var other = FindRoot(_catalog.Get(s.CheckId), 0);
            if (other is not null)
            {
                s.SuppressedBy = other.Key;
                AddAffected(other, s.Key);
                ev.Add(MakeEvent(EngineEventKind.Affected, other, other.State, other.State, other.IncidentId));
            }
            else
            {
                OpenNew(s, ev, CheckState.Suppressed);
            }
        }
    }

    // ---- OK ----

    private void OnOk(CheckRuntime st, CheckDefinition def, List<EngineEvent> ev)
    {
        var now = _time.GetUtcNow();
        switch (st.State)
        {
            case CheckState.Ok:
            case CheckState.Resolved:
                st.FailStreak = 0;
                break;
            case CheckState.Pending:
                Emit(ev, st, CheckState.Pending, CheckState.Ok, Severity.Info, null);
                st.State = CheckState.Ok;
                st.FailStreak = 0;
                break;
            case CheckState.Suppressed:
                Emit(ev, st, CheckState.Suppressed, CheckState.Ok, st.LastFailSeverity, null);
                st.State = CheckState.Ok;
                st.SuppressedBy = null;
                st.FailStreak = 0;
                break;
            case CheckState.Open:
                st.OkStreak = 1;
                RecordFlip(st, now);
                if (IsFlapping(st))
                {
                    EnterFlapping(st, ev, CheckState.Open, stableOk: true);
                    break;
                }

                Emit(ev, st, CheckState.Open, CheckState.Recovering, st.Severity, st.IncidentId);
                st.State = CheckState.Recovering;
                if (st.OkStreak >= def.RecoverThreshold)
                {
                    Resolve(st, ev, CheckState.Recovering);
                }

                break;
            case CheckState.Recovering:
                st.OkStreak++;
                if (st.OkStreak >= def.RecoverThreshold)
                {
                    Resolve(st, ev, CheckState.Recovering);
                }

                break;
            case CheckState.Flapping:
                if (!st.StableIsOk || st.StableSince is null)
                {
                    st.StableIsOk = true;
                    st.StableSince = now;
                }
                else if (now - st.StableSince.Value >= FlapStable)
                {
                    Resolve(st, ev, CheckState.Flapping);
                }

                break;
        }
    }

    private void Resolve(CheckRuntime st, List<EngineEvent> ev, CheckState from)
    {
        ev.Add(MakeEvent(EngineEventKind.Transition, st, from, CheckState.Resolved, st.IncidentId));
        st.State = CheckState.Ok;
        st.IncidentId = null;
        st.OpenedAt = null;
        st.OkStreak = 0;
        st.FailStreak = 0;
        st.Flips = [];
        st.StableSince = null;
        _resolvedRoots.Add(st);
        st.Affected = [];
    }

    // ---- flapping ----

    private static void RecordFlip(CheckRuntime st, DateTimeOffset now)
    {
        st.Flips.RemoveAll(f => now - f > FlapWindow);
        st.Flips.Add(now);
    }

    private static bool IsFlapping(CheckRuntime st) => st.Flips.Count >= FlapFlips;

    private void EnterFlapping(CheckRuntime st, List<EngineEvent> ev, CheckState from, bool stableOk)
    {
        ev.Add(MakeEvent(EngineEventKind.Transition, st, from, CheckState.Flapping, st.IncidentId));
        st.State = CheckState.Flapping;
        st.StableIsOk = stableOk;
        st.StableSince = _time.GetUtcNow();
    }

    // ---- events ----

    private void Emit(List<EngineEvent> ev, CheckRuntime st, CheckState from, CheckState to, Severity sev, string? incidentId) =>
        ev.Add(MakeEvent(EngineEventKind.Transition, st, from, to, incidentId, severityOverride: sev));

    private EngineEvent MakeEvent(
        EngineEventKind kind, CheckRuntime st, CheckState from, CheckState to, string? incidentId,
        bool newIncident = false, string? ownerKey = null, Severity? severityOverride = null)
    {
        var now = _time.GetUtcNow();
        var since = st.OpenedAt;
        var dur = since is null ? 0 : (int)Math.Max(0, (now - since.Value).TotalMinutes);
        return new EngineEvent(kind, st.Key, st.CheckId, incidentId, ownerKey ?? st.Key, newIncident, from, to,
            severityOverride ?? st.Severity, now, since, dur, st.LastMessage, st.Affected.ToList());
    }
}
