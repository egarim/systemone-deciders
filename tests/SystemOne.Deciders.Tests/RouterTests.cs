using Xunit;

namespace SystemOne.Deciders.Tests;

/// <summary>
/// The point of the IDecider seam: test the code that CONSUMES a decision with no model at
/// all. Same tests pass whether the engine is Jev, a local model, or this fake.
/// </summary>
public class RouterTests
{
    private static readonly string[] Teams = ["billing", "technical", "sales"];

    // the "app code" under test — routes a ticket, or escalates when the decider abstains
    private static async Task<string> Route(IDecider decider, string ticket)
    {
        var d = await decider.ChooseAsync(ticket, "Which team should handle this?", Teams, abstainBelow: 0.6);
        return d.Abstained ? "human-review" : d.Value;
    }

    [Fact]
    public async Task Routes_to_the_decided_team()
    {
        var team = await Route(FakeDecider.Always("technical"), "Stripe integration is broken");
        Assert.Equal("technical", team);
    }

    [Fact]
    public async Task Escalates_to_human_when_decider_is_unsure()
    {
        var unsure = new FakeDecider(
            new Weighted("billing", 0.34), new Weighted("technical", 0.33), new Weighted("sales", 0.33));
        var team = await Route(unsure, "just saying hi");
        Assert.Equal("human-review", team);
    }
}
