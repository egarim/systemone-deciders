# SystemOne.Deciders

One C# interface for **bounded decisions** — hand a model text + a fixed set of options, get back
one of those options with a probability, **never** free text. Behind the interface: TypeSafe's
**Jev** (cloud), **any local model** in a constrained-decoding harness, or a **fake** for tests.

> "System One" isn't a special model — it's a *harness*. Never let the model free-write; force it
> to pick from your answer space and read the distribution. Jev is one implementation; a local
> model + a grammar is another. Same contract.

```csharp
public interface IDecider
{
    Task<Decision> ChooseAsync(string state, string question,
        IReadOnlyList<string> options, double abstainBelow = 0.0, CancellationToken ct = default);
}
```

`Decision` = the chosen `Value`, its `Confidence`, the full `Options` distribution, and an
`Abstained` flag (top probability fell under your threshold → escalate to a human or a bigger model).

## The three engines

| Engine | Class | What it is |
|---|---|---|
| **Cloud** | `JevDecider` | `POST https://api.typesafe.ai/v1/systemone`, model `jev-latest`. Typed value + calibrated distribution, one forward pass, 0% malformed. Needs `TYPESAFE_API_KEY` (early-access). |
| **Local** | `LocalDecider` | A **llama.cpp server** + a **GBNF grammar** built from your options, so the model can only emit one of them. Offline, free, your box. |
| **Fake** | `FakeDecider` | Canned distribution, no model. For unit-testing the code that consumes a decision. |

## Run it

```bash
dotnet test                       # 14 hermetic tests, no network, no key
dotnet run --project samples/Sample.Cli -- fake
dotnet run --project samples/Sample.Cli -- jev      # export TYPESAFE_API_KEY=...
dotnet run --project samples/Sample.Cli -- local    # start a llama.cpp server first (below)
```

### Local server (the "make any model System One" path)

```bash
# any instruct GGUF you already have
llama-server -m qwen2.5-3b-instruct-q4_k_m.gguf --port 8080
export LOCAL_LLM=http://localhost:8080
```

`LocalDecider` sends your options as a grammar — `root ::= "billing" | "technical" | "sales"` —
so the output space *is* the options. The value cannot be anything else; confidence comes from the
first decision token's probability (`n_probs`). LM Studio / Ollama / vLLM expose the same idea via
`json_schema` / `grammar` / `guided_choice` — point `BaseAddress` there and adjust field names.

> **Guaranteed-valid vs. fully-calibrated.** `LocalDecider` does the guaranteed-valid path (one
> constrained generation → a valid value + a usable confidence), which is what routing and
> guardrail decisions actually need. For a calibrated probability over *every* option, score each
> option's sequence and softmax the log-likelihoods (Approach A) — a good next PR.

## Testing pattern

- **Unit** (fast, hermetic): `FakeDecider` for consumer logic; a stub `HttpMessageHandler` to test
  the real `JevDecider` / `LocalDecider` request-building and response-parsing with **no network**.
- **Live** (gated): `LiveTests` return early unless `TYPESAFE_LIVE=1` (+ key) or `LOCAL_LLM` is set.
  Assert on **calibrated confidence** and **batch accuracy**, not a single label — one-case model
  assertions are flaky.

## Notes

- `JevDecider`'s response parser handles the documented shapes defensively; Jev is early-access, so
  confirm the live payload and adjust `ParseChoice` if needed — or drop a community SDK
  (SystemOneDotNet / Jev.Net) behind this same `IDecider`.
- net8.0, no third-party runtime dependencies (just `System.Text.Json` / `System.Net.Http.Json`).

Companion write-up: **jocheojeda.com**.
