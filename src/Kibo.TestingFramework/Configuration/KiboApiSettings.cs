using System.Globalization;

namespace Kibo.TestingFramework.Configuration;

/// <summary>
/// Connection settings for the Kibo API under test.
/// Defaults target a local Mock API; every value can be overridden via environment variables
/// (for CI) or directly with an object initializer (for one-off tests).
/// </summary>
public sealed record KiboApiSettings
{
    /// <summary>Environment variable that overrides <see cref="BaseUrl"/>.</summary>
    public const string BaseUrlVariable = "KIBO_API_BASE_URL";

    /// <summary>Environment variable that overrides <see cref="TenantId"/>.</summary>
    public const string TenantIdVariable = "KIBO_TENANT_ID";

    /// <summary>Environment variable that overrides <see cref="Timeout"/> (whole or fractional seconds).</summary>
    public const string TimeoutSecondsVariable = "KIBO_API_TIMEOUT_SECONDS";

    /// <summary>Environment variable that enables HTTP request/response logging (<c>true</c>/<c>1</c>/<c>yes</c>).</summary>
    public const string HttpLoggingVariable = "KIBO_HTTP_LOGGING";

    /// <summary>Default tenant used when none is configured.</summary>
    public const string DefaultTenantId = "tenant-abc-123";

    /// <summary>Default base URL of the local Mock API.</summary>
    public static readonly Uri DefaultBaseUrl = new("http://localhost:5000/");

    /// <summary>Default per-request HTTP timeout.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Root URL of the API (e.g. <c>http://localhost:5000/</c>).</summary>
    public Uri BaseUrl { get; init; } = DefaultBaseUrl;

    /// <summary>Tenant sent in the <c>x-kibo-tenant</c> header. <c>null</c> sends no header.</summary>
    public string? TenantId { get; init; } = DefaultTenantId;

    /// <summary>Per-request HTTP timeout.</summary>
    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    /// <summary>
    /// Whether full request/response logs are written for every call. Off by default to keep test
    /// output clean; timing and capture for failure messages are always on regardless.
    /// </summary>
    public bool HttpLoggingEnabled { get; init; }

    /// <summary>Builds settings from process environment variables, falling back to defaults.</summary>
    public static KiboApiSettings FromEnvironment() => FromEnvironment(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Builds settings from an arbitrary variable source (useful for unit-testing configuration).
    /// </summary>
    /// <param name="getVariable">Returns the value of a variable, or <c>null</c> when it is not set.</param>
    /// <exception cref="InvalidOperationException">A variable is set but has an invalid value.</exception>
    public static KiboApiSettings FromEnvironment(Func<string, string?> getVariable)
    {
        ArgumentNullException.ThrowIfNull(getVariable);

        var settings = new KiboApiSettings();

        var baseUrl = getVariable(BaseUrlVariable);
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            {
                throw new InvalidOperationException(
                    $"{BaseUrlVariable} must be an absolute URL but was '{baseUrl}'.");
            }

            settings = settings with { BaseUrl = uri };
        }

        var tenantId = getVariable(TenantIdVariable);
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            settings = settings with { TenantId = tenantId };
        }

        var timeout = getVariable(TimeoutSecondsVariable);
        if (!string.IsNullOrWhiteSpace(timeout))
        {
            if (!double.TryParse(timeout, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                || seconds <= 0)
            {
                throw new InvalidOperationException(
                    $"{TimeoutSecondsVariable} must be a positive number of seconds but was '{timeout}'.");
            }

            settings = settings with { Timeout = TimeSpan.FromSeconds(seconds) };
        }

        var logging = getVariable(HttpLoggingVariable);
        if (!string.IsNullOrWhiteSpace(logging))
        {
            settings = settings with
            {
                HttpLoggingEnabled = logging.Trim().ToLowerInvariant() switch
                {
                    "true" or "1" or "yes" or "on" => true,
                    "false" or "0" or "no" or "off" => false,
                    _ => throw new InvalidOperationException(
                        $"{HttpLoggingVariable} must be true/false (or 1/0, yes/no, on/off) but was '{logging}'."),
                },
            };
        }

        return settings;
    }
}
