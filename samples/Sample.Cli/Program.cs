using SystemOne.Deciders;

// One decision, three engines behind the SAME interface. Pick with the first arg:
//   dotnet run -- fake     (no model, canned)
//   dotnet run -- jev      (needs TYPESAFE_API_KEY)
//   dotnet run -- local    (needs a llama.cpp server, default http://localhost:8080)
var engine = args.Length > 0 ? args[0] : "fake";
string[] teams = ["billing", "technical", "sales"];
const string ticket = "Hi, I've been trying to connect my Stripe account for 3 days and keep getting charged.";

IDecider decider = engine switch
{
    "jev"     => new JevDecider(new HttpClient()),                                 // TypeSafe Jev (cloud)
    "openjev" => new OpenJevDecider(new HttpClient                                 // github.com/daseinlabs/open-jev
                   { BaseAddress = new Uri(Environment.GetEnvironmentVariable("OPENJEV") ?? "http://localhost:8000") }),
    "openai"  => new OpenAiCompatDecider(new HttpClient                            // OpenAI / Azure / Ollama / LM Studio / vLLM
                   { BaseAddress = new Uri(Environment.GetEnvironmentVariable("OPENAI_BASE") ?? "https://api.openai.com/") },
                   model: Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o-mini"),
    "local"   => new LocalDecider(new HttpClient                                   // llama.cpp + GBNF grammar
                   { BaseAddress = new Uri(Environment.GetEnvironmentVariable("LOCAL_LLM") ?? "http://localhost:8080") }),
    _          => FakeDecider.Always("technical", 0.82),                           // no model, for tests/demos
};

var d = await decider.ChooseAsync(ticket, "Which team should handle this?", teams, abstainBelow: 0.6);

Console.WriteLine($"engine    : {engine}");
Console.WriteLine($"decision  : {d.Value}   ({d.Confidence:P0} confidence){(d.Abstained ? "  -> ABSTAIN, escalate" : "")}");
foreach (var w in d.Options.OrderByDescending(o => o.Probability))
    Console.WriteLine($"  {w.Probability,6:P1}  {w.Option}");
