using System.Net.Http.Json;
using System.Text.Json;

namespace SystemOne.Deciders;

/// <summary>
/// Wraps <b>open-jev</b> (github.com/daseinlabs/open-jev) — an MIT-licensed, Apple-Silicon (MLX)
/// open reproduction of Jev's actual mechanism. It prefills the context once, expands the KV cache
/// across the option batch, and scores every option in a <b>single padded forward pass</b>: the
/// score is the log-probability of each option's tokens given the context, and a softmax over the
/// option scores gives a probability per option. That is the closest thing to Jev you can self-host.
///
/// Run the server first (native macOS, Metal):  make serve   # openjev serve --port 8000
/// Then: new OpenJevDecider(new HttpClient{ BaseAddress = new("http://localhost:8000") }).
///
/// Unlike a chat model behind a grammar, this returns a genuine distribution over YOUR options in
/// one pass — no autoregressive decoding, no JSON to parse on the model's side.
/// </summary>
public sealed class OpenJevDecider : IDecider
{
    private readonly HttpClient _http;
    private readonly string _norm;

    /// <param name="norm">Score normalization: "mean" (length-adjusted, the default), "sum" (raw
    /// likelihood, biases toward shorter options), or "pmi" (accounts for base-rate plausibility,
    /// costs an extra pass).</param>
    public OpenJevDecider(HttpClient http, string norm = "mean")
    {
        _http = http;
        _norm = norm;
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri("http://localhost:8000");
    }

    public async Task<Decision> ChooseAsync(
        string state, string question, IReadOnlyList<string> options,
        double abstainBelow = 0.0, CancellationToken ct = default)
    {
        if (options.Count < 2) throw new ArgumentException("A choice needs at least two options.", nameof(options));

        var body = new
        {
            context = $"{state}\n{question}\nAnswer:",
            options = options,     // scored as continuations of the context
            norm = _norm,
            chat = false,
            sep = " ",
        };

        using var resp = await _http.PostAsJsonAsync("/score", body, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new JevApiException((int)resp.StatusCode, json);

        var dist = ParseScore(json, options);
        return Decision.From(dist, abstainBelow);
    }

    /// <summary>Maps open-jev's parallel <c>probability[]</c> array back onto the options we sent.</summary>
    internal static IReadOnlyList<Weighted> ParseScore(string json, IReadOnlyList<string> options)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("probability", out var probs) || probs.ValueKind != JsonValueKind.Array)
            throw new JevApiException(200, $"open-jev response missing 'probability': {json}");

        var p = probs.EnumerateArray().Select(e => e.GetDouble()).ToList();
        if (p.Count != options.Count)
            throw new JevApiException(200, $"open-jev returned {p.Count} probabilities for {options.Count} options");

        return options.Select((opt, i) => new Weighted(opt, p[i])).ToList();
    }
}
