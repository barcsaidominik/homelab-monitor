using System.Net;
using System.Text;
using System.Text.Json;
using HomelabMonitor.Api;
using HomelabMonitor.Probes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HomelabMonitor.Tests;

public sealed class HomepageHardwareServiceTests
{
    private const string Lhm = """
        {"id":0,"Text":"Sensor","Value":"","Children":[
          {"Text":"HOST","Value":"","Children":[
            {"Text":"CPU","Value":"","Children":[
              {"Text":"Temperatures","Value":"","Children":[
                {"Text":"Core (Tctl/Tdie)","Value":"88,6 °C","SensorId":"/amdcpu/0/temperature/2","Children":[]}]},
              {"Text":"Load","Value":"","Children":[
                {"Text":"CPU Total","Value":"63,4 %","SensorId":"/amdcpu/0/load/0","Children":[]}]},
              {"Text":"Powers","Value":"","Children":[
                {"Text":"Package","Value":"44,51 W","SensorId":"/amdcpu/0/power/0","Children":[]}]}]},
            {"Text":"Memory","Value":"","Children":[
              {"Text":"Memory Used","Value":"28,3 GB","SensorId":"/ram/data/0","Children":[]}]}]}]}
        """;

    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static HomepageHardwareService Service(Func<HttpResponseMessage> lhm, string? fan, ManualTime? t = null) =>
        new(new HttpClient(new FakeHandler(_ => lhm())), new HomepageOptions(), new FanStateProbeOptions(), () => fan, t ?? new ManualTime(Now),
            NullLogger<HomepageHardwareService>.Instance);

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Fan(DateTimeOffset at, string mode = "max") =>
        $"﻿{{\"At\":\"{at.UtcDateTime:o}\",\"Mode\":\"{mode}\",\"CpuC\":88.6,\"Fan1Rpm\":5800,\"Fan2Rpm\":6100}}";

    [Fact]
    public async Task MapsSensorsAndFan()
    {
        var h = await Service(() => Json(Lhm), Fan(Now.AddSeconds(-5))).GetAsync(default);
        Assert.Equal(88.6, h.CpuTemp);
        Assert.Equal(63.4, h.CpuLoad);
        Assert.Equal(44.5, h.CpuPower);
        Assert.Equal(28.3, h.RamUsed);
        Assert.Equal(0, h.GpuTemp); // missing sensor: 0, never null
        Assert.Equal((5800, 6100, "Max"), (h.Fan1, h.Fan2, h.FanMode));
        Assert.Equal("ok", h.LhmSource);
    }

    [Fact]
    public async Task LhmDown_StillReturnsFan()
    {
        var h = await Service(() => throw new HttpRequestException("refused"), Fan(Now, "auto")).GetAsync(default);
        Assert.Equal("unavailable", h.LhmSource);
        Assert.Equal(0, h.CpuTemp);
        Assert.Equal("Auto", h.FanMode);
    }

    [Fact]
    public async Task StaleOrMissingFanState_IsNoData()
    {
        Assert.Equal("No data", (await Service(() => Json(Lhm), null).GetAsync(default)).FanMode);
        var stale = await Service(() => Json(Lhm), Fan(Now.AddMinutes(-4))).GetAsync(default);
        Assert.Equal((0, 0, "No data"), (stale.Fan1, stale.Fan2, stale.FanMode));
    }

    [Fact]
    public async Task FanModeLabels_ComeFromConfiguration()
    {
        var options = new HomepageOptions { FanModeLabels = new() { ["auto"] = "Automatikus" } };
        var s = new HomepageHardwareService(new HttpClient(new FakeHandler(_ => Json(Lhm))), options, new FanStateProbeOptions(),
            () => Fan(Now, "auto"), new ManualTime(Now), NullLogger<HomepageHardwareService>.Instance);
        Assert.Equal("Automatikus", (await s.GetAsync(default)).FanMode);
    }

    [Fact]
    public async Task CachesForTenSeconds()
    {
        var calls = 0;
        var t = new ManualTime(Now);
        var s = Service(() => { calls++; return Json(Lhm); }, null, t);
        var first = await s.GetAsync(default);
        t.Advance(TimeSpan.FromSeconds(9));
        Assert.Same(first, await s.GetAsync(default));
        t.Advance(TimeSpan.FromSeconds(1));
        await s.GetAsync(default);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void JsonShape_IsCamelCaseNumbers()
    {
        var h = new HomepageHardwareService(new HttpClient(), new HomepageOptions(), new FanStateProbeOptions(), () => null, new ManualTime(Now), NullLogger<HomepageHardwareService>.Instance)
            .Build(new Dictionary<string, double> { ["/amdcpu/0/temperature/2"] = 60 }, null, Now);
        var json = JsonSerializer.Serialize(h, JsonSerializerOptions.Web);
        Assert.Contains("\"cpuTemp\":60", json);
        Assert.Contains("\"fanMode\":\"No data\"", json);
        Assert.DoesNotContain("null", json);
    }
}
