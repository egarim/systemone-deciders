using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SystemOne.Deciders;

/// <summary>
/// Cloud decider backed by TypeSafe's Jev (System One) API:
/// <c>POST https://api.typesafe.ai/v1/systemone</c>, model <c>jev-latest</c>.
/// One "choice" question in, a typed value + a distribution out — no JSON to parse on the
/// wire that could ever be malformed, because the answer space was declared up front.
///
/// The request body is the documented shape. The response shape is parsed defensively:
/// Jev is early-access, so if the live payload differs, adjust <see cref="ParseChoice"/> —
/// or drop in a community SDK (SystemOneDotNet / Jev.Net) behind this same IDecider.
/// </summary>
public sealed class JevDecider : IDecider
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;
    private const string QuestionKey = "decision";

    public JevDecider(HttpClient http, string? apiKey = null, string model = "jev-latest")
    {
        _http = http;
        _apiKey = apiKey
            ?? Environment.GetEnvironmentVariable("TYPESAFE_API_KEY")
            ?? throw new InvalidOperationException("Set TYPESAFE_API_KEY or pass apiKey.");
        _model = model;
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri("https://api.typesafe.ai/");
    }

    public async Task<Decision> ChooseAsync(
        string state, string question, IReadOnlyList<string> options,
        double abstainBelow = 0.0, CancellationToken ct = default)
    {
        if (options.Count < 2) throw new ArgumentException("A choice needs at least two options.", nameof(options));

        var body = new
        {
            model = _model,
            state,
            questions = new Dictionary<string, object>
            {
                [QuestionKey] = new { type = "choice", question, options }
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/systemone")
        {
            Content = JsonContent.Create(body)
        };
        req.Headers.Add("Authorization", $"Bearer {_apiKey}");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new JevApiException((int)resp.StatusCode, json);

        var dist = ParseChoice(json, options);
        return Decision.From(dist, abstainBelow);
    }

    /// <summary>
    /// Pulls the per-option probabilities out of the answer for our question. Handles the
    /// documented "answers[key].options[] = {option, probability}" shape and falls back to a
    /// flat {option: probability} map. Any option the server omits is treated as p=0.
    /// </summary>
    internal static IReadOnlyList<Weighted> ParseChoice(string json, IReadOnlyList<string> options)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        JsonElement answer = default;
        if (root.TryGetProperty("answers", out var answers) && answers.TryGetProperty(QuestionKey, out var a))
            answer = a;
        else if (root.TryGetProperty(QuestionKey, out var a2))
            answer = a2;
        else
            answer = root;

        var probs = new Dictionary<string, double>(StringComparer.Ordinal);

        if (answer.ValueKind == JsonValueKind.Object &&
            answer.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
        {
            foreach (var o in opts.EnumerateArray())
            {
                var name = o.TryGetProperty("option", out var n) ? n.GetString() : null;
                var p = o.TryGetProperty("probability", out var pp) ? pp.GetDouble() : 0.0;
                if (name is not null) probs[name] = p;
            }
        }
        else if (answer.ValueKind == JsonValueKind.Object)
        {
            // flat map: { "billing": 0.94, "technical": 0.05, ... }
            foreach (var prop in answer.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.Number)
                    probs[prop.Name] = prop.Value.GetDouble();
        }

        return options.Select(opt => new Weighted(opt, probs.TryGetValue(opt, out var p) ? p : 0.0)).ToList();
    }
}

public sealed class JevApiException(int statusCode, string body)
    : Exception($"Jev API returned {statusCode}: {body}")
{
    public int StatusCode { get; } = statusCode;
    public string Body { get; } = body;
}
