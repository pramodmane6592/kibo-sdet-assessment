namespace Kibo.TestingFramework.Diagnostics;

/// <summary>
/// Stamps every request with tracing headers:
/// <list type="bullet">
/// <item><see cref="CorrelationIdHeader"/> — shared by all calls of one test (from <see cref="RequestDiagnostics.CorrelationId"/>),
/// or unique per call when the client has none.</item>
/// <item><see cref="RequestIdHeader"/> — always unique per call.</item>
/// </list>
/// Headers already present on the request are left untouched.
/// </summary>
public sealed class CorrelationHandler : DelegatingHandler
{
    /// <summary>Header that groups all requests made by one test.</summary>
    public const string CorrelationIdHeader = "x-correlation-id";

    /// <summary>Header that identifies one individual request.</summary>
    public const string RequestIdHeader = "x-request-id";

    /// <summary>Generates a compact, unique identifier.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Headers.Contains(CorrelationIdHeader))
        {
            request.Options.TryGetValue(RequestDiagnostics.OptionKey, out var diagnostics);
            request.Headers.TryAddWithoutValidation(CorrelationIdHeader, diagnostics?.CorrelationId ?? NewId());
        }

        if (!request.Headers.Contains(RequestIdHeader))
        {
            request.Headers.TryAddWithoutValidation(RequestIdHeader, NewId());
        }

        return base.SendAsync(request, cancellationToken);
    }
}
