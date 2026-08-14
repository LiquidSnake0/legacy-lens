# Roadmap

Legacy Lens answers questions about code. The next milestones extend that in one
direction: **reading a codebase and reporting what is actually there.**

For M0 through M7, nothing here writes code. Editing agents exist and are built
by teams with years of head start. The gap worth filling is elsewhere: analysis
of inherited .NET, running locally, for organisations that are not allowed to
send their source anywhere.

M8 revisits that, narrowly. Not because the position was wrong, but because
Microsoft moved: it replaced a deterministic upgrade tool with a generative one
and shipped the failure mode this codebase exists to avoid. The scope stays
small on purpose, and the line is drawn at "the tool proposes a diff, a person
approves it".

A rule that holds across every milestone:

> **The tool never guesses what it can measure.**
> Roslyn and git supply the facts. The model turns them into sentences. Anything
> the model asserts on its own carries a citation, or it does not ship.

---

## M0. Hold up on a real codebase

*The prerequisite. Nothing below is demonstrable until this is true.*

**Done, with one thing that stays true: the first index is slow and cannot be
made fast on a CPU.**

Batching embedding requests made it 40% slower. Concurrency made it slower
still, at every thread count from two to eight, because one embedding already
saturates all eight cores. Both were measured rather than assumed, and both
were wrong.

What worked: tracking files by content hash so nothing is re-indexed, skipping
generated code entirely, and dropping files that vanish. A second run over an
unchanged repository takes 10 ms instead of 522 seconds; one edited file takes
1.2 seconds.

Still open: carrying that progress out of the log. The run now reports every
file it finishes, but the ingest endpoint answers only once the whole thing
returns, so anything watching from a browser still sees a request that hangs for
two hours. The log is enough for the person running it from a terminal, which is
who runs it today. Turning it into a stream is part of the background job in M7.

Now measured on a real one rather than estimated. See **Measured on Orchard**
below: 1.55 chunks per second, which puts a quarter of a million lines of C# at
roughly 1.7 hours on a CPU. The overnight-job prediction was right.

- ~~Index a genuinely old .NET Framework solution and measure honestly~~ done
- ~~Batch and parallelise embedding calls~~ tried, both made it slower
- ~~Incremental indexing~~ done, by content hash rather than mtime
- ~~Resumable ingestion with progress~~ done, by moving the write inside the
  loop. Each file is embedded, stored and recorded before the next one starts,
  so a crash costs the file in flight instead of the whole run, and each file
  logs where the run is and what the rate implies about the time left. The
  ledger already keyed files by content hash, so this needed no schema: the
  granularity to resume at was there before the ability to use it.

**Done when** a 1000-file solution indexes in minutes and re-indexes in seconds.
Half true: re-indexing is 10 ms, the first pass is still hours without a GPU.

---

## M1. The map ✅

*Point it at a solution, get the shape of the system back.*

**Done.** `POST /api/map`. 300,000 lines of nopCommerce in 219 ms, no compilation
involved. Two things the real corpus taught that guessing would not have:
project kind must come from the folder rather than the assembly references, and
a folder can share its name with a project inside it, which silently corrupts a
diagram if node and subgraph identifiers collide.

Shipped:

- Project dependency graph from `.csproj` references, resolved by name rather
  than by path, because relative paths in old solutions are frequently wrong
- Cycle detection
- Kind per project, from folder markers
- Findings: untested, oversized, orphaned, library coupled to web, unreadable
- Mermaid output, grouped by folder, with omissions stated rather than silent

Still open:

- Entry points: `Main`, controllers, WPF windows, hosted services
- Layer inference beyond folder names

---

## M2. The danger zone

*Where the code will hurt, before anyone touches it.*

**Done.** `POST /api/risk`. 1,731 files of nopCommerce ranked in 1.4 seconds.

Three things the corpus taught by being real rather than imagined: generated
code has to be excluded or it tops every chart, test files have to be excluded
or they rank as complex and untested, and a control character placed literally
in a source file degrades silently between editors, which broke the git parser
in a way no build error revealed.

**A defect found by measuring, not by reading.** The default history window is
24 months. Orchard has 11,873 commits and 119 of them fall inside it, roughly
0.26 commits per ranked file. The churn half of the score was measuring noise
while `HistoryStatus` cheerfully reported `Available`.

Comparing the two windows on the same repository:

| 24 months | full history |
|---|---|
| WebAppHosting.cs (3 commits) | OutputCacheFilter.cs (82 commits, 24 authors) |
| AdminController.cs (4) | CoreShapes.cs (79 commits, 18 authors) |
| AdminController.cs (9) | WebAppHosting.cs (55) |
| BlogPostAdminController.cs (3) | DefaultContentManager.cs (127 commits, 24 authors) |
| QueryPartDriver.cs (3) | DefaultDataMigrationInterpreter.cs (43) |
| OrchardLog4netLogger.cs (2) | DefaultContentQuery.cs (44) |

**One file in common out of six.** The left column puts a test-support file at
the top of the danger list. The right column names the core of the CMS, which is
what anyone who worked on Orchard would say.

The cause is structural rather than a bad constant: legacy code has stopped
changing, by definition, and a sliding window is calibrated for code that is
alive. Widening the default would only move the arbitrary line. The fix is to
adapt the window to the repository's actual activity, and to say so when the
window holds too few commits to rank anything.

Still open: coverage is inferred from file names. Knowing what a test actually
exercises needs resolved symbols, and requiring compilation would give up the
property that makes this usable on inherited code.

Crossing three signals that are all already on disk:

| Signal | Source |
|---|---|
| Size and complexity | Roslyn: lines, cyclomatic complexity, nesting depth |
| Change frequency | git log, over a window (see the defect above) |
| Test coverage | presence of tests referencing the type |

A file that is large, changed constantly, and untested is where the next
incident comes from. Every team suspects which files these are. Almost none can
name them with evidence.

- Rank files by the combination, not by any single metric
- Show the git history behind each verdict, so the ranking argues its own case
- Export as a table anyone can read

**Why second.** It is the one output that a non-technical reader understands
immediately, and it makes an argument rather than describing a state.

**Effort:** a weekend.

---

## M3. Hybrid search

*The retrieval gap, and the largest quality win available.*

**Done.** BM25 through SQLite's FTS5, merged with the cosine ranking by
reciprocal rank fusion. Existing indexes are backfilled on open rather than
re-embedded, which takes milliseconds instead of minutes.

Measured on this repository: `MaxChars` and `OverlapLines` returned nothing at
all before, and now return the file that defines them.

The design mistake worth remembering: the first version applied the cosine floor
to every chunk, including those both searches found. That discarded exactly what
the feature was added to rescue, since a chunk with a middling cosine score and
an exact term match is the case in question. The floor now applies only to
chunks the text search did not find.

Vector search is weak on rare identifiers. Someone typing `PriceEngine` wants
that exact token, and an embedding has no particular reason to favour an exact
match on a proper noun it never saw in training.

- BM25 through SQLite's built-in FTS5, so no new dependency
- Reciprocal rank fusion against the cosine ranking
- Recalibrate `MinimumScore` afterwards: the score distribution changes

**Why third rather than first.** It makes every answer better without being
visible in a demo. It matters most once the tool is used daily rather than
shown.

**Effort:** a weekend.

---

## M4. Class diagrams

*UML per module, extracted rather than imagined.*

**Done.** `POST /api/diagram`, by namespace or around one type.

The interesting problem turned out not to be rendering. Without resolved
symbols, `class A : B, IC` gives no way to tell a base class from an interface,
and the IFoo convention is broken constantly in old code. Resolved with a first
pass that records what the solution declares, so the naming convention is
consulted only for framework types.

Roslyn gives types, members, inheritance and interface implementations as facts.
The model's only job is grouping and naming the clusters.

- One diagram per namespace or per feature, not one unreadable diagram
- Inheritance and implementation from the symbol graph
- Mermaid `classDiagram` output

**Effort:** a weekend, most of it spent deciding what to leave out.

---

## M5. Demo comfort

*Small things that decide whether a demonstration lands.*

**Done.** Streaming, openable citations, `docker compose up` and nothing else.

- **Streaming.** Server-sent events, token by token. The citations go first, as
  their own event: retrieval finishes in about two seconds while generation is
  still starting, so most of the wait is now informative rather than blank.
- **Open a citation.** Served from the index rather than from disk. The stored
  text is what the model was given, and the file may have changed since;
  showing the current file would let a citation point at something the answer
  never saw.
- **A single command to start.** The stack now pulls its own models. The old
  healthcheck only proved Ollama answered, not that it had anything to answer
  with, so a clean start failed on the first question with a 404 from a model
  nobody had downloaded.

Two things found on the way that were not on the list:

- **Markdown was shown as punctuation.** Models answering about code write
  backticks and fenced blocks whether or not they were asked to, and rendering
  them raw made a correct answer look broken. Fences and inline code only, and
  bound as text rather than injected as HTML: a full markdown renderer means
  trusting model output with `innerHTML`.
- **A failure mid-stream cannot be an HTTP status**, the response has already
  started. It became an event, and the tokens that did arrive are kept rather
  than discarded.

**Effort:** an evening each, as estimated.

---

## Measured on Orchard

Everything above M5 was built against this repository and nopCommerce. To find
out what actually breaks at scale, the tool was pointed at **Orchard CMS**:
archived, .NET Framework, 89 projects, 6,203 files, 414,611 lines of which
258,589 are C#, 11,873 commits. Never compiled, which is the point.

| Operation | Time | Needs a model |
|---|---|---|
| Map: 89 projects, 319 dependencies, 50 findings | **0.38 s** | no |
| Risk ranking over 458 files, with git history | **1.73 s** | no |
| Semantic indexing, 1,976 chunks from 55,481 lines | **21.4 min** | yes |
| Same, extrapolated to 258,589 lines of C# | **~1.7 h** | yes |
| Embedding one question, per query | **57 ms** | yes |

Throughput was 1.55 chunks per second, flat across ten samples, on eight CPU
cores with no GPU. A machine with one is 20 to 50 times faster, so this is the
worst case rather than the typical one.

### The brute-force scan is not the wall

`SearchAsync` reads every vector and computes cosine in process. That was
expected to be the ceiling once several projects shared an instance. It is not.
Latency to the `sources` event, which is retrieval finishing. Indexes below
1,976 chunks were built by deleting rows from the real one; those above it by
duplicating real embeddings under new ids, so the vectors stay genuine and only
the count is synthetic:

| chunks | 250 | 1,000 | 4,000 | 16,000 | 32,000 | 64,000 |
|---|---|---|---|---|---|---|
| latency | 162 ms | 143 ms | 195 ms | 187 ms | 155 ms | 183 ms |

**256 times the data, no measurable change.** The scan is linear, at roughly
2.7 microseconds per chunk, but the constant is small enough that it would take
around 500,000 chunks, somewhere near 14 million lines, to cost a second. The
fixed costs dominate everything below that: 57 ms to embed the question, plus
HTTP.

So `sqlite-vec` and HNSW indexes are premature. They solve a problem this tool
does not have. **The wall is ingestion, and only ingestion.**

---

## M6. The deliverable

*The output that a buyer can put on a desk.*

The tool produces JSON and a web page. Neither is something a client keeps. What
consultancies sell for this problem is a document: what the system is, what will
hurt, in what order to fix it. They produce it by hand, in weeks, and it is stale
the month after.

- A generated assessment: shape, dependency cycles, dead weight, ranked risk,
  test gaps, with the evidence behind each claim
- Regenerated on every commit, so it stops being a snapshot
- Exportable, readable by someone who does not open an IDE

**Why this is the product.** Every number in it is measured. The model only
turns facts into sentences, which is the rule this roadmap opens with. It is
also the only output that survives contact with a non-technical reader.

**Why before the first-run flow.** The two milestones serve different readers.
The report is read by whoever pays; the onboarding form is used by whoever
installs. Those are the same person only once the tool is sold self-serve, and
it is not. Until then the operator is the one holding the keyboard, standing in
front of the codebase's owner, and what that meeting needs is the document, not
a smoother way for a stranger to start the thing unaccompanied. Building the
form first is building for a user who does not exist yet.

**Effort:** a weekend, most of it on what to leave out.

---

## M7. First run

*The gap between "a thing I built" and "a thing someone else can start".*

Today the tool assumes an index already exists and that whoever runs it knows
the curl commands. Everything below follows from one measured fact: the fast
half answers in two seconds and the slow half takes hours, so they cannot sit
behind the same button.

- **A form on first launch.** Where is the code: a local folder, or a public
  repository URL. Private repositories take a read-scoped token used for the
  clone and never stored. A tool whose argument is "your code stays yours" has
  no business holding your keys.
- **Answer immediately with the free half.** Map, risk ranking, findings, in
  seconds, before any model is involved. Semantic indexing becomes a background
  job the reader comes back to, not a spinner they watch.
- **Which model.** Local Ollama by default, or the user's own API key. Both
  keep the operator out of the loop. Hosting a shared instance is a different
  product with different obligations, and it is deliberately not this one.
- **Workspaces.** One index per project, selectable. This is the real work: a
  `workspace_id` on chunks and a schema migration. The form is an afternoon.

**Why it still ranks this high.** The measurements say the fast half is a
genuine product on its own and it is currently invisible. Answering immediately
with it is the one item here that does not wait for a self-serve audience: the
report in M6 wants the same split, so that piece gets built either way.

**Effort:** a weekend for workspaces, an afternoon for the form.

---

## M8. Mechanical migrations

*The transformations that are the same in every codebase.*

Legacy systems are each broken in their own way, so a push-button migration is a
promise that breaks on the third customer. But a large fraction of the work is
identical everywhere: `packages.config` to `PackageReference`, old csproj to
SDK-style, package version bumps, `ConfigurationManager` to `IConfiguration`.

Automate that fraction, one reviewable change at a time, and be explicit that
the rest needs a human. Every change lands as a diff a person approves, never
as a commit the tool makes on its own.

### How much of it is mechanical, measured

Counted on Orchard by reading project files, no model involved. The tool does
not compute this yet; this is the specification for what it should.

| | |
|---|---|
| Projects in the pre-SDK format | **89 of 89** |
| Using `packages.config` | **83** |
| Using `PackageReference` | **0** |
| Package references in total | **722** |

The format conversion is therefore the whole estate, and it is unambiguous
machine work. What it does **not** do is unblock the port:

| | |
|---|---|
| References with a path to modern .NET | 356 (**49%**) |
| References bound to `System.Web` | 366 (**51%**) |
| Projects held back by a dead end | **73 of 89** |

Four packages account for almost all of it, each present in 73 projects:
`Microsoft.AspNet.Mvc`, `Microsoft.AspNet.Razor`, `Microsoft.AspNet.WebPages`
and `Microsoft.Web.Infrastructure`. ASP.NET MVC 5 has no path to .NET 8, so
Orchard cannot be ported without rewriting its web layer.

**That is the finding, and it is the argument for this whole tool.** No amount
of automation resolves it. A person reads that table in seconds and decides:
convert the formats, isolate the sixteen projects that are clean, and have a
different conversation about the rest. The decision is the deliverable. It is
unreachable without the instruments.

One result worth recording because it was not expected: **zero version
divergence across 93 distinct packages, and a single hand-written
`bindingRedirect`.** Orchard is a *tended* legacy, not a rotten one. Those are
not the same job and must not carry the same estimate, so the scan has to tell
them apart before anyone quotes a price.

**The market says this is the opening.** Microsoft shipped GitHub Copilot app
modernization for .NET in September 2025 and deprecated the free .NET Upgrade
Assistant it replaced. The public complaints are specific: less deterministic
than the tool it replaced, partial upgrades leaving hundreds of hours of manual
repair, and NuGet package references that do not exist.

That last one is the failure mode this codebase is built against. The map, the
risk ranking and the diagrams read the disk. They cannot invent a package.

**Why last, and it is not a judgement on the idea.** M0 through M7 read. A
wrong reading is an embarrassing report, regenerated in seconds. This one
writes into someone else's system, where a wrong write is their production.
Same reason an autopilot is not installed on an aircraft whose instrument panel
is dark: the instruments are not a prerequisite to the automation, they are
what makes commanding it possible at all.

**Effort:** the mechanical conversion is bounded and knowable, a weekend. The
layer that decides *which* conversions to propose, and says plainly when the
answer is "this does not port", is the part that takes real time. Nothing here
ships before the surface a person commands it from.

---

## Deliberately out of scope

**Writing features, and open-ended refactoring.** Cursor, Copilot and aider do
this, with teams behind them. Competing there means losing quietly. M8 takes one
narrow slice of it, the transformations that are mechanical and identical across
every codebase, and nothing beyond that.

**Merging anything.** The tool proposes; a person commands. This is not a
limitation to be lifted later.

*Review* is the wrong word for it. A reviewer checks someone else's work after
the fact. The person here sets the course, reads the instruments throughout,
takes the controls at the moments that matter, and is inside the thing being
flown. A tool that upgrades a bank's framework unsupervised is a liability. The
person in command is the product.

**Cloud hosting.** The entire premise is that the code does not leave the
machine. A hosted version would contradict the one thing this tool offers, and
it would make the operator a data processor with the obligations that follow.
Running it *for* someone, on their behalf and on their premises, is a different
arrangement and does not require hosting anything.

**Languages beyond .NET, at first.** The chunker is already language-agnostic,
but Roslyn is not. Depth on one ecosystem beats a shallow pass over five.
