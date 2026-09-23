using Xunit;

namespace SystemOne.Deciders.Tests;

/// <summary>
/// Real calls — off by default so the fast suite stays hermetic and green in CI. When the
/// env var isn't set the test returns immediately (passes) instead of failing.
///   Jev:   set TYPESAFE_LIVE=1 and TYPESAFE_API_KEY   (early-access waitlist key)
///   local: set LOCAL_LLM=http://localhost:8080 with a llama.cpp server running
/// Assert on the calibrated confidence and on batch accuracy — not one label — because a
/// single-case model assertion is flaky by nature.
/// </summary>
public class LiveTests
{
    private static readonly string[] Teams = ["billing", "technical", "sales"];

    [Fact]
    public async Task Jev_classifies_a_clear_billing_ticket()
    {
        if (Environment.GetEnvironmentVariable("TYPESAFE_LIVE") != "1") return;   // gated
        var jev = new JevDecider(new HttpClient());

        var d = await jev.ChooseAsync(
            "I was double-charged $40, please refund.", "Which team should handle this?", Teams);

        Assert.Equal("billing", d.Value);
        Assert.True(d.Confidence > 0.6, $"low confidence: {d.Confidence:P0}");
    }

    [Fact]
    public async Task Local_model_in_a_harness_only_ever_returns_a_valid_option()
    {
        var baseUrl = Environment.GetEnvironmentVariable("LOCAL_LLM");
        if (string.IsNullOrEmpty(baseUrl)) return;   // gated
        var local = new LocalDecider(new HttpClient { BaseAddress = new Uri(baseUrl) });

        var d = await local.ChooseAsync(
            "My card was charged twice this morning.", "Which team should handle this?", Teams);

        Assert.Contains(d.Value, Teams);   // the whole guarantee: never anything off-list
    }
}
