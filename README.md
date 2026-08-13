# Legacy Lens

Ask questions about a codebase nobody maintains any more.

Point it at a repository. It reads the source, indexes it, and answers questions
in plain language with a citation for every claim — file and line numbers you can
open and check.

```
> Where is the pricing calculated?

Pricing is computed in Billing/PriceEngine.cs (lines 84-131). The base rate comes
from the customer tier, then two discounts are applied in sequence: volume, then
contractual. The contractual discount is read from an XML file loaded at startup
in Startup.cs:47, which is why changing it requires a restart.

  Billing/PriceEngine.cs:84-131   (0.81)
  Billing/DiscountRules.cs:12-58  (0.74)
  Startup.cs:40-52                (0.69)
```

**Everything runs on your own machine.** The model is local. No source code is
sent to a third-party API. That is not a feature of the demo — it is the reason
this exists. No manufacturer is going to upload the control software for their
machines to a cloud provider.

---

## Why the citations matter

A language model asked about code it has not seen will produce a fluent,
confident, wrong answer. Retrieval-augmented generation exists to stop that: the
model only sees excerpts actually pulled from your repository, and every claim
carries the file and lines it came from.

If the answer looks wrong, you open the file and see it in ten seconds. That is
the whole design goal — the tool is not asking for trust, it is showing its work.

---

## How it works

```
  repository
      │
      ▼
  SourceWalker ──────►  files worth indexing (skips build output, binaries, vendored code)
      │
      ▼
  CodeChunker  ──────►  chunks that respect code structure, with line numbers kept
      │
      ▼
  EmbeddingClient ───►  a vector per chunk            (Ollama, local)
      │
      ▼
  VectorStore  ──────►  SQLite, on disk
                              │
  question ──► embed ──► cosine similarity ──► top K chunks
                                                    │
                                                    ▼
                                            PromptBuilder
                                                    │
                                                    ▼
                                              ChatClient  (Ollama, local)
                                                    │
                                                    ▼
                                            answer + citations
```

### On the vector store

Similarity search is a brute-force cosine scan over every chunk. This is a
deliberate choice, not a shortcut. A repository of 200k lines produces roughly
15-20k chunks; scanning them is a few milliseconds and the index is a single
SQLite file you can copy, inspect, and delete.

Approximate nearest-neighbour indexes (HNSW, IVF) start to pay for themselves
somewhere around a million vectors. Below that they add an dependency, a tuning
surface, and a recall penalty in exchange for nothing.

`IVectorStore` is an interface precisely so that swapping in Qdrant or pgvector
is a new class, not a rewrite. When the numbers justify it.

---

## Running it

Requirements: Docker, and roughly 6 GB of free RAM for the generation model.

```bash
cp .env.example .env
docker compose up -d
docker compose exec ollama ollama pull nomic-embed-text
docker compose exec ollama ollama pull qwen2.5-coder:3b
```

Index a repository:

```bash
curl -X POST localhost:8080/api/ingest \
     -H 'content-type: application/json' \
     -d '{"path":"/repos/my-project"}'
```

Ask it something:

```bash
curl -X POST localhost:8080/api/ask \
     -H 'content-type: application/json' \
     -d '{"question":"Where is authentication handled?"}'
```

Mount the repository you want to index by editing the `repos` volume in
`docker-compose.yml`.

### Choosing models

| Model | Size | Notes |
|---|---|---|
| `nomic-embed-text` | 274 MB | Embeddings. Fast on CPU, no reason to change it. |
| `qwen2.5-coder:1.5b` | ~1 GB | Generation on a constrained machine. Noticeably weaker. |
| `qwen2.5-coder:3b` | ~2 GB | Generation. The default. |
| `qwen2.5-coder:7b` | ~4.7 GB | Generation. Better, needs the RAM to match. |

Embedding is cheap and stays local in every configuration. Generation is the
expensive half, so `IChatClient` has an OpenAI-compatible implementation for
machines that cannot host a model — at the cost of the privacy guarantee above.
Set `CHAT_PROVIDER=openai` only when that trade is acceptable.

---

## Development

```bash
dotnet restore
dotnet test
dotnet run --project src/LegacyLens.Api
```

Requires the .NET SDK and Node. If `dotnet restore` fails on a package version,
run `dotnet add package <name>` to pin whatever your SDK resolves.

---

## Status

The pipeline is complete and unit-tested end to end: walking, chunking,
embedding, storage, retrieval, prompting, answering. 45 tests, no network and no
model involved, 80 ms for the suite.

What has **not** happened yet is a run against a real repository with a real
model loaded. Everything below the API boundary is verified; the quality of the
answers coming out of it is not, because that is measured by using it rather
than by asserting on it. The retrieval score floor in particular is a placeholder
until it has been calibrated against a real index — see [docs/NEXT.md](docs/NEXT.md).

No frontend yet.

## Licence

MIT
