namespace SystemOne.Deciders;

/// <summary>One option and the probability the decider assigned to it.</summary>
public sealed record Weighted(string Option, double Probability);

/// <summary>
/// A "System One" decision: a value chosen from a bounded set, the confidence in it,
/// the full distribution over the options, and whether the decider abstained (top
/// probability fell under the caller's threshold). There is no free text here — that is
/// the whole point of a System One decider.
/// </summary>
public sealed record Decision(string Value, double Confidence, IReadOnlyList<Weighted> Options, bool Abstained)
{
    public static Decision From(IReadOnlyList<Weighted> dist, double abstainBelow)
    {
        // ponytail: argmax + threshold is all a bounded decision needs.
        var top = dist.OrderByDescending(w => w.Probability).First();
        return new Decision(top.Option, top.Probability, dist, top.Probability < abstainBelow);
    }
}

/// <summary>
/// The contract. Hand it program state as text plus a bounded question, get back a value
/// from <paramref name="options"/> — never anything else. Cloud (Jev), local (a model in a
/// constrained-decoding harness), or a stub can all sit behind this one interface, so the
/// calling code, its tests, and its behaviour never change when you swap the engine.
/// </summary>
public interface IDecider
{
    Task<Decision> ChooseAsync(
        string state,
        string question,
        IReadOnlyList<string> options,
        double abstainBelow = 0.0,
        CancellationToken ct = default);
}
