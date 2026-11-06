using System.Net;
using System.Text;
using System.Text.Json;
using HomelabMonitor.Api;
using Xunit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomelabMonitor.Tests;

public class HomepageDisksServiceTests
{
    private const long GB = 1024L * 1024 * 1024;
    private const string SERIAL = "SERIAL-SECRET";

    // A Hungarian-localized HDS report: the accented texts check the ISO-8859-2 decoding.
    private static readonly string _sampleXml = $"""
        <?xml version="1.0" encoding="ISO-8859-2"?>
        <Hard_Disk_Sentinel>
          <General_Information><Computer_Information><Computer_Name>HOST</Computer_Name></Computer_Information></General_Information>
          <Physical_Disk_Information_Disk_0>
            <Hard_Disk_Summary>
              <Hard_Disk_Model_ID>NVME SSD 2TB</Hard_Disk_Model_ID>
              <Hard_Disk_Serial_Number>{SERIAL}</Hard_Disk_Serial_Number>
              <Logical_Drive_s>C: [] D: [Adat] </Logical_Drive_s>
              <Power_on_time>234 nap, 14 óra</Power_on_time>
              <Estimated_remaining_lifetime>több mint 1000 nap</Estimated_remaining_lifetime>
              <Current_Temperature>53 °C</Current_Temperature>
              <Health>96 %</Health>
              <Performance>100 %</Performance>
            </Hard_Disk_Summary>
          </Physical_Disk_Information_Disk_0>
          <Physical_Disk_Information_Disk_1>
            <Hard_Disk_Summary>
              <Hard_Disk_Model_ID>HDD 8TB</Hard_Disk_Model_ID>
              <Logical_Drive_s>E: [] </Logical_Drive_s>
              <Health>100 %</Health>
              <Performance>100 %</Performance>
            </Hard_Disk_Summary>
          </Physical_Disk_Information_Disk_1>
          <Partition_Information />
        </Hard_Disk_Sentinel>
        """;

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 1, 18, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }

        public void Advance(TimeSpan by)
        {
            _now += by;
        }
    }

    private sealed class FakeHdsHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls
        {
            get;
            private set;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond());
        }
    }

    private readonly ManualClock _clock = new();
    private HomepageOptions _options = new();
    private IReadOnlyList<DriveSpace> _drives =
    [
        new("C", 500 * GB, 300 * GB),
        new("D", 1400 * GB, 700 * GB),
        new("E", 8000 * GB, 2000 * GB)
    ];

    private HomepageDisksService CreateService(HttpMessageHandler handler, Func<IReadOnlyList<DriveSpace>>? readDrives = null)
    {
        return new HomepageDisksService(new HttpClient(handler, disposeHandler: false), _options, readDrives ?? (() => _drives), _clock, NullLogger<HomepageDisksService>.Instance);
    }

    private HomepageDisksService CreateService(string xml)
    {
        return CreateService(new FakeHdsHandler(() => XmlResponse(xml)));
    }

    /// <summary>The real HDS also sends a wrong charset in the header: only the XML declaration is right.</summary>
    private static HttpResponseMessage XmlResponse(string xml)
    {
        // The service's static constructor registers the code pages provider.
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(HomepageDisksService).TypeHandle);
        var content = new ByteArrayContent(Encoding.GetEncoding("iso-8859-2").GetBytes(xml));
        content.Headers.TryAddWithoutValidation("Content-Type", "text/xml; charset=us-ascii");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    [Fact]
    public async Task Get_MapsHealthToEveryVolumeOfTheDisk()
    {
        var result = await CreateService(_sampleXml).GetAsync(default);

        Assert.Equal("ok", result.HealthSource);
        Assert.Equal("96%", result.Drives["C"].Health);
        Assert.Equal("96%", result.Drives["D"].Health);
        Assert.Equal("100%", result.Drives["E"].Health);
        Assert.Equal("NVME SSD 2TB", result.Drives["D"].Model);
        Assert.Equal("100%", result.Drives["C"].Performance);
        Assert.Equal("53 °C", result.Drives["C"].Temperature);
        Assert.Equal("53 °C", result.Drives["D"].Temperature);
    }

    [Fact]
    public async Task Get_DecodesLatin2TextDespiteWrongHeaderCharset()
    {
        var drive = (await CreateService(_sampleXml).GetAsync(default)).Drives["C"];

        Assert.Equal("több mint 1000 nap", drive.RemainingLifetime);
        Assert.Equal("234 nap, 14 óra", drive.PowerOnTime);
    }

    [Fact]
    public async Task Get_MissingSummaryField_IsNeverEmpty()
    {
        var drive = (await CreateService(_sampleXml).GetAsync(default)).Drives["E"];

        Assert.Equal("–", drive.RemainingLifetime);
        Assert.Equal("–", drive.PowerOnTime);
        Assert.Equal("–", drive.Temperature);
    }

    [Fact]
    public async Task Get_FormatsSpaceInGbAndTb()
    {
        var drives = (await CreateService(_sampleXml).GetAsync(default)).Drives;

        Assert.Equal("200 GB / 500 GB", drives["C"].Used);
        Assert.Equal("300 GB (60%)", drives["C"].Free);
        Assert.Equal("5.86 TB / 7.81 TB", drives["E"].Used);
        Assert.Equal("1.95 TB (25%)", drives["E"].Free);
    }

    [Fact]
    public async Task Get_UsesTheConfiguredCultureAndNoDataText()
    {
        _options = new HomepageOptions { Culture = "hu-HU", NoDataText = "Nincs adat" };
        var service = CreateService(new FakeHdsHandler(() => throw new HttpRequestException("connection refused")));

        var drive = (await service.GetAsync(default)).Drives["E"];

        Assert.Equal("5,86 TB / 7,81 TB", drive.Used);
        Assert.Equal("Nincs adat", drive.Health);
    }

    [Fact]
    public async Task Get_HdsDown_KeepsLiveSpace()
    {
        var service = CreateService(new FakeHdsHandler(() => throw new HttpRequestException("connection refused")));

        var result = await service.GetAsync(default);

        Assert.Equal("unavailable", result.HealthSource);
        Assert.Equal("No data", result.Drives["E"].Health);
        Assert.Equal("–", result.Drives["E"].Model);
        Assert.Equal("–", result.Drives["E"].Temperature);
        Assert.Equal("1.95 TB (25%)", result.Drives["E"].Free);
    }

    [Fact]
    public async Task Get_HdsTimeout_KeepsLiveSpace()
    {
        var service = CreateService(new FakeHdsHandler(() => throw new TaskCanceledException("timeout")));

        var result = await service.GetAsync(default);

        Assert.Equal("unavailable", result.HealthSource);
        Assert.Equal("300 GB (60%)", result.Drives["C"].Free);
    }

    [Fact]
    public async Task Get_HdsErrorStatus_KeepsLiveSpace()
    {
        var service = CreateService(new FakeHdsHandler(() => new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        var result = await service.GetAsync(default);

        Assert.Equal("unavailable", result.HealthSource);
        Assert.Equal("300 GB (60%)", result.Drives["C"].Free);
    }

    [Fact]
    public async Task Get_TruncatedXml_KeepsLiveSpace()
    {
        var result = await CreateService(_sampleXml[..200]).GetAsync(default);

        Assert.Equal("unavailable", result.HealthSource);
        Assert.Equal("300 GB (60%)", result.Drives["C"].Free);
    }

    [Fact]
    public async Task Get_XmlWithDtd_IsRejected()
    {
        var xml = """<?xml version="1.0"?><!DOCTYPE a [<!ENTITY b "c">]><Hard_Disk_Sentinel>&b;</Hard_Disk_Sentinel>""";

        Assert.Equal("unavailable", (await CreateService(xml).GetAsync(default)).HealthSource);
    }

    [Fact]
    public async Task Get_VolumeOfUnknownDisk_HasPlaceholders()
    {
        _drives = [new("F", 100 * GB, 50 * GB)];

        var drive = (await CreateService(_sampleXml).GetAsync(default)).Drives["F"];

        Assert.Equal("–", drive.Health);
        Assert.Equal("–", drive.Model);
    }

    [Fact]
    public async Task Get_CachesForThirtySeconds_EvenWhenHdsIsDown()
    {
        var handler = new FakeHdsHandler(() => throw new HttpRequestException("connection refused"));
        var service = CreateService(handler);
        var first = await service.GetAsync(default);

        _clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Same(first, await service.GetAsync(default));
        Assert.Equal(1, handler.Calls);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await service.GetAsync(default);
        Assert.Equal(2, handler.Calls);
    }

    private static async Task<(int Status, string Body, string CacheControl)> InvokeEndpointAsync(HomepageDisksService service)
    {
        var body = new MemoryStream();
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
            Response = { Body = body }
        };

        var result = await HomepageEndpoints.GetDisksAsync(service, NullLoggerFactory.Instance, context, default);
        await result.ExecuteAsync(context);

        return (context.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()), context.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Endpoint_KeepsDriveLetterKeysAndHidesSerial()
    {
        var (status, body, cacheControl) = await InvokeEndpointAsync(CreateService(_sampleXml));

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal("no-store", cacheControl);
        var root = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ok", root.GetProperty("healthSource").GetString());
        Assert.Equal("96%", root.GetProperty("drives").GetProperty("C").GetProperty("health").GetString());
        Assert.DoesNotContain(SERIAL, body);
    }

    [Fact]
    public async Task Endpoint_DriveFailure_Returns503WithoutDetails()
    {
        var service = CreateService(
            new FakeHdsHandler(() => XmlResponse(_sampleXml)),
            () => throw new IOException(@"secret detail: D:\internal"));

        var (status, body, _) = await InvokeEndpointAsync(service);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status);
        Assert.DoesNotContain("secret", body);
    }
}
