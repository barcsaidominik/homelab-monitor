namespace HomelabMonitor.Domain;

public enum CheckStatus { Ok, Warn, Error, Unknown }

public enum Severity { Info, Warn, Crit }

public enum CheckSource { Light, Heavy }

/// <summary>States of the incident state machine. RESOLVED is transient: after the event the state is OK.</summary>
public enum CheckState { Ok, Pending, Open, Recovering, Resolved, Suppressed, Flapping }

/// <summary>A check result. Message is at most 300 characters and must not contain secrets.</summary>
public sealed record CheckResult(
    string CheckId,
    CheckStatus Status,
    DateTimeOffset ObservedAt,
    string Message = "",
    string? Target = null,
    long DurationMs = 0,
    double? Value = null,
    double? Threshold = null,
    CheckSource Source = CheckSource.Light,
    string? RunId = null)
{
    public const int MaxMessageLength = 300;

    public string Key => Target is null ? CheckId : $"{CheckId}|{Target}";

    public static string Trim(string? message) =>
        string.IsNullOrEmpty(message) ? "" : message.Length <= MaxMessageLength ? message : message[..MaxMessageLength];
}

public static class CheckStatusParser
{
    public static CheckStatus Parse(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "OK" => CheckStatus.Ok,
        "WARN" or "WARNING" => CheckStatus.Warn,
        "ERROR" or "ERR" => CheckStatus.Error,
        _ => CheckStatus.Unknown,
    };

    public static string ToText(this CheckStatus status) => status switch
    {
        CheckStatus.Ok => "OK",
        CheckStatus.Warn => "WARN",
        CheckStatus.Error => "ERROR",
        _ => "UNKNOWN",
    };
}
