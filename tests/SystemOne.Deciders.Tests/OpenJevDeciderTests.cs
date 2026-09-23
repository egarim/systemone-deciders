using System.Text.Json;
using Xunit;

namespace SystemOne.Deciders.Tests;

public class OpenJevDeciderTests
{
    private static readonly string[] Teams = ["billing", "technical", "sales"];

    [Fact]
    public void Maps_parallel_probability_array_onto_options()
    {
        var canned = """
        {"best":"billing","best_index":0,"probability":[0.94,0.05,0.01],
         "logprob_sum":[-0.51,-2.99,-4.6],"n_tokens":[1,1,1],"timing":0.09}
        """;
        var dist = OpenJevDecider.ParseScore(canned, Teams);
        Assert.Equal(3, dist.Count);
        Assert.Equal("billing", dist[0].Option);
        Assert.Equal(0.94, dist[0].Probability, 3);
        Assert.Equal(0.01, dist[2].Probability, 3);
    }

    [Fact]
    public async Task Returns_a_real_distribution_and_argmax_via_stub_server()
    {
        var canned = """
        {"best":"technical","best_index":1,"probability":[0.10,0.85,0.05],
         "logprob_sum":[-2.3,-0.16,-3.0],"n_tokens":[1,1,1],"timing":0.09}
        """;
        var d = await new OpenJevDecider(new HttpClient(new StubHandler(canned))
        { BaseAddress = new Uri("http://localhost:8000") })
            .ChooseAsync("stripe won't connect", "Which team?", Teams);

        Assert.Equal("technical", d.Value);
        Assert.Equal(0.85, d.Confidence, 3);       // a genuine probability, not just a label
        Assert.False(d.Abstained);
    }

    [Fact]
    public async Task Sends_context_options_and_norm()
    {
        var stub = new StubHandler("""{"best":"billing","best_index":0,"probability":[1,0,0],"logprob_sum":[0,-9,-9],"n_tokens":[1,1,1],"timing":0.05}""");
        await new OpenJevDecider(new HttpClient(stub) { BaseAddress = new Uri("http://localhost:8000") }, norm: "pmi")
            .ChooseAsync("state", "Which team?", Teams);

        using var sent = JsonDocument.Parse(stub.LastRequestBody!);
        Assert.Contains("Which team?", sent.RootElement.GetProperty("context").GetString());
        Assert.Equal(3, sent.RootElement.GetProperty("options").GetArrayLength());
        Assert.Equal("pmi", sent.RootElement.GetProperty("norm").GetString());
    }

    [Fact]
    public async Task Abstains_when_top_probability_below_threshold()
    {
        var canned = """{"best":"billing","best_index":0,"probability":[0.4,0.35,0.25],"logprob_sum":[-1,-1,-1],"n_tokens":[1,1,1],"timing":0.05}""";
        var d = await new OpenJevDecider(new HttpClient(new StubHandler(canned)) { BaseAddress = new Uri("http://localhost:8000") })
            .ChooseAsync("ambiguous", "Which team?", Teams, abstainBelow: 0.6);
        Assert.True(d.Abstained);
        Assert.Equal("billing", d.Value);
    }
}
