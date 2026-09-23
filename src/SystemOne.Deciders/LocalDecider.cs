using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SystemOne.Deciders;

/// <summary>
/// Turns ANY local model into a System One decider — no Jev, no cloud. It talks to a
/// llama.cpp server and uses <b>constrained decoding</b>: a GBNF grammar built from your
/// options so the ONLY tokens the model may emit spell one of them. The result cannot be
/// a paragraph, a "maybe", or a malformed value — it is one of your options, always.
///
/// Run a server first, e.g.:
///   llama-server -m qwen2.5-3b-instruct-q4_k_m.gguf --port 8080
/// Then: new LocalDecider(new HttpClient{ BaseAddress = new("http://localhost:8080") }).
///
/// Confidence comes from the first decision token's probability (n_probs). That guarantees a
/// VALID value with a usable confidence in one generation; for a fully calibrated distribution
/// over every option, score each option's sequence (Approach A, see README) — this class does
/// the guaranteed-valid path, which is what most routing/guardrail decisions actually need.
/// LM Studio / Ollama expose the same idea via json_schema/grammar; point BaseAddress there and
/// adjust the request field names.
/// </summary>
public sealed class LocalDecider : IDecider
{
    private readonly HttpClient _http;

    public LocalDecider(HttpClient http)
    {
        _http = http;
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri("http://localhost:8080");
    }

    public async Task<Decision> ChooseAsync(
        string state, string question, IReadOnlyList<string> options,
        double abstainBelow = 0.0, CancellationToken ct = default)
    {
        if (options.Count < 2) throw new ArgumentException("A choice needs at least two options.", nameof(options));

        var body = new
        {
            prompt = BuildPrompt(state, question, options),
            grammar = BuildGrammar(options),   // <-- the harness: output space is the options, nothing else
            temperature = 0.0,
            n_predict = 32,
            n_probs = 1,
            cache_prompt = true,
            stop = new[] { "\n" }
        };

        using var resp = await _http.PostAsJsonAsync("/completion", body, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new JevApiException((int)resp.StatusCode, json);

        var (value, firstTokenProb) = ParseCompletion(json, options);
        var dist = SpreadDistribution(value, firstTokenProb, options);
        return Decision.From(dist, abstainBelow);
    }

    /// <summary>GBNF that admits exactly the options: <c>root ::= "billing" | "technical" | "sales"</c>.</summary>
    internal static string BuildGrammar(IReadOnlyList<string> options)
    {
        var alts = string.Join(" | ", options.Select(o => "\"" + Escape(o) + "\""));
        return "root ::= " + alts;

        static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    internal static string BuildPrompt(string state, string question, IReadOnlyList<string> options)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a classifier. Read the state and answer the question with exactly one label.");
        sb.AppendLine("Reply with the label only — no punctuation, no explanation.");
        sb.AppendLine();
        sb.Append("State: ").AppendLine(state);
        sb.Append("Question: ").AppendLine(question);
        sb.Append("Labels: ").AppendLine(string.Join(", ", options));
        sb.Append("Answer: ");
        return sb.ToString();
    }

    /// <summary>Reads the generated label and the probability of its first token from n_probs.</summary>
    internal static (string value, double firstTokenProb) ParseCompletion(string json, IReadOnlyList<string> options)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var content = root.TryGetProperty("content", out var c) ? c.GetString()?.Trim() ?? "" : "";
        // grammar guarantees content IS one of the options, but be defensive about whitespace/casing.
        var value = options.FirstOrDefault(o => string.Equals(o, content, StringComparison.OrdinalIgnoreCase))
                    ?? options.FirstOrDefault(o => content.StartsWith(o, StringComparison.OrdinalIgnoreCase))
                    ?? content;

        double firstTokenProb = 1.0;
        if (root.TryGetProperty("completion_probabilities", out var cp) &&
            cp.ValueKind == JsonValueKind.Array && cp.GetArrayLength() > 0)
        {
            var first = cp[0];
            if (first.TryGetProperty("probs", out var probs) && probs.ValueKind == JsonValueKind.Array &&
                probs.GetArrayLength() > 0 && probs[0].TryGetProperty("prob", out var p))
                firstTokenProb = p.GetDouble();
        }
        return (value, firstTokenProb);
    }

    private static IReadOnlyList<Weighted> SpreadDistribution(string value, double conf, IReadOnlyList<string> options)
    {
        conf = Math.Clamp(conf, 0.0, 1.0);
        var rest = options.Count > 1 ? (1.0 - conf) / (options.Count - 1) : 0.0;
        return options.Select(o => new Weighted(o, o == value ? conf : rest)).ToList();
    }
}
