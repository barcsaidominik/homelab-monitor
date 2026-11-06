using HomelabMonitor.Domain;
using HomelabMonitor.Probes;
using Xunit;

namespace HomelabMonitor.Tests;

public sealed class FanStateProbeTests
{
    private static FanStateProbe Probe(ManualTime t) => new(new FanStateProbeOptions { FanCheckId = "thermal.fan" }, () => null, t);

    private static string State(DateTimeOffset at, string mode = "auto", double? cpu = 60, int f1 = 2300, int f2 = 2600) =>
        $"{{\"At\":\"{at:o}\",\"Mode\":\"{mode}\",\"CpuC\":{(cpu is null ? "null" : cpu.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))},\"Fan1Rpm\":{f1},\"Fan2Rpm\":{f2},\"OverrideUntil\":null}}";

    [Fact]
    public void FreshState_IsOk_WithCpuValue()
    {
        var t = new ManualTime(T.Start);
        var r = Probe(t).Evaluate("﻿" + State(T.Start.AddSeconds(-10)));
        Assert.Equal(CheckStatus.Ok, r.Single(x => x.CheckId == "thermal.fan").Status);
        Assert.Equal(2600, r.Single(x => x.CheckId == "thermal.fan").Value);
        var cpu = r.Single(x => x.CheckId == "thermal.cpu");
        Assert.Equal(CheckStatus.Ok, cpu.Status);
        Assert.Equal(60, cpu.Value);
    }

    [Fact]
    public void HotCpu_IsWarn()
    {
        var t = new ManualTime(T.Start);
        var cpu = Probe(t).Evaluate(State(T.Start, "max", 96.5, 5800, 6100)).Single(x => x.CheckId == "thermal.cpu");
        Assert.Equal(CheckStatus.Warn, cpu.Status);
        Assert.Contains("max", cpu.Message);
    }

    [Fact]
    public void MissingOrStaleState_WarnsGuardOnly()
    {
        var t = new ManualTime(T.Start);
        var missing = Probe(t).Evaluate(null);
        Assert.Equal("thermal.fan", Assert.Single(missing).CheckId);
        Assert.Equal(CheckStatus.Warn, missing[0].Status);

        var stale = Probe(t).Evaluate(State(T.Start.AddMinutes(-4)));
        Assert.Equal(CheckStatus.Warn, Assert.Single(stale).Status);
    }

    [Fact]
    public void StoppedFans_WarnOnlyWhenWarm()
    {
        var t = new ManualTime(T.Start);
        Assert.Equal(CheckStatus.Ok, Probe(t).Evaluate(State(T.Start, cpu: 50, f1: 0, f2: 0)).Single(x => x.CheckId == "thermal.fan").Status);
        Assert.Equal(CheckStatus.Warn, Probe(t).Evaluate(State(T.Start, cpu: 80, f1: 0, f2: 0)).Single(x => x.CheckId == "thermal.fan").Status);
    }

    [Fact]
    public void StoppedFans_InMaxMode_WarnEvenWhenCool()
    {
        var t = new ManualTime(T.Start);
        Assert.Equal(CheckStatus.Warn, Probe(t).Evaluate(State(T.Start, "max", cpu: 50, f1: 0, f2: 0)).Single(x => x.CheckId == "thermal.fan").Status);
    }

    [Fact]
    public void StoppedFans_AfterFailedRecoveries_IsError()
    {
        var t = new ManualTime(T.Start);
        string WithTries(int n, int rpm) => State(T.Start, "max", 80, rpm, rpm).TrimEnd('}') + $",\"StallRecoveryTries\":{n}}}";
        Assert.Equal(CheckStatus.Warn, Probe(t).Evaluate(WithTries(2, 0)).Single(x => x.CheckId == "thermal.fan").Status);
        var err = Probe(t).Evaluate(WithTries(3, 0)).Single(x => x.CheckId == "thermal.fan");
        Assert.Equal(CheckStatus.Error, err.Status);
        Assert.Contains("3 recovery attempts", err.Message);
        Assert.Equal(CheckStatus.Ok, Probe(t).Evaluate(WithTries(3, 5000)).Single(x => x.CheckId == "thermal.fan").Status);
    }

    [Fact]
    public void BadJsonOrMissingCpu_IsUnknown()
    {
        var t = new ManualTime(T.Start);
        Assert.Equal(CheckStatus.Unknown, Assert.Single(Probe(t).Evaluate("{not json")).Status);
        Assert.Equal(CheckStatus.Unknown, Assert.Single(Probe(t).Evaluate("{\"Mode\":\"auto\"}")).Status);
        Assert.Equal(CheckStatus.Unknown, Probe(t).Evaluate(State(T.Start, cpu: null)).Single(x => x.CheckId == "thermal.cpu").Status);
    }
}
