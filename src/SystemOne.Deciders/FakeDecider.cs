namespace SystemOne.Deciders;

/// <summary>
/// A decider with no model behind it — you give it the distribution to return. Perfect for
/// unit-testing the code that CONSUMES a decision (routers, guardrails) without a network,
/// a key, or a GPU. Deterministic and instant.
/// </summary>
public sealed class FakeDecider : IDecider
{
    private readonly Func<string, string, IReadOnlyList<string>, IReadOnlyList<Weighted>> _answer;

    public FakeDecider(Func<string, string, IReadOnlyList<string>, IReadOnlyList<Weighted>> answer) => _answer = answer;

    /// <summary>Always return this exact distribution (options must match the call).</summary>
    public FakeDecider(params Weighted[] distribution) : this((_, _, _) => distribution) { }

    /// <summary>Always pick <paramref name="winner"/> at <paramref name="confidence"/>, rest share the remainder.</summary>
    public static FakeDecider Always(string winner, double confidence = 0.99) =>
        new((_, _, options) =>
        {
            var rest = options.Count > 1 ? (1.0 - confidence) / (options.Count - 1) : 0.0;
            return options.Select(o => new Weighted(o, o == winner ? confidence : rest)).ToList();
        });

    public Task<Decision> ChooseAsync(
        string state, string question, IReadOnlyList<string> options,
        double abstainBelow = 0.0, CancellationToken ct = default)
        => Task.FromResult(Decision.From(_answer(state, question, options), abstainBelow));
}
