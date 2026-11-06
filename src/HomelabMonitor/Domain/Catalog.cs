namespace HomelabMonitor.Domain;

/// <summary>The rule of one check in the catalog, from the <c>Catalog:Checks</c> configuration section.</summary>
public sealed class CheckDefinition
{
    public string DisplayName { get; set; } = "";
    public CheckSource Source { get; set; } = CheckSource.Light;

    /// <summary>Consecutive non-OK results needed to open an incident (N).</summary>
    public int FailThreshold { get; set; } = 1;

    /// <summary>Consecutive OK results needed to resolve it (M).</summary>
    public int RecoverThreshold { get; set; } = 1;

    /// <summary>Severity on a WARN result.</summary>
    public Severity OnWarn { get; set; } = Severity.Warn;

    /// <summary>Severity on an ERROR result. CRIT is decided here in the catalog, never by the producer of the result.</summary>
    public Severity OnError { get; set; } = Severity.Warn;

    /// <summary>Parent dependencies (check ids). While a parent is OPEN, the child is suppressed under the parent's incident.</summary>
    public string[] Parents { get; set; } = [];

    /// <summary>Name of a rule in <c>Notify:Rules</c>, if a CRIT incident of this check should notify.</summary>
    public string? NotifyRule { get; set; }

    public Severity SeverityFor(CheckStatus status) => status == CheckStatus.Warn ? OnWarn : OnError;
}

public sealed class CatalogOptions
{
    public Dictionary<string, CheckDefinition> Checks { get; set; } = new(StringComparer.Ordinal);
}

public sealed class MonitorCatalog
{
    public const string BrokenCheckId = "monitor.check.broken";

    private readonly Dictionary<string, CheckDefinition> _checks;
    private readonly Dictionary<string, int> _order = new(StringComparer.Ordinal);

    public MonitorCatalog(IDictionary<string, CheckDefinition> checks)
    {
        _checks = new Dictionary<string, CheckDefinition>(checks, StringComparer.Ordinal);
        foreach (var (id, def) in _checks)
        {
            if (string.IsNullOrWhiteSpace(def.DisplayName))
            {
                def.DisplayName = id;
            }
        }

        BuildOrder();
    }

    public IReadOnlyCollection<string> Ids => _checks.Keys;

    public bool TryGet(string checkId, out CheckDefinition definition) => _checks.TryGetValue(checkId, out definition!);

    /// <summary>Returns a default definition (WARN, N=1, M=1) for an unknown check id.</summary>
    public CheckDefinition Get(string checkId) =>
        _checks.TryGetValue(checkId, out var d) ? d : new CheckDefinition { DisplayName = checkId };

    /// <summary>Topological order: parents before children. Unknown ids go last.</summary>
    public int OrderOf(string checkId) => _order.TryGetValue(checkId, out var i) ? i : int.MaxValue;

    private void BuildOrder()
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 = visiting, 2 = done
        foreach (var id in _checks.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            Visit(id, state);
        }
    }

    private void Visit(string id, Dictionary<string, int> state)
    {
        if (state.TryGetValue(id, out var s))
        {
            if (s == 1)
            {
                throw new InvalidOperationException($"Cycle in the dependency graph: {id}");
            }

            return;
        }

        state[id] = 1;
        foreach (var parent in _checks[id].Parents)
        {
            if (!_checks.ContainsKey(parent))
            {
                throw new InvalidOperationException($"{id} refers to an unknown parent: {parent}");
            }

            Visit(parent, state);
        }

        state[id] = 2;
        _order[id] = _order.Count;
    }
}
