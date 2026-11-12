namespace Kibo.TestingFramework.Diagnostics;

/// <summary>
/// Per-client diagnostics settings, attached to every outgoing request (via
/// <see cref="HttpRequestMessage.Options"/>) so the shared handler pipeline can honor them.
/// </summary>
public sealed record RequestDiagnostics
{
    /// <summary>Key under which these settings travel on <see cref="HttpRequestMessage.Options"/>.</summary>
    public static readonly HttpRequestOptionsKey<RequestDiagnostics> OptionKey = new("Kibo.RequestDiagnostics");

    /// <summary>No overrides: fresh correlation id per call, handler-level logging defaults.</summary>
    public static readonly RequestDiagnostics None = new();

    /// <summary>
    /// Correlation id sent on every request from the client (typically one per test).
    /// <c>null</c> generates a new id per call.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>Destination for log output (e.g. xUnit's <c>ITestOutputHelper.WriteLine</c>). <c>null</c> uses the handler default.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>Forces logging on or off for this client. <c>null</c> uses the handler default (see <c>KIBO_HTTP_LOGGING</c>).</summary>
    public bool? LoggingEnabled { get; init; }
}
