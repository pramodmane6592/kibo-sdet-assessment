using Kibo.TestingFramework;

namespace Kibo.LegacyTests;

/// <summary>
/// Shares one <see cref="KiboApiSession"/> (and therefore one HttpClient) across every test class
/// in the collection; xUnit disposes it after the last test runs.
/// </summary>
[CollectionDefinition(Name)]
public sealed class KiboApiCollection : ICollectionFixture<KiboApiSession>
{
    /// <summary>Collection name used in <c>[Collection]</c> attributes.</summary>
    public const string Name = "Kibo API";
}
