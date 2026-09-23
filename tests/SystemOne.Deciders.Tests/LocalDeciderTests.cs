using Xunit;

namespace SystemOne.Deciders.Tests;

public class LocalDeciderTests
{
    private static readonly string[] Teams = ["billing", "technical", "sales"];

    [Fact]
    public void Grammar_admits_exactly_the_options()
    {
        var g = LocalDecider.BuildGrammar(Teams);
        Assert.Equal("root ::= \"billing\" | \"technical\" | \"sales\"", g);
    }

    [Fact]
    public void Grammar_escapes_quotes_and_backslashes()
    {
        var g = LocalDecider.BuildGrammar(["a\"b", "c\\d"]);
        Assert.Equal("root ::= \"a\\\"b\" | \"c\\\\d\"", g);
    }

    [Fact]
    public void Prompt_lists_the_labels()
    {
        var p = LocalDecider.BuildPrompt("charged twice", "Which team?", Teams);
        Assert.Contains("Labels: billing, technical, sales", p);
        Assert.EndsWith("Answer: ", p);
    }

    [Fact]
    public void Parses_llama_cpp_completion_with_first_token_prob()
    {
        var canned = """
        {"content":"technical",
         "completion_probabilities":[{"content":"technical","probs":[{"tok_str":"technical","prob":0.88}]}]}
        """;
        var (value, prob) = LocalDecider.ParseCompletion(canned, Teams);
        Assert.Equal("technical", value);
        Assert.Equal(0.88, prob, 3);
    }

    [Fact]
    public async Task ChooseAsync_returns_valid_constrained_value_via_stub_server()
    {
        var canned = """
        {"content":"billing",
         "completion_probabilities":[{"content":"billing","probs":[{"tok_str":"billing","prob":0.79}]}]}
        """;
        var local = new LocalDecider(new HttpClient(new StubHandler(canned))
        { BaseAddress = new Uri("http://localhost:8080") });

        var d = await local.ChooseAsync("refund please", "Which team?", Teams);

        Assert.Equal("billing", d.Value);          // guaranteed one of the options
        Assert.Equal(0.79, d.Confidence, 3);
        Assert.Contains(d.Options, w => w.Option == "sales"); // full option set present
    }
}
