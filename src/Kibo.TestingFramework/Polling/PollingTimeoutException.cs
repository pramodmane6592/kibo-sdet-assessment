namespace Kibo.TestingFramework.Polling;

/// <summary>
/// Thrown by <see cref="Poller"/> when a condition is not met before the timeout.
/// The message and properties describe what was being waited for and the last value observed.
/// </summary>
public sealed class PollingTimeoutException : TimeoutException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Human-readable diagnostic message.</param>
    /// <param name="lastResult">Last value returned by the polled action, if any.</param>
    /// <param name="attempts">Number of times the action was invoked.</param>
    /// <param name="elapsed">Total time spent polling.</param>
    /// <param name="innerException">Cancellation that interrupted an in-flight attempt, if any.</param>
    public PollingTimeoutException(string message, object? lastResult, int attempts, TimeSpan elapsed, Exception? innerException = null)
        : base(message, innerException)
    {
        LastResult = lastResult;
        Attempts = attempts;
        Elapsed = elapsed;
    }

    /// <summary>Last value returned by the polled action (<c>null</c> if no attempt completed).</summary>
    public object? LastResult { get; }

    /// <summary>Number of times the action was invoked.</summary>
    public int Attempts { get; }

    /// <summary>Total time spent polling.</summary>
    public TimeSpan Elapsed { get; }
}
