namespace HomelabMonitor.Api;

public sealed record HomepageError(string Error);

/// <summary>Read-only Homepage data endpoints. Error details go to the log only, never into the response.</summary>
public static class HomepageEndpoints
{
    private const string UnavailableMessage = "The snapshot is not available right now.";

    public static void MapHomepageDataEndpoints(this WebApplication app)
    {
        app.MapGet("/api/homepage/disks", GetDisksAsync);
        app.MapGet("/api/homepage/hardware", GetHardwareAsync);
    }

    public static Task<IResult> GetDisksAsync(HomepageDisksService disksService, ILoggerFactory loggerFactory, HttpContext httpContext, CancellationToken cancellationToken) =>
        Run(() => disksService.GetAsync(cancellationToken), "Building the disk snapshot failed.", loggerFactory, httpContext, cancellationToken);

    public static Task<IResult> GetHardwareAsync(HomepageHardwareService hardwareService, ILoggerFactory loggerFactory, HttpContext httpContext, CancellationToken cancellationToken) =>
        Run(() => hardwareService.GetAsync(cancellationToken), "Building the hardware snapshot failed.", loggerFactory, httpContext, cancellationToken);

    private static async Task<IResult> Run<T>(Func<Task<T>> build, string logMessage, ILoggerFactory loggerFactory, HttpContext httpContext, CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        try
        {
            return Results.Json(await build());
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            loggerFactory.CreateLogger(nameof(HomepageEndpoints)).LogWarning(ex, "{Message}", logMessage);
            return Results.Json(new HomepageError(UnavailableMessage), statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
