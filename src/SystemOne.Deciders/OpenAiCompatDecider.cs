using System.Net.Http.Json;
using System.Text.Json;

namespace SystemOne.Deciders;

/// <summary>
/// The mainstream alternative: constrain a chat model with <b>structured outputs</b>
/// (<c>response_format: json_schema</c>, strict) over any OpenAI-compatible endpoint. The schema
/// pins the answer to an <c>enum</c> of your options, so the model cannot return anything else.
///
/// One class reaches a huge slice of the ecosystem — point <c>BaseAddress</c> at:
///   • OpenAI / Azure OpenAI   (cloud, needs a key)
///   • Ollama, LM Studio, vLLM, LocalAI   (local OpenAI-compatible servers, key optional)
///
/// The honest limitation vs Jev / open-jev: strict json_schema guarantees a <b>valid</b> value but
/// gives you <b>no calibrated distribution</b> — you get the label, not a probability you can trust.
/// This decider reports confidence 1.0 for the chosen option; when you need a real distribution over
/// the options, use <see cref="OpenJevDecider"/> (batched log-prob scoring) instead.
/// </summary>
public sealed class OpenAiCompatDecider : IDecider
{
    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly string _model;

    public OpenAiCompatDecider(HttpClient http, string model, string? apiKey = null)
    {
        _http = http;
        _model = model;
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY"); // null is fine for local servers
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri("https://api.openai.com/");
    }

    public async Task<Decision> ChooseAsync(
        string state, string question, IReadOnlyList<string> options,
        double abstainBelow = 0.0, CancellationToken ct = default)
    {
        if (options.Count < 2) throw new ArgumentException("A choice needs at least two options.", nameof(options));

        var body = new
        {
            model = _model,
            messages = new object[]
            {
                new { role = "system", content = "You are a classifier. Answer only with the schema." },
                new { role = "user", content = $"State:\n{state}\n\nQuestion: {question}" },
            },
            response_format = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "decision",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        properties = new { choice = new { type = "string", @enum = options } },
                        required = new[] { "choice" },
                        additionalProperties = false,
                    }
                }
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions") { Content = JsonContent.Create(body) };
        if (!string.IsNullOrEmpty(_apiKey)) req.Headers.Add("Authorization", $"Bearer {_apiKey}");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new JevApiException((int)resp.StatusCode, json);

        var choice = ParseChoice(json, options);
        // json_schema gives a valid value but no calibrated distribution: chosen=1, rest=0.
        var dist = options.Select(o => new Weighted(o, o == choice ? 1.0 : 0.0)).ToList();
        return Decision.From(dist, abstainBelow);
    }

    /// <summary>Reads <c>choices[0].message.content</c> (a JSON string) and pulls out <c>choice</c>.</summary>
    internal static string ParseChoice(string json, IReadOnlyList<string> options)
    {
        using var doc = JsonDocument.Parse(json);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content").GetString() ?? "";

        using var inner = JsonDocument.Parse(content);
        var choice = inner.RootElement.TryGetProperty("choice", out var c) ? c.GetString() : null;

        // strict schema should guarantee membership, but never trust it blindly.
        return options.FirstOrDefault(o => string.Equals(o, choice, StringComparison.OrdinalIgnoreCase))
               ?? throw new JevApiException(200, $"model returned an off-schema choice: {content}");
    }
}
