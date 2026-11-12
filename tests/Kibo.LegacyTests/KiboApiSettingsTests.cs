using Kibo.TestingFramework.Configuration;

namespace Kibo.LegacyTests;

/// <summary>Unit tests for configuration loading — no running API required.</summary>
public sealed class KiboApiSettingsTests
{
    [Fact]
    public void FromEnvironment_WithNoVariables_UsesDefaults()
    {
        var settings = KiboApiSettings.FromEnvironment(_ => null);

        Assert.Equal(KiboApiSettings.DefaultBaseUrl, settings.BaseUrl);
        Assert.Equal(KiboApiSettings.DefaultTenantId, settings.TenantId);
        Assert.Equal(KiboApiSettings.DefaultTimeout, settings.Timeout);
    }

    [Fact]
    public void FromEnvironment_WithVariables_OverridesDefaults()
    {
        var variables = new Dictionary<string, string>
        {
            [KiboApiSettings.BaseUrlVariable] = "https://qa.kibo.example/api",
            [KiboApiSettings.TenantIdVariable] = "tenant-ci",
            [KiboApiSettings.TimeoutSecondsVariable] = "2.5",
        };

        var settings = KiboApiSettings.FromEnvironment(variables.GetValueOrDefault);

        Assert.Equal(new Uri("https://qa.kibo.example/api"), settings.BaseUrl);
        Assert.Equal("tenant-ci", settings.TenantId);
        Assert.Equal(TimeSpan.FromSeconds(2.5), settings.Timeout);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData(" YES ", true)]
    [InlineData("off", false)]
    public void FromEnvironment_HttpLogging_IsOffByDefaultAndParsesToggles(string? value, bool expected)
    {
        var settings = KiboApiSettings.FromEnvironment(name => name == KiboApiSettings.HttpLoggingVariable ? value : null);

        Assert.Equal(expected, settings.HttpLoggingEnabled);
    }

    [Theory]
    [InlineData(KiboApiSettings.HttpLoggingVariable, "maybe")]
    [InlineData(KiboApiSettings.BaseUrlVariable, "not-a-url")]
    [InlineData(KiboApiSettings.TimeoutSecondsVariable, "abc")]
    [InlineData(KiboApiSettings.TimeoutSecondsVariable, "-1")]
    public void FromEnvironment_WithInvalidValue_ThrowsNamingTheVariable(string variable, string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => KiboApiSettings.FromEnvironment(name => name == variable ? value : null));

        Assert.Contains(variable, ex.Message);
    }
}
