namespace Kibo.TestingFramework.Builders;

/// <summary>Generators for realistic, valid random test values.</summary>
public static class TestData
{
    /// <summary>Domain used for generated e-mail addresses (reserved, never routable).</summary>
    public const string EmailDomain = "kibo-test.example";

    /// <summary>A unique-looking customer e-mail, e.g. <c>customer-3f9a1c2e@kibo-test.example</c>.</summary>
    /// <param name="random">Random source.</param>
    public static string Email(Random random) => $"customer-{Hex(random, 8)}@{EmailDomain}";

    /// <summary>A product code, e.g. <c>SKU-04217</c>.</summary>
    /// <param name="random">Random source.</param>
    public static string ProductCode(Random random) => $"SKU-{random.Next(0, 100_000):D5}";

    /// <summary>A quantity between 1 and 10.</summary>
    /// <param name="random">Random source.</param>
    public static int Quantity(Random random) => random.Next(1, 11);

    /// <summary>A unit price between 1.00 and 500.00 with two decimal places.</summary>
    /// <param name="random">Random source.</param>
    public static decimal UnitPrice(Random random) => random.Next(100, 50_001) / 100m;

    private static string Hex(Random random, int length)
    {
        Span<byte> bytes = stackalloc byte[(length + 1) / 2];
        random.NextBytes(bytes);
        return Convert.ToHexStringLower(bytes)[..length];
    }
}
