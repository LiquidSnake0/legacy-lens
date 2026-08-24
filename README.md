# Legacy Lens

Ask questions about a codebase nobody maintains any more.

Point it at a repository. It reads the source, indexes it, and answers questions
in plain language with a citation for every claim: file and line numbers you can
open and check.

![Legacy Lens answering a question about its own source, with citations](docs/screenshot.png)

*Answering a question about its own source. Every claim carries the file and lines
it came from, with the retrieval score beside it.*

Or from the command line:

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
sent to a third-party API. That is not a feature of the demo. It is the reason
this exists. No manufacturer is going to upload the control software for their
machines to a cloud provider.

---

## Why the citations matter

A language model asked about code it has not seen will produce a fluent,
confident, wrong answer. Retrieval-augmented generation exists to stop that: the
model only sees excerpts actually pulled from your repository, and every claim
carries the file and lines it came from.

Retrieval runs two searches. **Vector search** finds excerpts that mean the same
thing as the question. **Full-text search** finds the ones containing the exact
term, which matters because embeddings are weak on rare identifiers: someone
typing `PriceEngine` wants that token, and a model that never saw the name in
training has no reason to favour an exact match on it.

The two rankings are merged by reciprocal rank fusion, on position rather than
score, since cosine similarity and BM25 share no unit. Each citation states
which search found it, and a chunk found only by term carries no similarity
score, because none was computed and inventing one would be the exact failure
this project is built around.

If the answer looks wrong, you open the file and see it in ten seconds. That is
the whole design goal: the tool is not asking for trust, it is showing its work.

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
deliberate choice, not a shortcut, and it has now been measured rather than
asserted.

Orchard CMS, 55,481 lines of C#, produces 1,976 chunks, so roughly one chunk per
28 lines. Query latency was measured against indexes from 250 to 64,000 chunks,
the larger ones built by duplicating real embeddings under new ids so that only
the count varies:

| chunks | 250 | 1,000 | 4,000 | 16,000 | 32,000 | 64,000 |
|---|---|---|---|---|---|---|
| latency | 162 ms | 143 ms | 195 ms | 187 ms | 155 ms | 183 ms |

256 times the data, no measurable difference. The scan is linear at about 2.7
microseconds per chunk, but embedding the question costs 57 ms on its own and
dominates everything below roughly 500,000 chunks, which is somewhere near 14
million lines of code.

Approximate nearest-neighbour indexes (HNSW, IVF) start to pay for themselves
well past that. Below it they add a dependency, a tuning surface, and a recall
penalty in exchange for nothing. The index stays a single SQLite file you can
copy, inspect, and delete.

`IVectorStore` is an interface precisely so that swapping in Qdrant or pgvector
is a new class, not a rewrite. When the numbers justify it.

---

## Mapping a solution

Answering questions needs a model. Understanding the *shape* of a solution does
not, and should not: the project files and the folder layout state it outright.

```bash
curl -X POST localhost:8080/api/map \
     -H 'content-type: application/json' \
     -d '{"path":"/repos/my-solution","minimumLines":3000}'
```

No compilation, no NuGet restore, no MSBuild. That is the point. The moment you
most need to understand an inherited solution is the moment it does not build,
because a package is gone or the SDK is not installed. Tools built on the
compiler are useless exactly when they would help.

### What it produces

Run against [nopCommerce 3.90](https://github.com/nopSolutions/nopCommerce), an
ASP.NET MVC application on .NET Framework 4.5: **31 projects, 2005 files,
300,073 lines, analysed in 219 ms.**

```mermaid
graph LR
  subgraph gLibraries["Libraries"]
    nNop_Services["Nop.Services<br/>77,151 lines"]
    nNop_Core["Nop.Core<br/>20,678 lines"]
  end
  subgraph gNop_Web["Nop.Web"]
    nNop_Admin["Nop.Admin<br/>47,147 lines"]
  end
  subgraph gPlugins["Plugins"]
    nNop_Plugin_Shipping_Fedex["Nop.Plugin.Shipping.Fedex<br/>23,231 lines"]
  end
  subgraph gPresentation["Presentation"]
    nNop_Web["Nop.Web<br/>79,458 lines"]
    nNop_Web_Framework["Nop.Web.Framework<br/>8,447 lines"]
  end

  nNop_Web --> nNop_Core
  nNop_Web --> nNop_Services
  nNop_Web --> nNop_Web_Framework
  nNop_Admin --> nNop_Core
  nNop_Admin --> nNop_Services
  nNop_Admin --> nNop_Web_Framework
  nNop_Web_Framework --> nNop_Core
  nNop_Web_Framework --> nNop_Services
  nNop_Services --> nNop_Core
  nNop_Plugin_Shipping_Fedex --> nNop_Core
  nNop_Plugin_Shipping_Fedex --> nNop_Services
  nNop_Plugin_Shipping_Fedex --> nNop_Web_Framework

  classDef web fill:#dbeafe,stroke:#2563eb,color:#1e3a5f
  classDef library fill:#f1f5f9,stroke:#64748b,color:#1e293b
  class nNop_Web,nNop_Admin,nNop_Plugin_Shipping_Fedex web
  class nNop_Web_Framework,nNop_Services,nNop_Core library

  %% 25 project(s) omitted: under 8,000 lines, or test projects
```

Alongside the diagram, the findings:

| Finding | Count | Example |
|---|---|---|
| Untested | 20 | No test project references `Nop.Plugin.Payments.PayPalStandard` |
| Oversized | 5 | `Nop.Web` holds 79,458 lines in one project |
| Library coupled to web | 4 | `Nop.Core` depends on `System.Web.Mvc` |
| Orphan | 1 | Nothing references `Nop.Plugin.ExchangeRate.EcbExchange` |

The third one is the interesting kind. A library named Core that depends on
`System.Web.Mvc` cannot be unit tested without a web context and cannot be
reused from a service or a desktop client. Nothing about the file layout says
so; nothing about the build complains. It surfaces the day someone tries.

### Where the code will hurt

```bash
curl -X POST localhost:8080/api/risk \
     -H 'content-type: application/json' \
     -d '{"path":"/repos/my-solution"}'
```

Three signals, all already on disk: complexity from Roslyn's parser, change
frequency from git, test coverage by naming convention. 1,731 files of
nopCommerce ranked in 1.4 seconds.

| Score | File | Why |
|---|---|---|
| 1.33 | `Administration/Controllers/ProductController.cs` | 3,800 lines, `PrepareProductModel` at complexity 36, nested 8 deep, untested |
| 1.33 | `Nop.Services/Catalog/ProductService.cs` | `SearchProducts` at complexity 101, nested 9 deep, untested |
| 1.27 | `Administration/Controllers/CustomerController.cs` | `Edit` at complexity 56, untested |

A cyclomatic complexity of 101 means covering that method's branches would take
101 tests. That number is the argument; the score beside it is only a sort key.

**The ranking uses the geometric mean of structure and churn**, not the average.
A file has to score high on both to reach the top: complicated but never touched
is not urgent, and touched constantly but trivial is not dangerous. Averaging
would let either one alone carry a file up.

**Generated code is excluded.** nopCommerce holds a WSDL proxy with 1,944
methods that topped every chart until it was filtered out, and told the reader
nothing they could act on.

**When git history is unavailable, the report says so.** A shallow clone and a
codebase where nothing ever changes produce the same empty result. Reporting
"nothing changes here" when the truth is "I could not look" is exactly the
confident wrong answer this project exists to avoid.

### Class diagrams

```bash
curl -X POST localhost:8080/api/diagram \
     -H 'content-type: application/json' \
     -d '{"path":"/repos/my-solution","type":"IShippingRateComputationMethod"}'
```

Either every type in a namespace, or one type and its immediate neighbours.
Never the whole solution: nopCommerce declares 1,746 types once generated and
test code is excluded, and a diagram of all of them is a grey rectangle.

```mermaid
classDiagram
  class AustraliaPostComputationMethod {
    +GetShippingOptions()
    +GetFixedRate()
    +GetConfigurationRoute()
    +4 more
  }
  class CanadaPostComputationMethod {
    +ShippingRateComputationMethodType
    +ShipmentTracker
    +GetShippingOptions()
    +4 more
  }
  class FedexComputationMethod {
    +GetShippingOptions()
    +GetFixedRate()
    +GetConfigurationRoute()
    +4 more
  }
  class FixedOrByWeightComputationMethod {
    +GetShippingOptions()
    +GetFixedRate()
    +GetConfigurationRoute()
    +4 more
  }
  class IPlugin {
    <<interface>>
  }
  class IShippingRateComputationMethod {
    <<interface>>
  }
  class UPSComputationMethod {
    +GetShippingOptions()
    +GetFixedRate()
    +GetConfigurationRoute()
    +4 more
  }
  class USPSComputationMethod {
    +GetShippingOptions()
    +GetFixedRate()
    +GetConfigurationRoute()
    +4 more
  }

  IPlugin <|-- IShippingRateComputationMethod
  IShippingRateComputationMethod <|.. FixedOrByWeightComputationMethod
  IShippingRateComputationMethod <|.. USPSComputationMethod
  IShippingRateComputationMethod <|.. CanadaPostComputationMethod
  IShippingRateComputationMethod <|.. AustraliaPostComputationMethod
  IShippingRateComputationMethod <|.. UPSComputationMethod
  IShippingRateComputationMethod <|.. FedexComputationMethod

  %% 6 relation(s) to types outside around IShippingRateComputationMethod not drawn
```

**The hard part is not drawing, it is knowing what a base type is.** In
`class A : B, IC` the compiler knows B is a class and IC an interface. A syntax
tree does not, and guessing from the leading capital I is a convention that
legacy code breaks constantly.

So it runs in two passes. The first records every type the solution declares and
its shape. The second resolves base lists against that table, and falls back to
the naming convention only for types the solution does not define, where nothing
better exists. C# requires the base class first, so anything after position zero
is an interface with certainty regardless.

The types that could not be resolved are listed rather than hidden: they mark
where the solution's own graph stops and the framework begins.

### What it refuses to guess

Project kind is decided by what sits in the folder, not by which assemblies are
referenced. Assembly references lie: `Nop.Core` references `System.Web.Mvc` and
is a class library all the same. A `web.config` next to a `Views` folder does
not lie.

That distinction is the rule the whole analysis follows:

> **The tool never guesses what it can measure.**
> The project files and git supply the facts. The model turns them into
> sentences, and never the other way round.

---

## The assessment

Everything above answers one question each, as JSON. None of it is something a
client keeps. What gets bought for this problem is a document: what the system
is, what will hurt, and in what order to deal with it.

```bash
dotnet run --project src/LegacyLens.Api -- report /repos/my-solution > assessment.md
```

or, against a running instance:

```bash
curl -X POST localhost:8080/api/report \
     -H 'content-type: application/json' \
     -d '{"path":"/repos/my-solution"}' -o assessment.md
```

Markdown comes back as the body rather than as a field inside JSON, because it
is meant to be written to a file, converted to PDF or pasted into a ticket.

**Orchard CMS, 414,611 lines, assessed in 2 seconds.** The document opens with
the paragraph for a reader who will read one paragraph:

> orchard is 89 projects and 414,611 lines across 6,203 source files. Everything
> targets v4.8. No test project references 45 of the 78 projects that ship code,
> covering 75,908 lines. 449 files were ranked on structure and change frequency
> together. The one at the top is
> `src/Orchard.Web/Core/Contents/Controllers/AdminController.cs`. 89 of 89
> project files are in the pre-SDK format. 73 projects reference packages that
> exist only inside the .NET Framework, which no conversion tool will fix; 16
> others are convertible as they stand.

Then the shape of the solution and its diagram, the findings, the ranked files
with the reasons beside them, what a move to modern .NET runs into, and the
order of work: what blocks everything else, what a tool does without anyone
having to decide, what needs a decision, and what may cost nothing at all.

**No model is involved.** The plan was for one to turn the facts into sentences.
It turned out not to be needed: every sentence is a template filled with a
measured number. Nothing in the document can be invented because nothing in it
is generated, which is the only answer worth giving to the question a buyer asks
about any generated document. It also means the whole thing costs nothing to
run, so CI regenerates it on every commit instead of it being a snapshot someone
produced once.

**The order carries no days.** Nothing here measured how fast anyone works, so
the sequence is a dependency order, which is a property of the codebase, rather
than a schedule, which is a property of the team.

**What it could not see is printed in the document**, not left in the source for
the reader to find out afterwards: that nothing was compiled, that test coverage
is a naming convention which over-reports, and how many packages are
unclassified rather than quietly assumed to be fine.

---

## Characterization tests

The risk ranking names the files that will hurt and stops there. The answer to
"this file is complicated, changes constantly and nothing tests it" is to put a
test on it, and that is the one thing nobody does on inherited code: writing a
test means knowing what the code is supposed to do, and on legacy that knowledge
left with whoever had it.

A characterization test does not need it. It records what the code *does*, not
what it should do.

```bash
dotnet run --project src/LegacyLens.Api -- characterize \
    src/LegacyLens.Analysis/bin/Debug/net10.0/LegacyLens.Analysis.dll \
    --type CodeMetrics --out ./characterization
```

What comes out has been called, watched twice, written down, compiled and run:

```csharp
[Fact]
public void MeasureFile_1()
{
    var subject = new global::LegacyLens.Analysis.CodeMetrics();
    Assert.Throws<global::System.ArgumentException>(() => subject.MeasureFile(""));
}
```

**Nothing is offered that did not pass.** A characterization test is true if and
only if it passes against the code as it stands, so the tool compiles the file
and runs every assertion in it before showing anyone the result. Whatever fails
is dropped with a reason. That is the whole argument for generating this kind of
code and no other: the machine settles the question by itself, so no one is
asked to review an assertion on trust.

**Two identical calls have to agree.** A method reading the clock, a GUID or a
random number produces a test that passes today and fails tomorrow morning.
Every call is made twice and the result is discarded when the two disagree.

**It will happily freeze a bug.** The generated file says so at the top. This
net guarantees a migration changed nothing; it says nothing about whether what
exists is right.

**The refusals are the interesting output.** On this repository's own analysis
assembly it examined 402 members, could call 11, and kept 44 tests. The other
391 were property accessors and generated members, types it could not construct,
parameters it has no values for, and methods returning void. On modern code that
ratio is poor by construction, and whether it inverts on a real .NET Framework
estate is untested.

**It runs the code**, which is the opposite of everything above, and that turns
out to cost less than expected. Pointed at the four managed .NET Framework
assemblies Orchard ships in `lib/`, this runtime loads all four and produces 35
tests, the best of them from `MSBuild.Community.Tasks` at 25. Modern .NET loads
Framework assemblies as long as the members being touched do not reach an API
that is gone.

What fails is later and quieter: reflection resolves signatures on demand, so an
assembly loads and then throws `FileNotFoundException` when a return type turns
out to live somewhere absent. That is reported as its own reason, and
deliberately never recorded as behaviour: a test asserting that the code throws,
when the truth is that a dependency was not deployed, would fail on every
machine that has it.

It is a command and never an HTTP route, because loading an assembly and calling
into it from a web request is remote code execution with a JSON body.

---

## Running it

Requirements: Docker, and roughly 6 GB of free RAM for the generation model.

```bash
docker compose up -d
```

That is the whole thing: it starts Ollama, pulls both models, starts the API on
`localhost:8080` and the interface on `localhost:4200`.

The first run downloads several gigabytes of model weights and takes a while.
They go into a named volume, so it happens once rather than on every start.
`docker compose logs -f models` shows the progress.

Everything has a working default. To change the models, or to index code that
lives outside this directory, `cp .env.example .env` and edit it.

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

By default it indexes whatever is in `./repos`. Set `REPOS_PATH` in `.env` to
mount a directory from elsewhere instead.

### More than one project

Both calls above take an optional `workspace`. Left out, they use `default`,
which is also where an index built before workspaces existed ends up.

```bash
curl -X POST localhost:8080/api/workspaces \
     -H 'content-type: application/json' \
     -d '{"name":"Billing","rootPath":"/repos/billing"}'
```

That returns an id to pass as `"workspace"` when indexing and when asking.
`GET /api/workspaces` lists them with their chunk counts, and
`DELETE /api/workspaces/{id}` removes one along with everything indexed under
it. Two projects in one index file cannot see each other's code, including when
they contain files at the same relative path.

### The web interface

`docker compose up` serves it on `http://localhost:4200`. To run it against a
locally built API instead:

```bash
cd web
npm install
npm start
```

Same address either way, which is also the origin the API allows by default;
change `CORS_ORIGIN` if you serve the frontend from somewhere else.

The answer streams in token by token, because generation on a CPU takes tens of
seconds and a blank screen for that long reads as a crash. The citations appear
before the first word: retrieval finishes long before generation does, and
clicking one shows the text the answer was written from.

Angular 22, standalone components, signals for local state, reactive forms for
the question. It talks to the same HTTP API as the curl commands above, so
neither side knows about the other beyond the JSON contract.

### Choosing models

| Model | Size | Notes |
|---|---|---|
| `nomic-embed-text` | 274 MB | Embeddings. Fast on CPU, no reason to change it. |
| `qwen2.5-coder:1.5b` | ~1 GB | Generation on a constrained machine. Noticeably weaker. |
| `qwen2.5-coder:3b` | ~2 GB | Generation. The default. |
| `qwen2.5-coder:7b` | ~4.7 GB | Generation. Better, needs the RAM to match. |

Embedding is cheap and stays local in every configuration. Generation is the
expensive half, so `IChatClient` has an OpenAI-compatible implementation for
machines that cannot host a model, at the cost of the privacy guarantee above.
Set `CHAT_PROVIDER=openai` only when that trade is acceptable.

---

## Indexing at scale

Embedding is the expensive half of this system, by an enormous margin.

| Corpus | Files | Chunks | First index |
|---|---|---|---|
| This repository | 65 | 423 | 8 minutes |
| nopCommerce 3.90 | 2,639 | 16,061 | roughly 2 hours |

Those numbers are on a laptop CPU with no GPU, at about 470 ms per chunk.

**Two optimisations that look obvious and do not work.** Ollama accepts batched
embedding requests, and batching made it 40% slower. Issuing requests
concurrently made it slower still, at every thread count from two to eight. A
single embedding already saturates all eight cores, so both changes only added
contention. Measured before being believed, and worth recording so nobody tries
again.

**What does work is not re-indexing.** Files are tracked by a hash of their
content, so a second run over an unchanged repository does nothing:

```
first run          65 files, 423 chunks    522 s
second run         65 files,   0 chunks     10 ms
one file edited    65 files,   2 chunks    1.2 s
```

Content hash rather than modification time, because a checkout, a branch switch
or a restored backup all rewrite timestamps without changing a line.

**Generated code is not indexed at all.** A WSDL proxy answers no question
anyone asks, and embedding twelve thousand lines of it costs minutes that buy
nothing.

**Files that disappear are dropped.** Nothing else would ever mention them
again, so their chunks would sit in the index answering questions with code that
no longer exists.

The first index of a large solution still takes as long as it takes. On CPU that
is irreducible without a GPU, and pretending otherwise would be the kind of
claim this project is built to avoid.

---

## Development

```bash
dotnet test                                   # 219 tests, no network, ~3 s
dotnet run --project src/LegacyLens.Api

cd web && npm test                            # 5 tests, no network, ~1 s
```

Requires the .NET 10 SDK and Node 20+.

---

## Status

Working, in two halves.

**Structural analysis** reads project files and folder layout, involves no model
at all, and answers in milliseconds: 300,000 lines of nopCommerce in 219 ms.

**Question answering** needs an index and a local model. 219 unit tests covering
every layer, no network, no model, 400 ms for the suite, and the pipeline has
been run end to end against a real repository: this one.

**The assessment** sits on the first half and inherits its speed: no model, no
index, no compilation, and a 414,611-line solution documented in two seconds.

Indexing its own 21 source files produced 58 chunks in 48 seconds on a laptop
CPU, and it answers questions about itself correctly, citing lines that hold
what the answer claims.

The retrieval floor was set by measurement rather than intuition, the method
and the numbers are in `Retriever.MinimumScore`. On seven questions it now
answers the four that the code covers and declines the three it does not.

Known gaps for the question-answering half, in order of impact: no reranking,
and a first index that cannot be made fast on a CPU. See
[docs/NEXT.md](docs/NEXT.md) for the detail,
and [docs/ROADMAP.md](docs/ROADMAP.md) for where this is going.

## Licence

MIT
