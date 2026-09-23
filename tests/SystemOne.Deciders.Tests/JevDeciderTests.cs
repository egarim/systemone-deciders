using System.Net;
using System.Text.Json;
using Xunit;

namespace SystemOne.Deciders.Tests;

public class JevDeciderTests
{
    private static readonly string[] Teams = ["billing", "technical", "sales"];

    [Fact]
    public async Task Maps_options_shape_response_to_typed_decision()
    {
        // "answers[key].options[] = {option, probability}"
        var canned = """
        {"answers":{"decision":{"options":[
            {"option":"billing","probability":0.94},
            {"option":"technical","probability":0.05},
            {"option":"sales","probability":0.01}]}}}
        """;
        var stub = new StubHandler(canned);
        var jev = new JevDecider(new HttpClient(stub), apiKey: "test-key");

        var d = await jev.ChooseAsync("I was charged twice", "Which team?", Teams);

        Assert.Equal("billing", d.Value);
        Assert.Equal(0.94, d.Confidence, 3);
        Assert.False(d.Abstained);
        Assert.Equal(3, d.Options.Count);
    }

    [Fact]
    public async Task Maps_flat_map_response_shape_too()
    {
        var canned = """{"decision":{"billing":0.10,"technical":0.85,"sales":0.05}}""";
        var jev = new JevDecider(new HttpClient(new StubHandler(canned)), apiKey: "k");

        var d = await jev.ChooseAsync("stripe won't connect", "Which team?", Teams);

        Assert.Equal("technical", d.Value);
        Assert.Equal(0.85, d.Confidence, 3);
    }

    [Fact]
    public async Task Abstains_when_top_probability_below_threshold()
    {
        var canned = """{"decision":{"billing":0.34,"technical":0.33,"sales":0.33}}""";
        var jev = new JevDecider(new HttpClient(new StubHandler(canned)), apiKey: "k");

        var d = await jev.ChooseAsync("ambiguous", "Which team?", Teams, abstainBelow: 0.6);

        Assert.True(d.Abstained);          // 0.34 < 0.6 -> escalate to a human / a bigger model
        Assert.Equal("billing", d.Value);  // still reports the best guess
    }

    [Fact]
    public async Task Sends_the_documented_request_body()
    {
        var stub = new StubHandler("""{"decision":{"billing":1.0,"technical":0.0,"sales":0.0}}""");
        var jev = new JevDecider(new HttpClient(stub), apiKey: "secret-123", model: "jev-latest");

        await jev.ChooseAsync("state text", "Which team?", Teams);

        using var sent = JsonDocument.Parse(stub.LastRequestBody!);
        Assert.Equal("jev-latest", sent.RootElement.GetProperty("model").GetString());
        Assert.Equal("state text", sent.RootElement.GetProperty("state").GetString());
        var q = sent.RootElement.GetProperty("questions").GetProperty("decision");
        Assert.Equal("choice", q.GetProperty("type").GetString());
        Assert.Equal(3, q.GetProperty("options").GetArrayLength());
        Assert.Equal("Bearer secret-123", stub.LastRequest!.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task Throws_typed_exception_on_api_error()
    {
        var jev = new JevDecider(new HttpClient(new StubHandler("nope", HttpStatusCode.TooManyRequests)), apiKey: "k");
        var ex = await Assert.ThrowsAsync<JevApiException>(() =>
            jev.ChooseAsync("x", "Which team?", Teams));
        Assert.Equal(429, ex.StatusCode);
    }
}
