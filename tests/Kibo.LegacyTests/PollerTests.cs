using System.Diagnostics;
using Kibo.TestingFramework.Polling;

namespace Kibo.LegacyTests;

/// <summary>Unit tests for <see cref="Poller"/> — no running API required.</summary>
public sealed class PollerTests
{
    private static readonly TimeSpan FastInterval = TimeSpan.FromMilliseconds(20);

    [Fact]
    public async Task WaitUntil_ConditionAlreadyTrue_ReturnsAfterSingleAttempt()
    {
        var attempts = 0;

        var result = await Poller.WaitUntilAsync(
            () => Task.FromResult(++attempts),
            n => n >= 1);

        Assert.Equal(1, result);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task WaitUntil_ConditionBecomesTrue_ReturnsWithoutWaitingForTimeout()
    {
        var attempts = 0;
        var stopwatch = Stopwatch.StartNew();

        var result = await Poller.WaitUntilAsync(
            () => Task.FromResult(++attempts),
            n => n == 3,
            interval: FastInterval,
            timeout: TimeSpan.FromSeconds(10));

        Assert.Equal(3, result);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Took {stopwatch.Elapsed} — should return as soon as the condition holds.");
    }

    [Fact]
    public async Task WaitUntil_ConditionNeverTrue_ThrowsWithConditionAndLastValue()
    {
        var attempts = 0;

        var ex = await Assert.ThrowsAsync<PollingTimeoutException>(() => Poller.WaitUntilAsync(
            () => Task.FromResult($"status-{++attempts}"),
            s => s == "ReadyForFulfillment",
            interval: FastInterval,
            timeout: TimeSpan.FromMilliseconds(200)));

        Assert.Contains("s => s == \"ReadyForFulfillment\"", ex.Message); // condition source text
        Assert.Contains($"status-{attempts}", ex.Message);                // last observed value
        Assert.Equal($"status-{attempts}", ex.LastResult);
        Assert.Equal(attempts, ex.Attempts);
        Assert.True(ex.Attempts > 1);
    }

    [Fact]
    public async Task WaitUntil_ResultWithoutToString_IsDescribedAsJson()
    {
        var ex = await Assert.ThrowsAsync<PollingTimeoutException>(() => Poller.WaitUntilAsync(
            () => Task.FromResult(new StatusSnapshot { Status = "Pending" }),
            o => o.Status == "Done",
            interval: FastInterval,
            timeout: TimeSpan.FromMilliseconds(100)));

        Assert.Contains("\"status\":\"Pending\"", ex.Message);
    }

    [Fact]
    public async Task WaitUntil_AttemptStillRunningAtDeadline_IsCancelledAndTimesOut()
    {
        var stopwatch = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<PollingTimeoutException>(() => Poller.WaitUntilAsync(
            async token =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                return 0;
            },
            _ => true,
            timeout: TimeSpan.FromMilliseconds(200)));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "Deadline must cancel the in-flight attempt.");
        Assert.Contains("No attempt completed", ex.Message);
        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);
    }

    [Fact]
    public async Task WaitUntil_ActionThrows_PropagatesImmediately()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Poller.WaitUntilAsync<int>(
            () => { attempts++; throw new InvalidOperationException("boom"); },
            _ => true,
            interval: FastInterval));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task WaitUntil_CallerCancels_ThrowsOperationCanceledNotTimeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Poller.WaitUntilAsync(
            () => Task.FromResult(false),
            done => done,
            interval: FastInterval,
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken: cts.Token));

        Assert.IsNotType<PollingTimeoutException>(ex);
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(-1, 1000)]
    [InlineData(100, 0)]
    public async Task WaitUntil_NonPositiveIntervalOrTimeout_Throws(int intervalMs, int timeoutMs)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Poller.WaitUntilAsync(
            () => Task.FromResult(true),
            done => done,
            interval: TimeSpan.FromMilliseconds(intervalMs),
            timeout: TimeSpan.FromMilliseconds(timeoutMs)));
    }

    // No ToString() override (unlike records/anonymous types), so the Poller falls back to JSON.
    private sealed class StatusSnapshot
    {
        public string Status { get; init; } = string.Empty;
    }
}
