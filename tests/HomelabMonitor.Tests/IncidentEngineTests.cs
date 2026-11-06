using HomelabMonitor.Domain;
using HomelabMonitor.Engine;
using Xunit;
using static HomelabMonitor.Tests.T;

namespace HomelabMonitor.Tests;

public sealed class IncidentEngineTests
{
    private readonly ManualTime _time = new(Start);

    private IncidentEngine Engine(MonitorCatalog c) => new(c, _time, Budapest);

    private static void Feed(IncidentEngine e, CheckResult r, int times)
    {
        for (var i = 0; i < times; i++)
        {
            e.Process(r);
        }
    }

    [Fact]
    public void FailureBelowThreshold_StaysPending_AndSingleOkClearsWithoutIncident()
    {
        var e = Engine(Catalog(("a", Def(n: 3))));
        e.Process(Err("a"));
        e.Process(Err("a"));
        Assert.Equal(CheckState.Pending, e.Get("a")!.State);
        var ev = e.Process(Ok("a"));
        Assert.Equal(CheckState.Ok, e.Get("a")!.State);
        Assert.All(ev, x => Assert.False(x.Notable));
    }

    [Fact]
    public void Threshold_OpensIncident_WithIdAndOneEvent()
    {
        var e = Engine(Catalog(("a", Def(n: 3))));
        e.Process(Err("a"));
        e.Process(Err("a"));
        var ev = e.Process(Err("a"));
        var open = Assert.Single(ev);
        Assert.True(open.NewIncident);
        Assert.Equal(CheckState.Open, open.To);
        Assert.Equal("INC-20261102-001", open.IncidentId);
    }

    [Fact]
    public void IncidentId_UsesLocalDate_AndSequences()
    {
        var e = Engine(Catalog(("a", Def()), ("b", Def())));
        _time.Set(new DateTimeOffset(2026, 11, 2, 23, 30, 0, TimeSpan.Zero)); // helyi: 11-03 00:30
        var id1 = e.Process(Err("a")).Single(x => x.NewIncident).IncidentId;
        var id2 = e.Process(Err("b")).Single(x => x.NewIncident).IncidentId;
        Assert.Equal("INC-20261103-001", id1);
        Assert.Equal("INC-20261103-002", id2);
    }

    [Fact]
    public void Recovery_RequiresMConsecutiveOk_AndFailureInterruptsIt()
    {
        var e = Engine(Catalog(("a", Def(m: 3))));
        e.Process(Err("a"));
        e.Process(Ok("a"));
        Assert.Equal(CheckState.Recovering, e.Get("a")!.State);
        e.Process(Ok("a"));
        e.Process(Err("a"));
        Assert.Equal(CheckState.Open, e.Get("a")!.State);
        e.Process(Ok("a"));
        e.Process(Ok("a"));
        Assert.Equal(CheckState.Recovering, e.Get("a")!.State);
        var ev = e.Process(Ok("a"));
        Assert.Contains(ev, x => x.To == CheckState.Resolved);
        Assert.Equal(CheckState.Ok, e.Get("a")!.State);
    }

    [Fact]
    public void ResolvedEvent_CarriesDuration()
    {
        var e = Engine(Catalog(("a", Def())));
        e.Process(Err("a"));
        _time.Advance(TimeSpan.FromMinutes(42));
        var ev = e.Process(Ok("a"));
        Assert.Equal(42, ev.Single(x => x.To == CheckState.Resolved).DurationMin);
    }

    [Fact]
    public void SeverityEscalation_EmitsEvent_FromCatalogMapping()
    {
        var def = new CheckDefinition { DisplayName = "d", OnWarn = Severity.Warn, OnError = Severity.Crit };
        var e = Engine(Catalog(("disk", def)));
        var open = e.Process(R("disk", CheckStatus.Warn)).Single(x => x.NewIncident);
        Assert.Equal(Severity.Warn, open.Severity);
        var esc = e.Process(R("disk", CheckStatus.Error)).Single();
        Assert.Equal(EngineEventKind.Escalation, esc.Kind);
        Assert.Equal(Severity.Crit, esc.Severity);
    }

    [Fact]
    public void ChildSuppressed_WhenParentOpen_AndParentGetsAffected()
    {
        var e = Engine(Catalog(("p", Def()), ("c", Def(parents: ["p"]))));
        e.Process(Err("p"));
        e.Process(Err("c"));
        Assert.Equal(CheckState.Suppressed, e.Get("c")!.State);
        Assert.Null(e.Get("c")!.IncidentId);
        Assert.Contains("c", e.Get("p")!.Affected);
    }

    [Fact]
    public void BatchOrder_ParentFirst_EvenIfChildListedFirst()
    {
        var e = Engine(Catalog(("p", Def()), ("c", Def(parents: ["p"]))));
        e.ProcessBatch([Err("c"), Err("p")]);
        Assert.Equal(CheckState.Suppressed, e.Get("c")!.State);
        Assert.Equal(CheckState.Open, e.Get("p")!.State);
    }

    [Fact]
    public void ChildAlreadyOpen_IsSuppressed_WhenParentOpensLater()
    {
        var e = Engine(Catalog(("p", Def(n: 2)), ("c", Def(parents: ["p"]))));
        e.Process(Err("p"));
        e.Process(Err("c"));
        Assert.Equal(CheckState.Open, e.Get("c")!.State);
        e.Process(Err("p"));
        Assert.Equal(CheckState.Suppressed, e.Get("c")!.State);
        Assert.Contains("c", e.Get("p")!.Affected);
    }

    [Fact]
    public void ParentRecoveringBeforeChild_ChildStaysSuppressed_ThenOpensOnParentResolve()
    {
        var e = Engine(Catalog(("p", Def(m: 2)), ("c", Def(parents: ["p"]))));
        e.Process(Err("p"));
        e.Process(Err("c"));
        e.Process(Ok("p"));
        Assert.Equal(CheckState.Recovering, e.Get("p")!.State);
        e.Process(Err("c"));
        Assert.Equal(CheckState.Suppressed, e.Get("c")!.State);
        var ev = e.Process(Ok("p"));
        Assert.Contains(ev, x => x.CheckId == "p" && x.To == CheckState.Resolved);
        Assert.Equal(CheckState.Open, e.Get("c")!.State);
        Assert.NotNull(e.Get("c")!.IncidentId);
        Assert.Contains(ev, x => x.CheckId == "c" && x.NewIncident);
    }

    [Fact]
    public void SuppressedChild_BecomesOk_WhenItRecoversFirst()
    {
        var e = Engine(Catalog(("p", Def()), ("c", Def(parents: ["p"]))));
        e.Process(Err("p"));
        e.Process(Err("c"));
        e.Process(Ok("c"));
        Assert.Equal(CheckState.Ok, e.Get("c")!.State);
        e.Process(Ok("p"));
        Assert.Equal(CheckState.Ok, e.Get("c")!.State);
    }

    [Fact]
    public void TransitiveSuppression_GrandchildPointsToRootIncident()
    {
        var e = Engine(Catalog(("g", Def()), ("p", Def(parents: ["g"])), ("c", Def(parents: ["p"]))));
        e.ProcessBatch([Err("g"), Err("p"), Err("c")]);
        Assert.Equal(CheckState.Suppressed, e.Get("p")!.State);
        Assert.Equal(CheckState.Suppressed, e.Get("c")!.State);
        Assert.Equal("g", e.Get("c")!.SuppressedBy);
        Assert.Contains("p", e.Get("g")!.Affected);
        Assert.Contains("c", e.Get("g")!.Affected);
    }

    [Fact]
    public void ChildOpenFirst_ThenChainOpens_RehomesAffectedToRoot()
    {
        var e = Engine(Catalog(("g", Def(n: 3)), ("p", Def(n: 2, parents: ["g"])), ("c", Def(parents: ["p"]))));
        e.Process(Err("c")); // c OPEN
        e.Process(Err("p"));
        e.Process(Err("p")); // p OPEN -> c suppressed under p
        Assert.Equal("p", e.Get("c")!.SuppressedBy);
        Feed(e, Err("g"), 3); // g OPEN -> p suppressed, c re-homed
        Assert.Equal("g", e.Get("p")!.SuppressedBy);
        Assert.Equal("g", e.Get("c")!.SuppressedBy);
        Assert.Contains("c", e.Get("g")!.Affected);
        Assert.Contains("p", e.Get("g")!.Affected);
    }

    private void FlipPairs(IncidentEngine e, int pairs)
    {
        for (var i = 0; i < pairs; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            e.Process(Ok("a"));
            _time.Advance(TimeSpan.FromMinutes(1));
            e.Process(Err("a"));
        }
    }

    [Fact]
    public void Flapping_EntersAfterFourFlips_AndExitsToResolvedAfter30StableOkMinutes()
    {
        var e = Engine(Catalog(("a", Def(m: 3))));
        e.Process(Err("a"));
        FlipPairs(e, 1);
        Assert.Equal(CheckState.Open, e.Get("a")!.State);
        _time.Advance(TimeSpan.FromMinutes(1));
        e.Process(Ok("a"));
        _time.Advance(TimeSpan.FromMinutes(1));
        var ev = e.Process(Err("a")); // 4th flip
        Assert.Equal(CheckState.Flapping, e.Get("a")!.State);
        Assert.Single(ev, x => x.To == CheckState.Flapping);
        var inc = e.Get("a")!.IncidentId;

        _time.Advance(TimeSpan.FromMinutes(1));
        e.Process(Ok("a")); // stabil OK kezdete
        _time.Advance(TimeSpan.FromMinutes(29));
        e.Process(Ok("a"));
        Assert.Equal(CheckState.Flapping, e.Get("a")!.State);
        _time.Advance(TimeSpan.FromMinutes(1));
        var ev2 = e.Process(Ok("a"));
        Assert.Contains(ev2, x => x.To == CheckState.Resolved && x.IncidentId == inc);
        Assert.Equal(CheckState.Ok, e.Get("a")!.State);
    }

    [Fact]
    public void Flapping_ExitsToOpen_After30StableFailingMinutes_AndDoesNotImmediatelyReflap()
    {
        var e = Engine(Catalog(("a", Def(m: 3))));
        e.Process(Err("a"));
        FlipPairs(e, 2);
        Assert.Equal(CheckState.Flapping, e.Get("a")!.State);
        _time.Advance(TimeSpan.FromMinutes(29));
        e.Process(Err("a"));
        Assert.Equal(CheckState.Flapping, e.Get("a")!.State);
        _time.Advance(TimeSpan.FromMinutes(1));
        e.Process(Err("a"));
        Assert.Equal(CheckState.Open, e.Get("a")!.State);
        _time.Advance(TimeSpan.FromMinutes(1));
        e.Process(Ok("a"));
        Assert.Equal(CheckState.Recovering, e.Get("a")!.State);
    }

    [Fact]
    public void FlipsOlderThan60Minutes_DoNotCountTowardsFlapping()
    {
        var e = Engine(Catalog(("a", Def(m: 5))));
        e.Process(Err("a"));
        for (var i = 0; i < 4; i++)
        {
            e.Process(Ok("a"));
            e.Process(Err("a"));
            _time.Advance(TimeSpan.FromMinutes(61));
        }

        Assert.NotEqual(CheckState.Flapping, e.Get("a")!.State);
    }

    [Fact]
    public void Unknown_IsNotFailure_AndDoesNotChangeState()
    {
        var e = Engine(Catalog(("a", Def(n: 2))));
        e.Process(Err("a"));
        e.Process(Unk("a"));
        Assert.Equal(CheckState.Pending, e.Get("a")!.State);
        e.Process(Err("a"));
        Assert.Equal(CheckState.Open, e.Get("a")!.State);
        e.Process(Unk("a"));
        Assert.Equal(CheckState.Open, e.Get("a")!.State);
    }

    [Fact]
    public void ThreeConsecutiveUnknown_RaisesBrokenWarn_AndClearsOnNextKnownResult()
    {
        var e = Engine(Catalog(("a", Def())));
        e.Process(Unk("a"));
        e.Process(Unk("a"));
        Assert.Null(e.Get("monitor.check.broken|a"));
        var ev = e.Process(Unk("a"));
        var open = Assert.Single(ev, x => x.NewIncident);
        Assert.Equal(MonitorCatalog.BrokenCheckId, open.CheckId);
        Assert.Equal(Severity.Warn, open.Severity);
        Assert.Equal(CheckState.Open, e.Get("monitor.check.broken|a")!.State);
        var ev2 = e.Process(Ok("a"));
        Assert.Contains(ev2, x => x.CheckId == MonitorCatalog.BrokenCheckId && x.To == CheckState.Resolved);
    }

    [Fact]
    public void UnknownStreak_IsBrokenByKnownResult()
    {
        var e = Engine(Catalog(("a", Def())));
        e.Process(Unk("a"));
        e.Process(Unk("a"));
        e.Process(Ok("a"));
        e.Process(Unk("a"));
        e.Process(Unk("a"));
        Assert.Null(e.Get("monitor.check.broken|a"));
    }

    [Fact]
    public void Targets_AreSeparateFingerprints()
    {
        var e = Engine(Catalog(("a", Def())));
        e.Process(R("a", CheckStatus.Error, "t1"));
        Assert.Equal(CheckState.Open, e.Get("a|t1")!.State);
        Assert.Null(e.Get("a|t2"));
    }

    [Fact]
    public void RestoredState_ContinuesIncidentAndIdSequence()
    {
        var cat = Catalog(("a", Def()), ("b", Def()));
        var e1 = Engine(cat);
        e1.Process(Err("a"));
        var e2 = new IncidentEngine(cat, _time, Budapest, e1.Snapshot(), ["INC-20261102-001"]);
        Assert.Equal(CheckState.Open, e2.Get("a")!.State);
        var id = e2.Process(Err("b")).Single(x => x.NewIncident).IncidentId;
        Assert.Equal("INC-20261102-002", id);
    }

    [Fact]
    public void Catalog_RejectsCyclesAndUnknownParents()
    {
        Assert.Throws<InvalidOperationException>(() => Catalog(("a", Def(parents: ["b"])), ("b", Def(parents: ["a"]))));
        Assert.Throws<InvalidOperationException>(() => Catalog(("a", Def(parents: ["zzz"]))));
    }
}
