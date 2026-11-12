using Kibo.TestingFramework.Clients;
using Kibo.TestingFramework.Configuration;
using Kibo.TestingFramework.Diagnostics;

namespace Kibo.TestingFramework;

/// <summary>
/// Owns the single <see cref="HttpClient"/> shared by every API client in a test run, and hands
/// out typed clients configured from <see cref="KiboApiSettings"/>.
/// </summary>
/// <remarks>
/// <para>
/// Designed to be used as an xUnit class/collection fixture: created once, disposed at the end
/// of the run. Reusing one <see cref="HttpClient"/> avoids socket exhaustion, and
/// <see cref="SocketsHttpHandler.PooledConnectionLifetime"/> keeps DNS changes from going stale.
/// </para>
/// <para>
/// Handler pipeline (outermost first):
/// <see cref="CorrelationHandler"/> → <see cref="DiagnosticsHandler"/> → <see cref="SocketsHttpHandler"/>.
/// Correlation runs first so the captured exchange includes the tracing headers.
/// </para>
/// </remarks>
public sealed class KiboApiSession : IDisposable
{
    private readonly HttpClient _httpClient;

    /// <summary>Creates a session configured from environment variables (see <see cref="KiboApiSettings.FromEnvironment()"/>).</summary>
    /// <remarks>Single public constructor so xUnit can create it as a fixture.</remarks>
    public KiboApiSession()
        : this(KiboApiSettings.FromEnvironment())
    {
    }

    private KiboApiSession(KiboApiSettings settings)
    {
        Settings = settings;
        _httpClient = CreateHttpClient(settings);
        Orders = new OrdersClient(_httpClient, settings.TenantId);
    }

    /// <summary>Settings this session was created with.</summary>
    public KiboApiSettings Settings { get; }

    /// <summary>Orders client using the configured default tenant (new correlation id per call, no log output).</summary>
    public OrdersClient Orders { get; }

    /// <summary>Creates a session with explicit settings (e.g. to target another environment).</summary>
    /// <param name="settings">Connection settings.</param>
    public static KiboApiSession Create(KiboApiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new KiboApiSession(settings);
    }

    /// <summary>
    /// Creates an orders client for one test: every request carries the same new correlation id,
    /// and logs (when enabled) go to <paramref name="log"/> — typically <c>ITestOutputHelper.WriteLine</c>.
    /// </summary>
    /// <param name="log">Per-test log destination.</param>
    public OrdersClient CreateOrdersClient(Action<string>? log = null)
    {
        var client = Orders.WithCorrelationId(CorrelationHandler.NewId());
        return log is null ? client : client.WithLogOutput(log);
    }

    /// <inheritdoc />
    public void Dispose() => _httpClient.Dispose();

    /// <summary>Builds the HTTP pipeline used by sessions; exposed for custom hosts and tests.</summary>
    /// <param name="settings">Connection settings.</param>
    /// <param name="transport">Innermost handler; a pooled <see cref="SocketsHttpHandler"/> when <c>null</c>.</param>
    public static HttpClient CreateHttpClient(KiboApiSettings settings, HttpMessageHandler? transport = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var pipeline = new CorrelationHandler
        {
            InnerHandler = new DiagnosticsHandler(settings.HttpLoggingEnabled)
            {
                InnerHandler = transport ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) },
            },
        };

        return new HttpClient(pipeline, disposeHandler: true)
        {
            BaseAddress = EnsureTrailingSlash(settings.BaseUrl),
            Timeout = settings.Timeout,
        };
    }

    // Without a trailing slash, relative paths replace the last segment of a base path
    // (http://host/api + v1/orders => http://host/v1/orders).
    private static Uri EnsureTrailingSlash(Uri uri) =>
        uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
}
