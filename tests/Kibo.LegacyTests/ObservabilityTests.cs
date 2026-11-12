using System.Net;
using Kibo.TestingFramework;
using Kibo.TestingFramework.Builders;
using Kibo.TestingFramework.Clients;
using Kibo.TestingFramework.Diagnostics;
using Kibo.TestingFramework.Http;
using Xunit.Abstractions;

namespace Kibo.LegacyTests;

/// <summary>
/// Demonstrates the framework's built-in observability against the running API:
/// timing assertions, correlation ids and self-diagnosing failures.
/// </summary>
[Collection(KiboApiCollection.Name)]
[Trait("Category", "Observability")]
public sealed class ObservabilityTests
{
    // Generous enough for a CI agent, tight enough to catch a real regression of an in-memory API.
    private static readonly TimeSpan CreateOrderBudget = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan GetOrderBudget = TimeSpan.FromMilliseconds(300);

    private readonly OrdersClient _orders;

    public ObservabilityTests(KiboApiSession session, ITestOutputHelper output)
    {
        _orders = session.CreateOrdersClient(output.WriteLine);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task CreateAndGetOrder_RespondWithinPerformanceBudget()
    {
        // Warm-up: the first call pays for connection setup and server-side JIT, which is not what we measure.
        await _orders.GetOrderAsync(Guid.NewGuid());

        var created = (await _orders.CreateOrderAsync(OrderBuilder.Default().WithItems(3).Build()))
            .EnsureStatus(HttpStatusCode.Created);
        var fetched = (await _orders.GetOrderAsync(created.EnsureData().Id))
            .EnsureStatus(HttpStatusCode.OK);

        Assert.True(created.Elapsed < CreateOrderBudget,
            $"POST /v1/orders took {created.ElapsedMs} ms (budget {CreateOrderBudget.TotalMilliseconds} ms).{Environment.NewLine}{created.Describe()}");
        Assert.True(fetched.Elapsed < GetOrderBudget,
            $"GET /v1/orders/{{id}} took {fetched.ElapsedMs} ms (budget {GetOrderBudget.TotalMilliseconds} ms).{Environment.NewLine}{fetched.Describe()}");
    }

    [Fact]
    public async Task CallsFromOneTest_ShareCorrelationId_ButHaveUniqueRequestIds()
    {
        var first = await _orders.CreateOrderAsync(OrderBuilder.Default().Build());
        var second = await _orders.GetOrderAsync(first.EnsureData().Id);

        Assert.NotNull(first.CorrelationId);
        Assert.Equal(first.CorrelationId, second.CorrelationId);
        Assert.NotEqual(first.Exchange!.RequestId, second.Exchange!.RequestId);
        Assert.Contains(first.Exchange.RequestHeaders, h => h.Key == CorrelationHandler.CorrelationIdHeader && h.Value == first.CorrelationId);
    }

    [Fact]
    public async Task UnexpectedStatus_FailsWithFullRequestAndResponse()
    {
        var request = OrderBuilder.Default().WithCustomerEmail("diagnose-me@kibo-test.example").Build();
        var response = await _orders.CreateOrderAsync(request);

        // Deliberately wrong expectation, to show what a CI failure message looks like.
        var ex = Assert.Throws<ApiResponseException>(() => response.EnsureStatus(HttpStatusCode.OK));

        Assert.Contains("Expected 200 OK but got 201 Created", ex.Message);
        Assert.Contains(response.CorrelationId!, ex.Message);           // traceable
        Assert.Contains("POST http://", ex.Message);                     // request line
        Assert.Contains("diagnose-me@kibo-test.example", ex.Message);    // request body
        Assert.Contains("\"status\":\"Pending\"", ex.Message);           // response body
        Assert.Contains(" ms)", ex.Message);                             // timing
    }

    [Fact]
    public async Task Logging_IsOffByDefault_AndEnabledPerClient()
    {
        var lines = new List<string>();
        var quiet = _orders.WithLogOutput(lines.Add).WithLogging(false);
        var verbose = _orders.WithLogOutput(lines.Add).WithLogging();

        await quiet.GetOrderAsync(Guid.NewGuid());
        Assert.Empty(lines);

        var response = await verbose.GetOrderAsync(Guid.NewGuid());
        var log = Assert.Single(lines);
        Assert.Contains("GET http://", log);
        Assert.Contains("404 NotFound", log);
        Assert.Contains(response.CorrelationId!, log);
    }

    [Fact]
    public async Task ResponseLogs_AreExposedForAdHocDebugging()
    {
        var response = await _orders.CreateOrderAsync(OrderBuilder.Default().Build());

        Assert.StartsWith("POST ", response.RequestLog);
        Assert.Contains("x-kibo-tenant", response.RequestLog);
        Assert.StartsWith("201 Created", response.ResponseLog);
        Assert.True(response.ElapsedMs >= 0);
    }
}
