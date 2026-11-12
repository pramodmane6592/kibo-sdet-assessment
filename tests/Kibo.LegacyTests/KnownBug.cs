using System.Net;
using Kibo.TestingFramework.Http;

namespace Kibo.LegacyTests;

/// <summary>
/// Assertions for documented API defects. A known-bug test pins the API's <em>current</em> behavior
/// so the suite stays green, and fails with an explicit "appears FIXED" message as soon as the API
/// starts returning the correct status — prompting the test to be flipped to assert the fix.
/// </summary>
public static class KnownBug
{
    /// <summary>Asserts the API still exhibits bug <paramref name="bugId"/>.</summary>
    /// <param name="response">Response under test.</param>
    /// <param name="bugId">Identifier of the bug report (see <see cref="OrderEdgeCaseTests"/>).</param>
    /// <param name="expected">Status the API <em>should</em> return.</param>
    /// <param name="actual">Status the API currently returns because of the bug.</param>
    public static void AssertStatus<T>(ApiResponse<T> response, string bugId, HttpStatusCode expected, HttpStatusCode actual)
    {
        if (response.StatusCode == actual)
        {
            return;
        }

        Assert.Fail(response.StatusCode == expected
            ? $"{bugId} appears FIXED: the API now returns the expected {(int)expected} {expected}. "
              + "Update this test to assert the correct behavior and close the bug."
            : $"{bugId}: expected the known (buggy) {(int)actual} {actual} or the fixed {(int)expected} {expected}, "
              + $"but got:{Environment.NewLine}{response.Describe()}");
    }
}
