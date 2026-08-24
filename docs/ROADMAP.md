# Roadmap

Legacy Lens answers questions about code. The next milestones extend that in one
direction: **reading a codebase and reporting what is actually there.**

For M0 through M7, nothing here writes code. Editing agents exist and are built
by teams with years of head start. The gap worth filling is elsewhere: analysis
of inherited .NET, running locally, for organisations that are not allowed to
send their source anywhere.

The last two milestones write, and they are ordered by what a wrong output
costs. **M8 writes tests, never production code**: a generated test that is
wrong fails on the spot and is discarded before anyone sees it, so the machine
checks its own work and no human is asked to trust anything. **M9 writes into
the code itself**, which is why it comes last and why the line is drawn at "the
tool proposes a diff, a person approves it". M9 is also the one concession to
the position above, and Microsoft is the reason: it replaced a deterministic
upgrade tool with a generative one and shipped the exact failure mode this
codebase exists to avoid.

A rule that holds across every milestone:

> **The tool never guesses what it can measure.**
> Roslyn and git supply the facts. The model turns them into sentences. Anything
> the model asserts on its own carries a citation, or it does not ship.

---

## M0. Hold up on a real codebase ✅

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

## M2. The danger zone ✅

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

## M3. Hybrid search ✅

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

## M4. Class diagrams ✅

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

## M5. Demo comfort ✅

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

## M6. The deliverable ✅

*The output that a buyer can put on a desk.*

**Done.** `POST /api/report`, or `report <path>` on the command line, produces a
markdown assessment of a solution in seconds. Orchard, 414,611 lines, takes two.

The tool produced JSON and a web page. Neither is something a client keeps. What
consultancies sell for this problem is a document: what the system is, what will
hurt, in what order to fix it. They produce it by hand, in weeks, and it is stale
the month after.

- ~~A generated assessment~~ done: shape, findings, ranked risk, the migration
  survey and the evidence behind each claim
- ~~Regenerated on every commit~~ done, as a CI job that fails when the document
  cannot be produced
- ~~Exportable, readable by someone who does not open an IDE~~ done: markdown,
  which renders on its own and converts to both HTML and PDF

**The model was left out, deliberately.** The plan was for it to turn facts into
sentences. It is not needed to: every sentence in the document is a template
filled with a measured number, which is a stronger answer to the only question a
buyer asks about a generated document. Nothing in it can be hallucinated because
nothing in it was generated. It also costs nothing to run, which is what lets it
regenerate on every commit rather than once per quarter.

**Composing the four analyses found a fault none of them could see alone.** The
risk ranking drops files whose own name looks like a test, which is all it can
do with the file in front of it. Orchard's top-ranked file was
`Orchard.Specs/Bindings/WebAppHosting.cs`, support code inside a test project
that no naming convention identifies. Naming a test fixture as the most
dangerous file in someone's product discredits every other row in the table. The
report knows which folders belong to a test project, because it holds the map
and the ranking does not, so it drops them. That is the argument for composing
these rather than printing them side by side.

**Three smaller things the first real run exposed.** The diagram had no bound on
its size and drew 56 boxes for Orchard, a picture that renders as a wall; it now
raises its own line threshold until it fits, and says in prose what fell off,
since a Mermaid comment is rendered by nothing. A file could rank high without
any single reason crossing the threshold that earns a sentence, leaving a blank
cell that reads as a hole in the analysis. And the paragraph about package drift
asserted that versions disagreed, on a solution with 279 hand-written binding
redirects and no divergent version anywhere: true of most drifted legacies,
false of this one, and contradicted by the table directly above it.

**Why before the first-run flow.** The two milestones serve different readers.
The report is read by whoever pays; the onboarding form is used by whoever
installs. Those are the same person only once the tool is sold self-serve, and
it is not. Until then the operator is the one holding the keyboard, standing in
front of the codebase's owner, and what that meeting needs is the document, not
a smoother way for a stranger to start the thing unaccompanied. Building the
form first is building for a user who does not exist yet.

**Still open: the order of work carries no cost.** It is a dependency order, and
it says so, because nothing here measured how fast anyone works. A reader who
wants a price still has to supply the one number this tool cannot.

**Effort:** an evening rather than a weekend, because the four analyses
underneath were already right. Most of it went on what to leave out.

---

## M7. First run ✅

*The gap between "a thing I built" and "a thing someone else can start".*

Today the tool assumes an index already exists and that whoever runs it knows
the curl commands. Everything below follows from one measured fact: the fast
half answers in two seconds and the slow half takes hours, so they cannot sit
behind the same button.

- **A form on first launch ✅.** Where is the code: a local folder, or a public
  repository URL. Private repositories take a read-scoped token used for the
  clone and never stored. A tool whose argument is "your code stays yours" has
  no business holding your keys.
- **Answer immediately with the free half ✅.** Map, risk ranking, findings, in
  seconds, before any model is involved. Semantic indexing becomes a background
  job the reader comes back to, not a spinner they watch.
- **Which model ✅.** Local Ollama by default, or the user's own API key. Both
  keep the operator out of the loop. Hosting a shared instance is a different
  product with different obligations, and it is deliberately not this one.
- **Workspaces ✅.** One index per project, selectable. This is the real work: a
  `workspace_id` on chunks and a schema migration. The form is an afternoon.

**Workspaces, done.** Every chunk, every full-text row and every ledger entry
carries the project it belongs to, and all six store operations are scoped by
it. `GET/POST/DELETE /api/workspaces` manage them; `/api/ingest` and `/api/ask`
take one; `/api/health` counts per project rather than reporting a single number
over a file holding three.

The part worth writing down is the collision. A chunk id is its file path and
its start line, so two projects that each contain a `src/A.cs` produce the same
id for two unrelated pieces of code. Keyed on the id alone, the second index
silently overwrote the first, and the same held for the full-text table, which
kept one searchable row holding whichever text was written last. Both are now
keyed on the pair, which SQLite cannot express as an alteration, so an index
written before this migrates by being rebuilt once. An existing index is carried
into a workspace named for what it is rather than refused, because refusing
throws away hours of embedding that are still perfectly good.

Thirteen tests cover it against a real SQLite file, including an index
hand-written in the shape it had before workspaces existed, since the migration
has to survive files that no code in the repository can still produce. Removing
the composite key fails six of them.

One fault only the running instance found: deleting a workspace that had never
been indexed hit a ledger table that no ingestion had yet created. The tests all
indexed something first and so created it on the way past. The schema is now
brought up at startup rather than by the first ingestion.

**What is left here:** the launch form, answering immediately with the fast half
while indexing runs behind it, and the model choice.

**Why it still ranks this high.** The measurements say the fast half is a
genuine product on its own and it is currently invisible. Answering immediately
with it is the one item here that does not wait for a self-serve audience: the
report in M6 wants the same split, so that piece gets built either way.

**Effort:** spent.

---

## M8. Characterization tests ✅

*A net under the code, before anyone moves it.*

**Done, and the measurement is less flattering than the idea.**
`characterize <assembly.dll> [--type <name>] [--out <directory>]` calls a
compiled assembly, watches what it does, writes the observations out as xUnit
tests, compiles them, runs them, and keeps only what passed.

M2 names the files that will hurt and stops there. The honest answer to "this
file is complicated, changes constantly and nothing tests it" is to put a test
on it, and that is exactly what nobody does on inherited code: writing a test
means knowing what the code is supposed to do, and on legacy the intent is gone
along with whoever held it.

A characterization test does not need the intent. It records what the code
*does*, not what it should do. That sounds like a weak test, and it is precisely
the right one before a migration: any change that alters observable behaviour
breaks it, which is the only guarantee anyone actually wants when moving code
they do not understand.

**Why this does not break the rule at the top.** It is the one kind of generated
code whose correctness the machine can settle on its own. A characterization
test is true if and only if it passes against the code as it stands today. So it
is compiled and run before it is offered, and one that fails is thrown away
rather than shown. The model proposes, the compiler and the test runner decide,
and a person only ever reads output that has already survived both. Nothing is
asserted on trust.

**What it is not.** It is not a way to produce correct tests. A characterization
test will happily freeze a bug in place, and the output has to say so: this net
guarantees that a migration changes nothing, not that what exists is right. A
tool that blurred those two would be selling a false sense of safety, which is
worse than no net at all.

- Target the files M2 already ranks, not the repository. The point is a net
  under the dangerous parts, not coverage as a number to report
- Generate, compile, run, and discard silently whatever does not pass
- Write into a new test project only. Nothing this milestone produces ever
  lands in production code
- Report what could not be covered and why. Static dependencies, direct I/O,
  clocks and randomness are where real legacy resists, and naming them is more
  useful than a coverage figure that hides them

**Why before the migrations.** A mechanical migration without a net is a bet
that the transformation was faithful. With one, it is a claim anyone can check
by running the suite. The order is Feathers' and it has not been improved on:
put the code under test, then change it.

### Measured on this repository

Pointed at `LegacyLens.Analysis`, 402 members examined:

| | |
|---|---|
| Methods it could call | **11** |
| Tests kept, all compiled and passing | **44**, in 6 files |
| Property accessors, operators, generated members | 275 |
| Needing an instance it could not build | 55 |
| Taking a parameter it has no values for | 43 |
| Returning void | 18 |
| Time | 4.2 s |

**Eleven methods out of four hundred is the finding, and it is a small
number.** Modern code is the reason: records generate accessors and equality
members by the dozen, dependencies arrive through constructors rather than being
reachable, and parameters are domain types rather than integers. Everything this
milestone can reach is a static or default-constructible method taking
primitives, which is a description of exactly the code that is *already* easy to
test.

Whether the ratio inverts on real legacy is the open question and it is not
answered here. The prior is that it improves: the code this tool exists for is
full of large static helpers taking strings and integers, which is the shape it
handles. But that is a guess, and this section will say so until someone runs it
on a .NET Framework estate.

### The wall is real, and it is not where this section first put it

Characterization needs to *run* the code, which is the opposite of every other
milestone here. From that, this section originally predicted that a .NET
Framework assembly would not load on Linux at all and that the whole capability
needed a Windows host.

**Measured, that prediction is wrong.** Orchard ships four managed .NET
Framework assemblies in `lib/`, and this runtime loads them:

| Assembly | Callable | Tests kept |
|---|---:|---:|
| `MSBuild.Community.Tasks` | 7 | **25** |
| `Microsoft.Web.XmlTransform` | 2 | **6** |
| `System.Data.SqlServerCe` | 31 | **4** |
| `SlowCheetah.Xdt` | 0 | 0 |

Modern .NET loads a Framework assembly perfectly well as long as the members
being touched do not reach an API that is gone. Nothing fails at load time.

**What actually fails is lazier and nastier.** Reflection resolves signatures on
demand, so an assembly loads, and then the first read of a return type throws
`FileNotFoundException` for something it references.
`MSBuild.Community.Tasks` did exactly that, on `Microsoft.Build.Framework`, and
it took the whole run down with an unhandled exception before this was fixed.
Handled, the same assembly is the best result of the four: 25 tests, and 5
members reported as needing an assembly that is not on this machine.

That failure also had to be separated from real behaviour. A missing assembly
surfaces as an exception, which is indistinguishable from code that genuinely
throws, and pinning one would produce a test asserting that the code fails,
when what happened is that a dependency was not deployed. Those tests would then
fail on any machine that has it.

**So the honest statement is narrower than the prediction:** what cannot be
characterized here is whatever reaches an API that modern .NET dropped, member
by member, and the run says how many those were. A Windows host widens the
surface; it is not the entry ticket this section claimed it was.

**Still not verified:** no assembly of Orchard's own was tried, because the
repository holds source and never built binaries. The four measured here are its
third-party dependencies.

**Effort:** an evening, against a guess of a weekend. The generation was the
easy half; deciding what to refuse was the work.

---

### Composite arguments

A method taking anything but a primitive produced no test at all: there was no
value to call it with, and it was counted as `ParameterTypeNotSupported`.

Plain data types are now built from the same primitive values as everything
else and written back out as an object initialiser, so what appears in the test
file rebuilds the object the method was called with. Depth is capped at two,
framework types are refused, and a type without a parameterless constructor or
without settable properties is still counted rather than guessed at.

Finding it required a second defect to surface: `CanSupply` kept its own list of
supported types, a copy of what `Values.For` already knew, and the two drifted
the moment composites existed. The list is gone and the question is asked of
the one place that can answer it.

**The gain is unmeasured on real legacy, and the one measurement available says
zero.** Run over this repository's own `Analysis` assembly, the number of
methods accepted is 71 before and 71 after. Its types are records with primary
constructors and no settable properties, which is the opposite of what this
targets and a reminder that a tool's own codebase is a poor sample of the
estate it is pointed at. The shape it does handle, a mutable class with a
parameterless constructor, is covered end to end by a generated suite that
compiles and runs.

---

## M9. Mechanical migrations 🟡

*The transformations that are the same in every codebase.*

Legacy systems are each broken in their own way, so a push-button migration is a
promise that breaks on the third customer. But a large fraction of the work is
identical everywhere: `packages.config` to `PackageReference`, old csproj to
SDK-style, package version bumps, `ConfigurationManager` to `IConfiguration`.

Automate that fraction, one reviewable change at a time, and be explicit that
the rest needs a human. Every change lands as a diff a person approves, never
as a commit the tool makes on its own.

### How much of it is mechanical, measured

Counted on Orchard by reading project files, no model involved. The tool
computes this now: `Modernisation.Survey` produces every figure below, and the
report has carried them since M6. The line that used to sit here, saying the
tool did not compute it yet, outlived the code by several commits.

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
divergence across 93 distinct packages, and 279 `bindingRedirect` elements.**

The 279 corrects a figure written here earlier, which said one. The earlier
count was taken by hand with a case-sensitive search, and .NET names the file
`Web.config`. The scan, which compares names case-insensitively because Windows
does, finds 279 across the module configs. Verified independently before this
was rewritten: `find -iname web.config -o -iname app.config` over the same tree,
excluding build output, returns the same 279.

**So Orchard is not the tended legacy this section claimed.** Its packages agree
with each other today, but the redirects are the trace of disagreements that
were already reconciled once, and they are load-bearing: dropping one during a
conversion is a runtime failure that nothing in the build predicts. A tended
legacy and a drifted one are not the same job and must not carry the same
estimate, which is exactly why the scan has to tell them apart, and exactly why
a number nobody re-measured is worth less than one the tool produces on demand.

**The market says this is the opening.** Microsoft shipped GitHub Copilot app
modernization for .NET in September 2025 and deprecated the free .NET Upgrade
Assistant it replaced. The public complaints are specific: less deterministic
than the tool it replaced, partial upgrades leaving hundreds of hours of manual
repair, and NuGet package references that do not exist.

That last one is the failure mode this codebase is built against. The map, the
risk ranking and the diagrams read the disk. They cannot invent a package.

**Why last, and it is not a judgement on the idea.** M0 through M7 read: a wrong
reading is an embarrassing report, regenerated in seconds. M8 writes, but only
tests, and a wrong one is caught by the runner before anyone reads it. This one
writes into someone else's system, where a wrong write is their production. That
is three steps down in what a mistake costs, and it is the whole argument for
the order. Same reason an autopilot is not installed on an aircraft whose
instrument panel is dark: the instruments are not a prerequisite to the
automation, they are what makes commanding it possible at all.

**Effort:** the mechanical conversion is bounded and knowable, a weekend. The
layer that decides *which* conversions to propose, and says plainly when the
answer is "this does not port", is the part that takes real time. Nothing here
ships before the surface a person commands it from.

### First conversion, measured on Orchard

`packages.config` to `PackageReference`, emitted as a patch and never applied.
Ten of the sixteen unblocked projects are candidates; the other six declare no
packages, so there is nothing to convert.

| | |
|---|---|
| Patches produced | **10** |
| Accepted by `git apply --check` | **10** |
| Project files still valid XML after applying | **10** |
| `packages.config` removed | **10** |

Two defects survived a careful reading of the output and were caught only by
handing the patch to git, which is the argument for the test that does exactly
that on every run:

**A file that does not end with a newline needs `\ No newline at end of file`.**
Orchard's project files are written that way. Without the marker every patch
was rejected at its last hunk.

**A byte order mark has to survive being read.** `File.ReadAllText` detects and
strips one, so the first line of the patch was three bytes short of the first
line on disk, and `packages.config` deletions were rejected.

Neither is visible in a diff a person reads. Both are fatal. The suite now runs
`git apply --check` against a generated patch rather than asserting on its
text.

---

### Second conversion: the SDK format, measured on Orchard

The rewrite itself is close to trivial. A pre-SDK file is a hundred and fifty
lines of which the SDK supplies all but ten. What is not trivial is knowing
when the ninety lines being deleted contained the one that mattered, so the
verdict is the deliverable and the patch is what is left when there is nothing
in the way.

| | |
|---|---|
| Projects judged | **89** |
| Converted | **10** |
| Refused, with a named reason | **79** |
| Patches accepted by `git apply --check` | **10 of 10** |
| Project files still valid XML after applying | **10 of 10** |

Refusals overlap, so these do not sum to 79:

| | |
|---|---|
| Custom build targets | **77** |
| A `ProjectExtensions` block, which carries the project flavour | **73** |
| Imports the SDK does not supply | **76** |
| Depends on packages with no path forward | **73** |

**Eleven in twelve projects cannot have their format converted mechanically,
and the reason is almost never the format.** It is a custom target, a web
flavour, or a dependency that ends the conversation before the file is even
opened. Any tool reporting a higher success rate on this estate is either
deleting build steps or not looking.

Unrecognised properties are carried over rather than dropped, which leaves
noise in the output: a class library emerges still declaring its ClickOnce
publish settings. That is the deliberate trade. Deleting a property the tool did
not understand is the one mistake it cannot detect afterwards, so it keeps them
and says so in the caveats.

### A measurement that was wrong the first time

Counted with `grep`, eighty-eight of eighty-nine projects appeared to carry a
custom target and only one project looked clean. Counted with an XML parser,
seventy-seven do and eleven are clean. The Visual Studio template ships a
`<Target Name="BeforeBuild">` block **inside an XML comment**, and a line-based
search cannot tell that from a real one.

The same shape as the 279 binding redirects earlier in this document: a quick
count, a plausible number, and a conclusion that would have been wrong. The
scan has to parse what it claims to measure.

---

## M10. Seams ✅

*Where the code can be cut, and where it cannot.*

A strangler fig replaces a system one capability at a time: the new
implementation runs beside the old, traffic moves across, and the old code is
deleted when nothing routes to it any more. It needs somewhere to cut. Michael
Feathers calls that a **seam**: a place where behaviour can be changed without
editing the code around it. Legacy code rarely has many, and which ones exist
decides whether an incremental migration is possible at all or whether the
honest answer is a rewrite.

M8 already answers *what does this do*. M9 answers *what converts
mechanically*. Neither answers *where can I cut*, and without that the two
cannot be composed into a migration a person actually performs.

### Measured on Orchard

`POST /api/seams`. Source only, no compilation, so it answers on a solution that
does not build. Generated files and tests are excluded, as everywhere else.

| | |
|---|---|
| Types judged | **3,380** |
| Substitutable today | **1,169** |
| Substitutable after extracting an interface | **1,909** |
| Not without a rewrite | **302** |

What closes a seam, by how many types it holds shut:

| | |
|---|---|
| `File` | **40** |
| `Guid.NewGuid` | **23** |
| `Directory` | **17** |
| `new StreamReader` | **15** |
| `DateTime.UtcNow` | **12** |
| `HttpContext.Current` | **12** |

The refusal reads as a sentence, because that is what gets acted on:
*CodeGenerationCommands reaches File, Directory, Guid.NewGuid and DateTime.Now
directly. Those calls have to be passed in before anything can replace them.*

An earlier count of the same shapes appears below. It was taken with a lexical
scan over every `.cs` in the tree, tests and generated code included, and gave
3,798 types. The two are not in conflict; the smaller number is the one worth
having, and the difference is a reminder that a count means nothing without the
filter that produced it.

| | |
|---|---|
| Interfaces declared | **567** |
| Classes declared | **3,332** |
| Sealed classes | **4** |
| Static classes | **188** |

**Orchard is unusually seam-rich and must not be taken as typical.** It is a
CMS built on a dependency injection container, so roughly one interface exists
for every six classes and almost nothing is sealed. A line-of-business
application written in 2009 without a container will invert both figures, and
the tool has to say so rather than report a number that flatters the estate.

### What closes a seam

| | |
|---|---|
| `File.*` called directly | **161** |
| `HttpContext.Current` | **25** |
| `DateTime.Now` / `UtcNow` | **37** |
| `ConfigurationManager.*` | **21** |

These are ambient dependencies: a call reaching out of the method to the disk,
the request, the clock or the config file. Each one is a place where the new
implementation cannot be substituted, because there is nothing to substitute
*through*. They are also the reason a characterization test sometimes refuses
to be deterministic, which M8 already detects by running twice, so the two
milestones are looking at the same defect from opposite ends.

### The binaries you cannot open

| | |
|---|---|
| `HintPath` pointing outside `packages/` | **9** |

An assembly referenced by path rather than by package is one nobody can
recompile from this repository. It may be a vendor DLL, an internal build
nobody kept the source of, or a file copied in years ago. For a port it matters
more than its count suggests:

- If the source exists, it recompiles against the new target and the question
  disappears.
- If it ships on NuGet, a version targeting **.NET Standard 2.0** or later is
  the bridge, since both .NET Framework and modern .NET can consume it.
- If neither is true, the seam has to go **around** it, not inside it. The
  capability gets an interface, the old binary keeps serving it on the old
  runtime, and the strangler replaces the interface rather than the DLL.

Modern .NET will reference a .NET Framework assembly through a compatibility
shim, and it frequently works. It is not a migration. M8 already recorded what
that failure looks like from the inside: an assembly that loads and then throws
`FileNotFoundException` on the first member read, because a dependency it
needed was never on this machine. A tool that reports such an assembly as
portable is lying by omission.

### What it should produce

For each capability a person might strangle: the seams that already exist, the
ambient dependencies that close them, and a verdict of *substitutable*,
*substitutable after extraction*, or *not without a rewrite*. The value is the
third one. Anyone can list interfaces; saying plainly that a module cannot be
cut is what saves the three weeks.

**Why after M9 and not before.** Cutting is a decision made with the safety net
already in place, and M8 is what provides it. Proposing a seam to someone who
cannot yet prove the behaviour is unchanged is proposing a leap.

**Effort:** the lexical pass is a weekend and produces most of the table above.
The verdict is the part that takes real time, and it is the only part worth
having.

---

## Deliberately out of scope

**Writing features, and open-ended refactoring.** Cursor, Copilot and aider do
this, with teams behind them. Competing there means losing quietly. M9 takes one
narrow slice of it, the transformations that are mechanical and identical across
every codebase, and nothing beyond that. M8 takes another, the tests whose
correctness a test runner settles without asking anyone. Both are narrow for the
same reason: they are the cases where generated code can be checked rather than
believed.

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
