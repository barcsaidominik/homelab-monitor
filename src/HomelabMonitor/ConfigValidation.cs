using HomelabMonitor.Domain;
using HomelabMonitor.Probes;

namespace HomelabMonitor;

/// <summary>
/// Cross-section consistency checks that the configuration binder cannot do. The service refuses to start on any error:
/// a typo in a check id would otherwise turn into a check that silently never opens an incident or never notifies.
/// </summary>
public static class ConfigValidation
{
    public static IReadOnlyList<string> Validate(MonitorCatalog catalog, ProbeOptions probes, MonitorOptions monitor, NotifyOptions notify)
    {
        var errors = new List<string>();

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(monitor.TimeZone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // A typo would silently shift maintenance windows and report times.
            errors.Add($"Monitor:TimeZone '{monitor.TimeZone}' is not a known time zone id.");
        }

        ValidateProbes(probes, errors);

        foreach (var (name, rule) in notify.Rules)
        {
            if (rule.ReminderAfterHours is { } hours && (hours <= 0 || rule.Daily))
            {
                errors.Add($"Notify:Rules:{name}: ReminderAfterHours must be positive and cannot be combined with Daily.");
            }
        }

        foreach (var id in catalog.Ids)
        {
            if (catalog.Get(id).NotifyRule is { } rule && !notify.Rules.ContainsKey(rule))
            {
                errors.Add($"Catalog:Checks:{id}:NotifyRule '{rule}' is not defined under Notify:Rules.");
            }
        }

        foreach (var id in ProbeCheckIds(probes))
        {
            if (!catalog.TryGet(id, out var def))
            {
                errors.Add($"Probe check id '{id}' is missing from Catalog:Checks.");
            }
            else if (def.Source != CheckSource.Light)
            {
                errors.Add($"Probe check id '{id}' is a {def.Source} check in the catalog; probes produce Light results.");
            }
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var u in monitor.Reports.Uptime)
        {
            if (string.IsNullOrWhiteSpace(u.Key) || u.Key.Contains('.', StringComparison.Ordinal) || !keys.Add(u.Key))
            {
                errors.Add($"Monitor:Reports:Uptime key '{u.Key}' must be non-empty, unique and without dots.");
            }
        }

        foreach (var (section, check) in monitor.Reports.Uptime.Select(u => ("Uptime", u.Check))
                     .Concat(monitor.Reports.Backups.Select(b => ("Backups", b.Check)))
                     .Concat(monitor.Reports.Values.Select(v => ("Values", v.Check))))
        {
            if (!catalog.TryGet(check, out _))
            {
                errors.Add($"Monitor:Reports:{section} refers to '{check}', which is missing from Catalog:Checks.");
            }
        }

        return errors;
    }

    private static void ValidateProbes(ProbeOptions o, List<string> errors)
    {
        void Address(string key, string value)
        {
            if (!System.Net.IPAddress.TryParse(value, out _))
            {
                errors.Add($"{key} '{value}' is not an IP address.");
            }
        }

        foreach (var h in o.Http)
        {
            if (!Uri.TryCreate(h.Url, UriKind.Absolute, out _))
            {
                errors.Add($"Probes:Http '{h.CheckId}': '{h.Url}' is not an absolute URL.");
            }
        }

        if (o.Http.Any(h => h.ForceLan))
        {
            Address("Probes:LanAddress", o.LanAddress);
        }

        foreach (var d in o.Dns)
        {
            Address($"Probes:Dns '{d.CheckId}' Server", d.Server);
        }

        foreach (var t in o.Tls)
        {
            Address($"Probes:Tls '{t.CheckId}' Address", t.Address);
        }

        if (o.FanState.Enabled && string.IsNullOrWhiteSpace(o.FanState.StatePath))
        {
            errors.Add("Probes:FanState is enabled but StatePath is empty.");
        }

        foreach (var g in ProbeCheckIds(o).GroupBy(id => id, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            errors.Add($"Check id '{g.Key}' is produced by more than one probe.");
        }
    }

    /// <summary>The check ids the configured light probes produce (without building the probes).</summary>
    public static IEnumerable<string> ProbeCheckIds(ProbeOptions o) =>
        o.Http.Select(h => h.CheckId)
            .Concat(o.Dns.Select(d => d.CheckId))
            .Concat(o.Tls.Select(t => t.CheckId))
            .Concat(o.Services.Keys)
            .Concat(o.Processes.Keys)
            .Concat(o.JsonApi.SelectMany(j => j.Thresholds.Select(t => t.CheckId).Concat(j.Stall is null ? [] : [j.Stall.CheckId])))
            .Concat(o.FanState.Enabled ? [o.FanState.FanCheckId, o.FanState.CpuCheckId] : []);
}
