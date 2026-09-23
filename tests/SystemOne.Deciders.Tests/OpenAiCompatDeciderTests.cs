using System.Text.Json;
using Xunit;

namespace SystemOne.Deciders.Tests;

public class OpenAiCompatDeciderTests
{
    private static readonly string[] Teams = ["billing", "technical", "sales"];

    // shape of an OpenAI /v1/chat/completions response; message.content is a JSON string {"choice":"..."}
    private static string Canned(string choice) =>
        "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"choice\\\":\\\"" + choice + "\\\"}\"}}]}";

    [Fact]
    public async Task Parses_structured_choice_into_a_valid_value()
    {
        var d = await new OpenAiCompatDecider(new HttpClient(new StubHandler(Canned("technical"))), model: "gpt-x", apiKey: "k")
            .ChooseAsync("stripe won't connect", "Which team?", Teams);

        Assert.Equal("technical", d.Value);   // guaranteed one of the options
        Assert.Equal(1.0, d.Confidence, 3);    // json_schema gives a valid value, not a calibrated prob
        Assert.False(d.Abstained);
    }

    [Fact]
    public async Task Sends_a_strict_json_schema_with_the_options_as_an_enum()
    {
        var stub = new StubHandler(Canned("billing"));
        await new OpenAiCompatDecider(new HttpClient(stub), model: "gpt-x", apiKey: "secret")
            .ChooseAsync("charged twice", "Which team?", Teams);

        using var sent = JsonDocument.Parse(stub.LastRequestBody!);
        var fmt = sent.RootElement.GetProperty("response_format");
        Assert.Equal("json_schema", fmt.GetProperty("type").GetString());
        var schema = fmt.GetProperty("json_schema");
        Assert.True(schema.GetProperty("strict").GetBoolean());
        var en = schema.GetProperty("schema").GetProperty("properties").GetProperty("choice").GetProperty("enum");
        Assert.Equal(3, en.GetArrayLength());
        Assert.Equal("Bearer secret", stub.LastRequest!.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task Works_against_a_local_server_with_no_api_key()
    {
        // Ollama / LM Studio / vLLM: no Authorization header should be sent.
        var stub = new StubHandler(Canned("sales"));
        var http = new HttpClient(stub) { BaseAddress = new Uri("http://localhost:11434") };
        var d = await new OpenAiCompatDecider(http, model: "qwen2.5", apiKey: "")
            .ChooseAsync("upgrade my plan", "Which team?", Teams);

        Assert.Equal("sales", d.Value);
        Assert.False(stub.LastRequest!.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task Rejects_an_off_schema_choice()
    {
        var d = new OpenAiCompatDecider(new HttpClient(new StubHandler(Canned("legal"))), model: "gpt-x", apiKey: "k");
        await Assert.ThrowsAsync<JevApiException>(() => d.ChooseAsync("x", "Which team?", Teams));
    }
}
