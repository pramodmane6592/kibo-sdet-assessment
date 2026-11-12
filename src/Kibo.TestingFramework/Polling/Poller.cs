using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Kibo.TestingFramework.Http;

namespace Kibo.TestingFramework.Polling;

/// <summary>
/// Polls an asynchronous operation until its result satisfies a condition — a replacement for
/// fixed <c>Thread.Sleep</c>/<c>Task.Delay</c> waits on eventually-consistent behavior.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Returns as soon as the condition is met; the first attempt runs immediately.</item>
/// <item>The timeout is a hard deadline: an attempt still in flight when it expires is cancelled
/// through the <see cref="CancellationToken"/> passed to the action, and abandoned if it does not
/// honor that token — the poller never waits past its timeout and never accepts a late result.</item>
/// <item>On timeout a <see cref="PollingTimeoutException"/> reports the condition, attempt count
/// and the last observed value.</item>
/// <item>Exceptions thrown by the action or condition are not swallowed: they propagate
/// immediately, because an unexpected error is a real failure, not "not ready yet".</item>
/// <item>Cancellation requested by the caller surfaces as <see cref="OperationCanceledException"/>, not as a timeout.</item>
/// </list>
/// </remarks>
public static class Poller
{
    /// <summary>Default delay between attempts.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Default overall timeout.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private const int MaxDescribedValueLength = 2000;

    /// <summary>
    /// Invokes <paramref name="action"/> until <paramref name="condition"/> returns <c>true</c> for its result.
    /// </summary>
    /// <typeparam name="T">Result type of the action.</typeparam>
    /// <param name="action">Operation to poll.</param>
    /// <param name="condition">Predicate the result must satisfy.</param>
    /// <param name="interval">Delay between attempts; defaults to <see cref="DefaultInterval"/>.</param>
    /// <param name="timeout">Overall deadline; defaults to <see cref="DefaultTimeout"/>.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <param name="conditionDescription">
    /// Text describing the condition in the timeout message. Filled in by the compiler with the
    /// source text of <paramref name="condition"/>; pass explicitly for a friendlier description.
    /// </param>
    /// <returns>The first result that satisfies the condition.</returns>
    /// <exception cref="PollingTimeoutException">The condition was not met in time.</exception>
    public static Task<T> WaitUntilAsync<T>(
        Func<Task<T>> action,
        Func<T, bool> condition,
        TimeSpan? interval = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        [CallerArgumentExpression(nameof(condition))] string? conditionDescription = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        return WaitUntilAsync(_ => action(), condition, interval, timeout, cancellationToken, conditionDescription);
    }

    /// <summary>
    /// Invokes <paramref name="action"/> until <paramref name="condition"/> returns <c>true</c> for its result.
    /// The action receives a token that is cancelled at the deadline, so slow attempts cannot overrun it.
    /// </summary>
    /// <typeparam name="T">Result type of the action.</typeparam>
    /// <param name="action">Operation to poll; should honor the supplied token.</param>
    /// <param name="condition">Predicate the result must satisfy.</param>
    /// <param name="interval">Delay between attempts; defaults to <see cref="DefaultInterval"/>.</param>
    /// <param name="timeout">Overall deadline; defaults to <see cref="DefaultTimeout"/>.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <param name="conditionDescription">
    /// Text describing the condition in the timeout message. Filled in by the compiler with the
    /// source text of <paramref name="condition"/>; pass explicitly for a friendlier description.
    /// </param>
    /// <returns>The first result that satisfies the condition.</returns>
    /// <exception cref="PollingTimeoutException">The condition was not met in time.</exception>
    public static async Task<T> WaitUntilAsync<T>(
        Func<CancellationToken, Task<T>> action,
        Func<T, bool> condition,
        TimeSpan? interval = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        [CallerArgumentExpression(nameof(condition))] string? conditionDescription = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(condition);

        var pollInterval = interval ?? DefaultInterval;
        var pollTimeout = timeout ?? DefaultTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pollInterval, TimeSpan.Zero, nameof(interval));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pollTimeout, TimeSpan.Zero, nameof(timeout));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(pollTimeout);

        var stopwatch = Stopwatch.StartNew();
        var attempts = 0;
        var hasResult = false;
        T? lastResult = default;
        OperationCanceledException? interruption = null;

        try
        {
            while (true)
            {
                attempts++;

                // WaitAsync enforces the deadline even if the action ignores its token (or, via the
                // Func<Task<T>> overload, never received one). The abandoned attempt keeps running in
                // the background; its eventual failure is observed so it cannot surface as an
                // UnobservedTaskException.
                var attempt = action(deadline.Token);
                try
                {
                    lastResult = await attempt.WaitAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!attempt.IsCompleted)
                {
                    ObserveInBackground(attempt);
                    throw;
                }

                hasResult = true;

                if (condition(lastResult))
                {
                    return lastResult;
                }

                var remaining = pollTimeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                await Task.Delay(remaining < pollInterval ? remaining : pollInterval, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The deadline fired while waiting or while an attempt was in flight.
            interruption = ex;
        }

        stopwatch.Stop();

        var lastObserved = hasResult
            ? $"Last observed value: {Describe(lastResult)}"
            : "No attempt completed before the timeout.";

        throw new PollingTimeoutException(
            $"Timed out after {stopwatch.Elapsed.TotalSeconds:0.00}s waiting for: {conditionDescription ?? "condition"}"
                + $" ({attempts} attempt(s), interval {pollInterval.TotalMilliseconds:0}ms, timeout {pollTimeout.TotalSeconds:0.##}s)."
                + Environment.NewLine + lastObserved,
            hasResult ? lastResult : null,
            attempts,
            stopwatch.Elapsed,
            interruption);
    }

    private static void ObserveInBackground(Task task) =>
        task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>Renders a value for diagnostics: its own <c>ToString()</c> if overridden, otherwise JSON.</summary>
    private static string Describe(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        var text = value.ToString();
        if (text is null || text == value.GetType().ToString())
        {
            try
            {
                text = JsonSerializer.Serialize(value, value.GetType(), ApiClientBase.DefaultJsonOptions);
            }
            catch (Exception ex) when (ex is NotSupportedException or JsonException)
            {
                text = value.GetType().Name;
            }
        }

        return text.Length <= MaxDescribedValueLength ? text : text[..MaxDescribedValueLength] + "… (truncated)";
    }
}
