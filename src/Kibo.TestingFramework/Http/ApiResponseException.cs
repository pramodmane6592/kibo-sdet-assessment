using System.Net;
using Kibo.TestingFramework.Diagnostics;

namespace Kibo.TestingFramework.Http;

/// <summary>
/// Thrown when a response does not meet a test's expectation. The message contains the full
/// captured exchange (request, response, timing, correlation id) so the failure is self-diagnosing.
/// </summary>
public sealed class ApiResponseException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Diagnostic message.</param>
    /// <param name="statusCode">Status code that was received.</param>
    /// <param name="exchange">Captured exchange, if available.</param>
    public ApiResponseException(string message, HttpStatusCode statusCode, HttpExchange? exchange)
        : base(message)
    {
        StatusCode = statusCode;
        Exchange = exchange;
    }

    /// <summary>Status code that was received.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>Captured exchange, if the diagnostics handler is installed.</summary>
    public HttpExchange? Exchange { get; }
}
