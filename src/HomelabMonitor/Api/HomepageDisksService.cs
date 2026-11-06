using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace HomelabMonitor.Api;

/// <summary>Snapshot keyed by drive letter. <c>Health</c> belongs to the physical disk, so it is the same on all of its volumes.</summary>
public sealed record HomepageDisks(
    string HealthSource,
    IReadOnlyDictionary<string, HomepageDrive> Drives,
    DateTime UpdatedAt);

/// <summary>Always filled strings: Homepage would render a null in a numeric field as NaN.</summary>
public sealed record HomepageDrive(
    string Used,
    string Free,
    string Model,
    string Health,
    string Performance,
    string RemainingLifetime,
    string PowerOnTime,
    string Temperature);

public sealed record DriveSpace(string Letter, long TotalBytes, long FreeBytes);

/// <summary>
/// Read-only snapshot for the disk cards. Space usage is live system data; health comes from the Hard Disk Sentinel
/// WebStatus <c>/xml</c> endpoint, so an HDS outage only affects the health fields. HDS builds the report on request,
/// so a successful response also shows that HDS is running. Serial numbers and other identifiers are never returned.
/// </summary>
public sealed class HomepageDisksService(
    HttpClient client,
    HomepageOptions options,
    Func<IReadOnlyList<DriveSpace>> readDrives,
    TimeProvider timeProvider,
    ILogger<HomepageDisksService> logger)
{
    private const string SOURCE_OK = "ok";
    private const string SOURCE_UNAVAILABLE = "unavailable";
    private const string EMPTY_TEXT = "–";
    private const string DISK_ELEMENT_PREFIX = "Physical_Disk_Information_Disk_";
    private const double BYTES_PER_GB = 1024d * 1024 * 1024;

    private static readonly TimeSpan _cacheDuration = TimeSpan.FromSeconds(30);

    private readonly CultureInfo _culture = CultureInfo.GetCultureInfo(options.Culture);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private (HomepageDisks Disks, DateTimeOffset CreatedAt)? _cached;

    /// <summary>The HDS XML is ISO-8859-2 encoded, which .NET does not know without the code pages provider.</summary>
    static HomepageDisksService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public async Task<HomepageDisks> GetAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            var now = timeProvider.GetUtcNow();
            if (_cached is { } cached && now - cached.CreatedAt < _cacheDuration)
            {
                return cached.Disks;
            }

            var disks = await BuildAsync(now, cancellationToken);
            _cached = (disks, now);
            return disks;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public static IReadOnlyList<DriveSpace> ReadLocalDrives()
    {
        return DriveInfo.GetDrives()
            .Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady)
            .Select(d => new DriveSpace(d.Name[..1], d.TotalSize, d.AvailableFreeSpace))
            .ToList();
    }

    private async Task<HomepageDisks> BuildAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var health = await ReadHealthAsync(cancellationToken);
        var drives = readDrives()
            .Where(d => d.TotalBytes > 0)
            .ToDictionary(d => d.Letter, d => BuildDrive(d, health));

        return new HomepageDisks(health is null ? SOURCE_UNAVAILABLE : SOURCE_OK, drives, now.UtcDateTime);
    }

    private HomepageDrive BuildDrive(DriveSpace drive, Dictionary<string, DiskHealth>? health)
    {
        var used = drive.TotalBytes - drive.FreeBytes;
        var freePercent = (int)Math.Round(100d * drive.FreeBytes / drive.TotalBytes);
        var disk = health?.GetValueOrDefault(drive.Letter);

        return new HomepageDrive(
            $"{FormatSize(used)} / {FormatSize(drive.TotalBytes)}",
            $"{FormatSize(drive.FreeBytes)} ({freePercent}%)",
            disk?.Model ?? EMPTY_TEXT,
            health is null ? options.NoDataText : disk?.Health ?? EMPTY_TEXT,
            disk?.Performance ?? EMPTY_TEXT,
            disk?.RemainingLifetime ?? EMPTY_TEXT,
            disk?.PowerOnTime ?? EMPTY_TEXT,
            disk?.Temperature ?? EMPTY_TEXT);
    }

    private async Task<Dictionary<string, DiskHealth>?> ReadHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            // The charset in the Content-Type header is wrong (us-ascii), so read bytes: the XML declaration is authoritative.
            await using var stream = await client.GetStreamAsync(options.HdsXmlUrl, cancellationToken);
            return await ParseDisksAsync(stream, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or XmlException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Hard Disk Sentinel WebStatus is not reachable.");
            return null;
        }
    }

    private static async Task<Dictionary<string, DiskHealth>> ParseDisksAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit });
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);

        var disks = new Dictionary<string, DiskHealth>();
        var summaries = document.Root?.Elements()
            .Where(e => e.Name.LocalName.StartsWith(DISK_ELEMENT_PREFIX, StringComparison.Ordinal))
            .Select(e => e.Element("Hard_Disk_Summary"))
            .OfType<XElement>() ?? [];

        foreach (var summary in summaries)
        {
            var disk = new DiskHealth(
                Text(summary, "Hard_Disk_Model_ID"),
                Text(summary, "Health").Replace(" ", ""),
                Text(summary, "Performance").Replace(" ", ""),
                Text(summary, "Estimated_remaining_lifetime"),
                Text(summary, "Power_on_time"),
                Text(summary, "Current_Temperature"));

            // Example: "C: [] D: [] ", where the brackets hold the volume label.
            foreach (Match letter in Regex.Matches(Text(summary, "Logical_Drive_s"), @"\b([A-Z]):"))
            {
                disks[letter.Groups[1].Value] = disk;
            }
        }

        return disks;
    }

    private static string Text(XElement parent, string name)
    {
        var value = parent.Element(name)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? EMPTY_TEXT : value;
    }

    private string FormatSize(long bytes)
    {
        var gigabytes = bytes / BYTES_PER_GB;
        return gigabytes >= 1000
            ? string.Create(_culture, $"{gigabytes / 1024:0.##} TB")
            : string.Create(_culture, $"{gigabytes:0} GB");
    }

    private sealed record DiskHealth(string Model, string Health, string Performance, string RemainingLifetime, string PowerOnTime, string Temperature);
}
