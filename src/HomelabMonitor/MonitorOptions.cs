namespace HomelabMonitor;

public sealed class MonitorOptions
{
    public string DataDir { get; set; } = @"C:\ProgramData\HomelabMonitor";
    public bool RunBackground { get; set; } = true;
    public string TimeZone { get; set; } = "UTC";
    public int CycleSeconds { get; set; } = 60;
    public HeavyOptions Heavy { get; set; } = new();
    public MaintenanceOptions Maintenance { get; set; } = new();
    public ReportOptions Reports { get; set; } = new();

    public string DbPath => Path.Combine(DataDir, "homelab-monitor.db");
    public string MuteFilePath => string.IsNullOrWhiteSpace(Maintenance.MuteFile) ? Path.Combine(DataDir, "mute.json") : Maintenance.MuteFile;
}

public sealed class HeavyOptions
{
    /// <summary>Folder of the heavy-*.json result files written by the external (scheduled) heavy checks.</summary>
    public string Directory { get; set; } = @"C:\ProgramData\HomelabMonitor\heavy";
    public int StaleHours { get; set; } = 7;
    public int MaxAgeHours { get; set; } = 12;
}

public sealed class MaintenanceWindowOptions
{
    public DayOfWeek Day { get; set; }
    public string Start { get; set; } = "00:00";
    public string End { get; set; } = "00:00";
}

public sealed class MaintenanceOptions
{
    public int PostBootMinutes { get; set; } = 10;
    public string MuteFile { get; set; } = "";
    public List<MaintenanceWindowOptions> Windows { get; set; } = [];
}

/// <summary>A notification rule, referenced by <c>CheckDefinition.NotifyRule</c>.</summary>
public sealed class NotifyRuleOptions
{
    /// <summary>Message body; <c>{names}</c> is replaced with the display names of the affected checks.</summary>
    public string Body { get; set; } = "{names}";

    /// <summary>One reminder after this many hours (ignored when <see cref="Daily"/> is set).</summary>
    public double? ReminderAfterHours { get; set; }

    /// <summary>A reminder every 24 hours while the incident is open.</summary>
    public bool Daily { get; set; }

    /// <summary>Append the time since the incident opened to the first message.</summary>
    public bool WithDuration { get; set; }
}

public sealed class NotifyOptions
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "https://ntfy.sh";
    public string TopicFile { get; set; } = "";

    /// <summary>Prefix of every notification text.</summary>
    public string Prefix { get; set; } = "[Homelab]";

    public Dictionary<string, NotifyRuleOptions> Rules { get; set; } = new(StringComparer.Ordinal);
    public int RatePerHour { get; set; } = 6;
    public int StormCount { get; set; } = 3;
    public int StormWindowMinutes { get; set; } = 10;
    public int MaxAgeMinutes { get; set; } = 120;
}

public sealed class HeartbeatOptions
{
    public bool Enabled { get; set; }
    public string UrlFile { get; set; } = "";
    public int IntervalMinutes { get; set; } = 5;
    public int StuckOutboxMinutes { get; set; } = 30;
}

/// <summary>A check whose uptime is reported. <see cref="Key"/> is the JSON key in the summary API (no dots: Homepage uses dotted paths).</summary>
public sealed class UptimeEntry
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Check { get; set; } = "";
}

/// <summary>A check listed in the report by its name (backups) or by its last numeric value (metrics).</summary>
public sealed class ReportCheckEntry
{
    public string Name { get; set; } = "";
    public string Check { get; set; } = "";
}

public sealed class ReportOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Local time (Monitor:TimeZone) of the daily and weekly report.</summary>
    public string At { get; set; } = "07:00";

    public DayOfWeek WeeklyDay { get; set; } = DayOfWeek.Monday;
    public int KeepWeeklyFiles { get; set; } = 26;

    // Empty defaults on purpose: the configuration binder APPENDS to existing list items (C,D,E showed up twice).
    public string[] Disks { get; set; } = [];
    public int DiskSampleMinutes { get; set; } = 60;
    public List<UptimeEntry> Uptime { get; set; } = [];
    public List<ReportCheckEntry> Backups { get; set; } = [];

    /// <summary>Light checks whose last numeric value is shown in the report (e.g. failed jobs in 24 h).</summary>
    public List<ReportCheckEntry> Values { get; set; } = [];
}
