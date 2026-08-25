# Roadmap

Legacy Lens answers questions about code. The next milestones extend that in one
direction: **reading a codebase and reporting what is actually there.**

For M0 through M7, nothing here writes code. Editing agents exist and are built
by teams with years of head start. The gap worth filling is elsewhere: analysis
of inherited .NET, running locally, for organisations that are not allowed to
send their source anywhere.

The milestones that write are ordered by what a wrong output costs. **M8 writes tests, never production code**: a generated test that is
wrong fails on the spot and is discarded before anyone sees it, so the machine
checks its own work and no human is asked to trust anything. **M9 writes into
the code itself**, which is why it comes last and why the line is drawn at "the
tool proposes a diff, a person approves it". M9 is also the one concession to
the position above, and Microsoft is the reason: it replaced a deterministic
upgrade tool with a generative one and shipped the exact failure mode this
codebase exists to avoid. **M11 writes nothing at all**: it asks questions, and
what it produces is a plan and a sketch that compiles. **M12 runs code**, which
is a different kind of capability again, so the command is the way in and the
route is off unless the operator turns it on.

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

**Fixed.** The window is used when the repository was alive during it, and the
whole history when it was not, with the threshold at a fifth of the directory's
commits: a codebase being worked on and one that is archived are an order of
magnitude apart rather than a few per cent. This repository has all of its
history inside two years; Orchard has one per cent of its own. The report says
which stretch was read, because two reports of the same codebase are only
comparable when the reader can see that.

The right column above is what comes out now.

**A second cause, found the same way.** Even over the right window, a file with
no churn scored exactly zero, because a zero factor in a geometric mean does not
lower a score, it deletes one. The most complex untested file in the whole of
Orchard, 116 branches over 338 lines, was ranked **last of 458**. Churn is
floored at a quarter now: it can double a score and no longer erases one, which
is the same reasoning `Rank` already applied when every value ties.

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

## M9. Mechanical migrations ✅

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

**Effort:** spent. The mechanical conversion was bounded and knowable, as
expected. The layer that decides *which* conversions to propose, and says
plainly when the answer is "this does not port", took the rest of it, which was
also as expected: on Orchard the four conversions produce ten patches and
seventy-nine named refusals.

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

---

### Third conversion: one version per package, measured on Orchard

The conversion nobody puts in a demo, and the one that decides whether the
others are safe. A package pinned to three versions in three projects is what
binding redirects exist to paper over, and converting a project's format on top
of that disagreement carries it forward silently.

| | |
|---|---|
| Distinct packages | **93** |
| Pinned to more than one version | **0** |
| Patch produced | **none** |

**Nothing to do, and it was checked.** That is the whole result on this estate,
and it is worth having: "no divergence" and "not looked at" are the same
silence otherwise. On a drifted estate this is the first patch to apply and the
one that makes the rest reviewable.

Every version it would write is one already on disk. Choosing the newest of
what is present cannot invent a package that does not exist, which is the
failure reported against the tools this replaces. Asking nuget.org for
something newer could, and would also mean two runs over the same unchanged
repository disagree.

Two things it says out loud rather than doing:

**Crossing a major version is a code change.** Nothing in a version number says
whether the API changed, so unifying 6.0.8 and 13.0.3 is flagged rather than
performed quietly.

**Binding redirects are named and never edited.** A redirect names an assembly
version, which is not the package version and cannot be derived from it by
reading these files. Guessing one produces a build that succeeds and an
application that throws on first use.

---

### Fourth conversion: configuration, measured on Orchard

`appSettings` and `connectionStrings` become one `appsettings.json`. The call
sites do not move, and the reason is the point: `ConfigurationManager` is a
static reachable from anywhere and `IConfiguration` is a dependency somebody
hands in. Rewriting the calls means opening a seam in every type that reads
configuration and changing every caller of those types. A tool that did it
anyway would emit a patch that does not compile, which is worse than no patch.

| | |
|---|---|
| Config files read | **65** |
| App settings carried over | **9** |
| Connection strings carried over | **1** |
| Keys declared twice with different values | **1** |
| **Keys the code reads that nothing declares** | **1** |
| Reads whose key is computed at runtime | **9** |
| Call sites left alone, across 6 types | **11** |

The undeclared key is the finding: `Orchard.Glimpse:WhitelistedIpAddresses`, read
at `WhitelistedIpAddressesSecurityPolicy.cs:14` and declared in none of the 65
config files. That is a null the application already meets at runtime, and it
was there long before anyone thought about porting.

**A defect this found in itself, on the first real run.** The first version
nested dotted keys, turning `Mail.Host` into `{ "Mail": { "Host": ... } }`,
because that is what a person writing the file by hand would do. It is wrong.
.NET joins nested names with a colon, so the key becomes `Mail:Host` and every
call site reading `Mail.Host` gets null. Keys are now kept exactly as they were
written. A translation that improves on its source is a different file with the
same name.

---

### All four, accepted by git on Orchard

| | |
|---|---|
| `convert repos/orchard packages` | 33 KB, **accepted** |
| `convert repos/orchard sdk` | 61 KB, **accepted** |
| `convert repos/orchard versions` | no patch, nothing divergent |
| `convert repos/orchard config` | 718 B, **accepted** |

Checked with `git apply --check` against the real repository rather than
against a fixture, and the same patches come back byte-identical through
`POST /api/convert`.

**They were also, until this point, unreachable.** The first two conversions
had existed and been tested for several milestones with no command and no route
to obtain them: this section described a patch a person approves, and no person
could get one without writing C#. `convert <path> <kind>` writes the patch to
standard output and the reasons to standard error, so redirecting it leaves a
file git can take.

The interface shows the same thing: four conversions to pick from, the patch as
a diff, and the refusals given a heading rather than folded away, because on a
real estate they are the longer list. Nothing on that page applies anything,
and the page says so under every patch. A tool that commits its own output is a
tool whose mistakes stop being reviewable, which would undo the argument the
whole milestone rests on.

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

## M11. Measure, migrate, mutate ✅

*Three steps, and the tool changes role between them.*

M9 ends on a number that reads like a wall: **73 of 89 projects cannot be
ported**, held by four packages with no path to modern .NET. That is the honest
answer and it is where every tool in this space stops. It is also useless on its
own. "Blocked" is not a plan, and a CTO who reads it has learned that the
project is hard, which they already knew.

The four blockers are not the same problem wearing one label:

| | |
|---|---|
| `Microsoft.AspNet.Mvc` | has a successor, and a line-by-line correspondence for most of it |
| `Microsoft.AspNet.Razor` | has a successor, same story |
| `Microsoft.Web.Infrastructure` | has no successor because it **disappears**: nothing replaces it |
| `Microsoft.AspNet.WebPages` | has no successor and no equivalent. This one is a rewrite |

One is replaced, one is deleted, one is rewritten. Three budgets that have
nothing to do with each other, currently reported as one word.

This milestone is the ladder out of that, in three steps that are worth naming
because they are worth selling separately.

---

### Measure ✅

Everything M0 through M6 already do, gathered behind one surface instead of nine
endpoints: the map, the diagrams, the risk ranking, the packaging survey, the
seams, the assessment. No model is involved and none of it is new. What is new
is that it is one screen and one question box rather than a README full of curl.

**This is the part that sells first.** It runs in a day, on a read-only copy,
and commits nobody to anything: here is what converts by itself, here is what
never will, and here are the measurements behind both. The rest of this
milestone is the engagement that follows, and nobody grants that before seeing
this.

---

### Migrate ✅

The mechanical conversions from M9, applied rather than downloaded.

A button, and the button is not the risk. The rule has always been that a person
approves the diff, and clicking after reading is a person approving the diff.
What changes is where the result lands: **a branch of its own, and the original
branch checked out again**. Nothing moves under an open editor, and the history,
the second reader and the revert all come free.

**It does not push and it does not open a pull request.** That needs a remote and
a credential, and sending someone's code anywhere is their decision, not a
button's. What comes back is the branch name and the three commands that read
it, keep it or drop it.

Two refusals earn their place, and both were checked against a real repository:

**An uncommitted working tree.** Applying then would put somebody's work in
progress in the same commit as the patch, and the branch would stop being one
reviewable change.

**A patch git will not take.** Checked before the branch is created, so a
failure leaves nothing behind to clean up. Half an application is worse than a
refusal: it looks like success until somebody builds.

Measured on Orchard: ten project files, 104 insertions against 973 deletions,
committed to `legacy-lens/sdk-<timestamp>`, with `dev` still checked out and a
clean working tree afterwards. The commit message says where the change came
from and carries no fingerprint of a machine having written it.

On Orchard this covers ten projects. Ten of eighty-nine, said plainly, because
the number is the finding.

---

### Mutate ✅

The other seventy-nine.

**All four pieces are built and visible.** The surface, the candidates with
their coverage, a projection of one file, and the questioner, all in the browser
rather than only behind a command.

Here the tool stops answering questions and starts asking them. That inversion
is the whole idea, and it is not an interface flourish: on a non-trivial case the
missing information is **not in the code**. Whether a session must survive a
restart, whether an endpoint has callers nobody controls, how many machines sit
behind a load balancer. No static analysis will ever recover any of it.

**The questioner is deterministic, and no model is involved in it.** This
reverses what an earlier draft of this document said, and the reason is the
reason everything else here is written down rather than generated: a model asked
what to ask produces plausible questions with no known set of answers behind
them, and a diagnosis that cannot say where it will land is a conversation. The
questions come from a hand-written catalogue, and the anchoring in the code is
mechanical because the files and the lines are already measured. A catalogue is
verifiable. A model deciding what to ask is not.

Three rules keep that from being a chatbot with a logo.

**The set of outcomes is finite and written down first.** "Session does not
survive a restart" has four exits, not forty: in-memory state, a distributed
cache, the database, or the state is removed. They live in a hand-written
catalogue. Questions exist to eliminate them, so there is always a known place
to land, and a question that eliminates nothing is a question that does not get
asked.

**Every question cites a line.** Not "how do you handle sessions" but the four
lines in this controller that write and read `Session`, printed with their file
and line numbers above the question. A question with no reference to the code is
a generic questionnaire, and it reads as one by the second screen: the reader
can tell nothing was read before they were asked.

**There is a stopping condition.** The session ends when an answer stops
reducing the remaining options. Measurable, and the alternative is a model that
asks until the reader closes the tab.

What comes out separates its sources, which no migration tool does today:

> The code says: `Session` is written in 47 places.
> You said: two instances behind a load balancer.
> Therefore: a distributed cache, and here are the 47 places.

Every conclusion traces to a measured fact or to a sentence somebody owned. Six
months later, when the decision is questioned, the trail is still there.

---

### Three pieces this needs

**The usage surface ✅, and it comes first.** The hard question about a dead
package is never "what are the alternatives", which any blog post answers in ten
minutes. It is "which alternative covers what I actually use". A package exposes
two hundred members and a codebase touches six of them. Reading which six, and
how concentrated they are, is pure Roslyn work with no model involved, and
without it everything else in this milestone is decoration. A replacement
proposed without knowing what is used is a guess with a table around it.

Measured on Orchard, for the package that holds 73 of its 89 projects:

| | |
|---|---|
| Uses of `Microsoft.AspNet.Mvc` | **3,634** |
| Distinct types | **190** |
| Files importing it | **365** |
| **Types carrying four fifths of it** | **24** |
| Files carrying four fifths of it | **134** |

*These are the figures after M20 corrected how a solution's own attributes are
recognised. When this milestone shipped they read 3,877 uses of 198 types with
28 carrying four fifths.*

**Twenty-four.** That is the number this milestone exists to produce. "73
projects blocked" is a wall; "28 type correspondences cover eighty per cent of
the work" is a catalogue somebody can write in an afternoon. The two sentences
describe the same codebase.

The figures above were 4,529 uses across 271 types with 41 carrying four fifths,
until M13 stopped attributing to a package the names the target framework still
supplies itself, and the test scaffolding a test file names beside them. `TextWriter` and `ArgumentException` appear in Orchard's MVC
files like they appear in every C# file, and 69 such names over 502 uses were
being counted as ASP.NET MVC's work. The number got smaller because the
measurement got truer, which is the only reason a number here is allowed to move.

The ten most used are `ActionResult` at 541, `HttpUnauthorizedResult` at 408,
`HttpPost`, `RouteValueDictionary`, `SelectListItem`, `HtmlHelper`, `ActionName`,
`Controller`, `IHtmlString`. Every one of them has a direct counterpart in
ASP.NET Core. The rewrite is repetitive, which is exactly why counting it pays.

The tenth used to be `Test`, NUnit's attribute. It is `UrlHelper` now: a short
hand-written list of what a test framework supplies keeps that scaffolding out,
the same way a short list keeps out what C# supplies.

**A mistake the first real measurement found, which the unit tests did not.**
The obvious way to collect types is `OfType<TypeSyntax>()`, and it is wrong:
Roslyn derives `IdentifierNameSyntax` from `TypeSyntax`, so every identifier in
every expression qualifies. The first run reported `x`, `builder`, `result` and
`Count` as the most used types in Orchard, against 59,950 uses of 5,362 "types".
Fourteen tests passed throughout. The type positions are now named one by one,
and two of them are not obvious: a delegate is not a `BaseTypeDeclarationSyntax`,
so Orchard's own `Localizer` counted as ASP.NET MVC's, and a generic parameter
`T` belongs to the method that declares it.

Static calls are deliberately not counted. Telling `Assert.That` from
`services.Add` needs resolved symbols, and guessing from the capital letter puts
half the codebase's local variables back in the list.

**A catalogue of successors ✅, hand-written.** Roughly a hundred entries covers
the .NET Framework surface that matters. Coverage against the usage surface is
then arithmetic. **The catalogue is not generated**, for the reason this whole
document keeps returning to: a model asked for successors returns the right
ninety-seven and invents three, with the same confidence, and inventing package
references is the exact failure reported against the tool Microsoft shipped.

It is a **file**, not a table in an assembly. It grows with every migration
anybody performs and should not need a rebuild to do so, and it is the part that
took the work: the engine around it is a week, the knowledge in it is years.
That also makes it separable from the code, which matters for anyone who wants
to keep one and publish the other.

Every type gets one of **three** answers, never two:

| | |
|---|---|
| a named replacement | it converts |
| recorded as having none | a blocker, and the fact worth having |
| absent from the catalogue | **unknown**, which is not the same as fine |

Folding the last two together is how "we have not looked at this" becomes "this
is fine", and that sentence is what gets a migration signed off and discovered
in month four.

First run against Orchard, for `Microsoft.AspNet.Mvc`:

| | |
|---|---|
| Calls covered by `Microsoft.AspNetCore.Mvc` | **71%** |
| Types recorded as having no replacement | 3, over 5 calls |
| Types the catalogue says nothing about | **146, over 1,127 calls** |

**Seventy-one per cent, and the honest part is the 146.** The
catalogue is young; what makes the number usable is that it says so instead of
counting silence as success. Coverage is weighted by calls rather than by type,
because a type used five hundred times and one used once are not the same amount
of work.

`Microsoft.Web.Infrastructure` is catalogued with an empty successor and a note:
nothing replaces it because nothing needs to, and deleting the reference is the
migration.

**A compiled projection ✅.** These rewrites are repetitive: one ASP.NET MVC
controller resembles every other one, so a reader who sees one before and after
knows what the remaining forty-six cost. The model writes that projection, and
the compiler decides whether it is worth showing.

No SDK, no restore, no network. The assemblies are the ones this process already
trusts, which on an ASP.NET Core host is the whole of `Microsoft.AspNetCore.App`:
**the target framework is present because the tool is running on it.**

**What the first real file destroyed.** A projection compiled on its own has none
of its project around it. Orchard's smallest controller names thirteen types
from Orchard, so "does it compile" rejects every projection worth making. The
question had to become *did it invent anything*, which needs three outcomes:

| | |
|---|---|
| declared by the solution | absent because the project is not here. Expected |
| exists in the framework | a missing using. Worth another attempt, not a discard |
| **exists nowhere** | **invented, and the only real defect** |

Getting to three took three corrections, each found by running it and none by
the tests. `IActionResult` was reported as invented when the model had merely
forgotten the namespace. `[OrchardFeature]` was reported as invented because an
attribute is written without the suffix it is declared with. And `ContentManagement`,
`Localization` and `UI` were reported as invented types when they are segments
of Orchard's own namespaces: the compiler names the segment, not the path.

**Measured, on Orchard's `AdminLocalizedTaxonomyController`, with a 1.5B model
running locally:**

| | |
|---|---|
| Invented names | **0** |
| Attempts needed | **1** |
| Types from the project, correctly recognised | 13 |
| Correspondences handed over from the catalogue | ActionResult becomes IActionResult |

It moved `System.Web.Mvc` to `Microsoft.AspNetCore.Mvc` and `System.Web.Routing`
to `Microsoft.AspNetCore.Routing`, and left every Orchard type alone. The model
is never asked what replaces what: the catalogue supplies those as facts, and it
applies them to code, which is the one part no catalogue can do because every
file uses them differently.

It ships labelled for what it is: **nothing invented, behaviour not verified.**
Smaller than "here is your migrated code" and far more useful than a chat
transcript somebody has to go and test.

**The questioner ✅.** Three dilemmas so far, each one raised by names that
appear in the code: where session state goes, what replaces the ambient request,
what replaces output caching. Four outcomes, four questions and a stopping
condition apiece.

The engine is a fold. Nothing is stored but the answers, and everything
derived, what is still possible, what to ask next, whether it has landed, is
recomputed from them. There is no second copy of the state to fall out of step
with the first, and an answer to a question a later catalogue no longer contains
is skipped rather than fatal, so a diagnosis recorded last month still reads
correctly today.

Every choice says what it would rule out **before it is clicked**, computed
against what is still standing rather than against the full list. Halfway
through a session an answer often rules out nothing, and the screen says so. An
answer that quietly narrows things behind the reader's back is what makes a
wizard untrustworthy.

Three endings, not one. It lands on an outcome; or every outcome is ruled out,
which is a real result and says so rather than showing an empty panel; or two
remain and nothing left to ask can separate them, which is stated plainly
instead of picked between.

**What running it found, and the unit tests did not.** Twenty-nine tests passed
against a textbook session-state controller that raised **nothing at all**:

| | |
|---|---|
| The catalogue named `SessionStateAttribute` | the code writes `[SessionState]`, and C# allows the short form |
| The catalogue named `HttpSessionState` | real code writes `Session["cart"]`, and the type name appears nowhere |
| `using System.Web.SessionState;` matched | a using is not a place a reader can go and see the problem |
| Two triggers on one line | printed the same line twice, which reads as a defect in the tool |

The first is a rule of the language, so it lives in the finder: any trigger
ending in `Attribute` is matched both ways, once, rather than being remembered
by hand for every future entry. The rest are the catalogue and the ranking.

**Then a real repository broke the fix.** Orchard mentions `Session` 62 times,
and 56 of them are NHibernate's `ISession`, which shares the name and nothing
else. The dilemma was being raised mostly by an ORM that has nothing to do with
it, and a panel that is ninety per cent wrong is worse than one that is empty.

The discriminator is the shape rather than the name: all six real ones are
`Session[...]`, and NHibernate never indexes its own. A trigger can now be
written `Session[]`, meaning *only where the name is indexed*, which is a rule
about the language written where a person can read it rather than a special
case buried in the finder.

Answers are kept per project. Two codebases behind two different load balancers
give two different answers to the same question, and mixing them describes
neither. Deleting a project takes its answers with it, or they would be handed
to whichever project next reuses the identifier.

Built in that order. Reversed, it produces handsome projections of code nobody
counted, which is the competitor this project exists to be different from.

---

### What it does not promise

**A compiled projection is not a passing test.** It proves the code is valid,
not that it behaves the same. That is now wired to the characterization net, and
M12 below is what came of it: both versions called with the same values, and the
differences printed with the inputs that produced them. What it changes is
narrower than it sounds, and the honest half is there rather than here: on a
controller importing `System.Web` there is no behaviour to record at all, and
the answer is *not checked* with the reason.

**No architecture is recommended in the abstract.** The tool does not say
"microservices". It says which projects depend on nothing but themselves, which
one is referenced by thirty-four others, and which two reference each other and
are therefore one component that does not know it. Those are read from the
dependency graph. What to do with them is a decision that depends on team size,
traffic and who is on call, and none of that is in the code.

**No estimate in days.** Volume and shape, measured: 366 `System.Web` references
across 73 projects, and whether 200 of them sit in twelve files or are spread
evenly, which is the difference between three weeks and six months. The price is
set by a person, because it depends on who does the work. A tool that announces
"47 days" will be wrong, and that is the error people remember, because it is
the one that got them to sign.

**A correction the browser forced.** The file offered for projection was the
one using the package most, which on Orchard is 821 lines: a local model spends
minutes on it, twice over because a failure is retried, and nobody reads the
result. Ranked by **calls per line** instead, the first offer became a
180-line file with 60 calls per hundred lines, which teaches the same lesson in
a screen. Files past 400 lines are left out of the list entirely and the count
of them is stated, because offering a file the projection will then refuse is
worse than offering none.

**Effort:** Measure is assembly, a week. Migrate is a button and a pull request
on top of M9, days. Mutate is the milestone: the usage surface first, then the
catalogue, then the projection loop, and the catalogue is written by hand and
keeps being written.

---

## M12. Nothing moved ✅

*The sentence M11 was not allowed to say.*

A projection ends on a claim it earned: **compiles against .NET 10, invents
nothing**. That is a statement about the code being valid. It says nothing about
what the code does, and a file can be perfectly valid and quietly return
something else. Every migration tool in this space stops at the first sentence
and lets the reader hear the second.

So both versions are compiled into their own load context, called with the same
values, and the results compared.

**The same values, not equivalent ones.** One set of arguments is built and
handed to both, which is why a parameter of a type the file declares itself is
refused: recompiled into the other assembly it is a different type with the same
name, and rebuilding an equivalent object there would be comparing two objects
rather than one. Any difference found could as easily be the rebuild's.

**A method that disagrees with itself is dropped, not reported.** Both calls go
through M8's observer, which runs each one twice and keeps the result only if
the two runs agree. Without that, every method reading a clock is a behaviour
change and the report is noise nobody reads twice. The other half of that rule
earns its place too: a rewrite that *starts* reading a clock is reported,
because the original was steady and now it is not.

---

### The values are read from the code

The first live run, on a file written to look ordinary, reported a rewrite of
`years >= 3` into `years > 3` as **unchanged over six calls**. The invented
values are 0, 1, -1, 42 and the two extremes. None of them separates the two.

The boundary was written down three lines away the whole time.

Every number and short string a file mentions is now offered back to it as an
argument, each number with the two either side of it, because an off-by-one
lives beside a constant rather than on it. Ranked by how often the file says
them and by size after that.

**Sorted the way they print instead, "1000" comes before "7"**, and the boundary
falls off the end of the eight that are kept: a file with three configuration
numbers and one limit kept the configuration and dropped the limit. That was a
bug this found in itself, and it survived a first mutation run because the test
written for it had too few numbers to overflow the cap.

They are taken in turn with the invented ones rather than appended, because the
limit caps rows and not candidates: appended, a smaller case budget silently
switched off reading the code and nothing said so.

What that buys, measured on the same file:

| | |
|---|---|
| `amount * 20 / 100` → `amount / 5` | identical until `int.MaxValue`, where it overflows. Caught, and not planted |
| `years >= 3` → `years > 3` | caught at 3, because the file said 3 |
| `ArgumentNullException.ThrowIfNull` for a hand-written throw | same exception, same inputs. Reported unchanged |

---

### Where it reaches, said plainly

Behaviour can only be compared where both versions run here, and a controller
importing `System.Web` does not compile on modern .NET at all. **On the files the
projection starts from, this will usually answer *not checked*.**

That is not a gap to be apologised for; it is the finding. What the check
reaches is code that has already been pulled out from behind the framework: the
services, the calculators, the validators. Which is the order M10's seams exist
to encourage, and this is what makes each step of it safe.

**Zero methods compared is not success.** It is the most likely outcome on a file
whose work happens through a framework, and reporting it as a pass would be the
worst thing in this repository. The refusals travel with the report rather than
being available on request, and they are counted in methods: a refusal is
recorded per call, so one unsteady method contributed fourteen of them and the
claim announced that fourteen methods were passed over. Found by reading it back.

---

### Running code, which is a different kind of capability

Everything else here reads. It parses files that do not build, it compiles a
rewrite without running it, and none of that can do anything to the machine.
This executes, and on a rewrite a model wrote that is executing something nobody
has read.

The **command** needs nothing turned on: somebody typing it against two paths
they chose has already made the decision. The **route** is off unless the
operator sets `ALLOW_RUNNING_CODE`, and the published image does not, so a demo
anybody can reach cannot be talked into running anything. That is a setting on
the process rather than a dialog, because the person who deploys is the person
who knows whose code it is.

**Effort:** the engine is days. The honesty around it, which is the part that
took the work, is the refusals, the counting and the two live runs that found
what the tests did not.

---

## What this borrows, and from whom

Nothing here is a new idea. Each of these is an existing technique with a half
that is right for this problem and a half that is not, and the work was picking
the seam rather than inventing anything.

Worth writing down for two reasons: so nobody re-derives it, and because the
distinction between taking an *idea* and taking *code* is the one that decides
whether a licence is anybody's problem. **An idea carries no licence.** A file
does. Everything below is the first kind; anything of the second kind would need
its licence read before a line of it came near this repository, and that is a
decision to make deliberately rather than while in a hurry.

| | taken | left |
|---|---|---|
| **Characterization tests** (Feathers) | pin what the code does before changing it; the tests are a net, not a specification | writing them by hand, which is the reason nobody has them |
| **Golden master / approval testing** | a result you cannot describe can still be pinned by its serialised form | a checked-in file per case, which rots as fast as it is written |
| **Property-based testing** (QuickCheck and its descendants) | generated inputs reach boundaries examples never do | random generation. A suite that differs between two runs is one nobody can review or commit, so the values here are fixed |
| **Seeded fuzzing** (a fuzzer's dictionary of constants pulled from the target) | the program tells you its own boundaries; feed them back as inputs | the fuzzing. There is no crash oracle here, and no budget for millions of executions |
| **Concolic execution** | the interesting inputs are the ones a branch turns on | the solver. Reading the literals gets most of it for none of the weight |
| **Mutation testing** (Stryker and its kind) | change the code, see whether the tests notice; used three times in this repository and it found something each time | running it over the whole codebase on every push |
| **Upgrade Assistant, try-convert** | the mechanical conversions really are mechanical, and M9 does them | stopping at the wall and reporting it as completion |

The two that meet in M12 are the third and fourth rows. Property-based testing
says *generate inputs you would not have thought of*, and is right. Seeded
fuzzing says *the constants are in the program already*, and is also right.
Neither is enough alone: invented values missed `years >= 3` and the file's own
values would never have found the overflow at `int.MaxValue`. Taken in turn,
they find both.

---

## M13. Asking the framework ✅

*The column beside the catalogue, answered by the runtime rather than by hand.*

`data/successors.json` is the part of this that took the work, and it is written
by hand. Around a hundred entries covers the surface that matters, the coverage
figures already say out loud how much is missing, and it grows one migration at
a time.

Microsoft publishes [apisof.net](https://github.com/dotnet/apisof.net), which is
open source and is a database of which API exists in which framework and
version. That is the half worth taking: nobody should be typing out which types
`System.Web` has and modern .NET does not.

**It is not the same question.** That data says what *exists*. The catalogue here
says what *replaces* what, which is a judgement with a note attached, and the
three-way answer this rests on, replaced, has none, unknown, is not in it. So
the shape of M13 is a generated floor with the judgements laid on top by hand,
and the generated part must be visibly separate from the written part, or the
whole distinction that makes the catalogue trustworthy dissolves.

**Read the licence before taking a byte of it.** Taking the idea is free; taking
the data is a decision with terms, and this repository publishes under MIT.

---

### What was built instead, and why

The licence was read first: apisof.net is MIT, and compatible. Nothing was taken
anyway.

The question the unknown column asks is *does modern .NET still have a type of
this name*, and the framework being migrated to is already loaded into this
process. It is compiled against every assembly that framework ships, on every
projection. The answer was in metadata this tool reads anyway, current with the
runtime in front of it, offline, and without anybody else's data in the
repository.

So the reading is local, and the two things apisof knows that it does not are
written here rather than pretended away: **which version introduced an API**, and
**which package a type moved into once it left the base library**. The second is
the one that will bite: `System.Configuration.ConfigurationManager` is absent
from the platform set and available as a package, and this reports it as gone.

**Four answers, and only one of them is a lead.** Measured on Orchard, against
the package that holds 73 of its 89 projects, on the 146 types the catalogue
never mentions over 1,127 calls:

| | types | calls | |
|---|---:|---:|---|
| named inside the successor | 17 | 136 | a lead |
| the same name somewhere unrelated | 15 | 182 | **a trap** |
| nowhere at all | 114 | 809 | the finding |

**146 unknown became 129 left to decide**, and a fourth answer that used to sit
in this table was removed by fixing the cause instead. A name the framework
still supplies under `System.*`, which the catalogue does not record as this
package's, is no longer attributed to it at all: 69 types over 502 uses on
Orchard, gone from the estimate rather than explained inside it.

**That correction had a trap of its own, caught by measuring.** Excluding every
`System.*` name would drop `Newtonsoft.Json.JsonSerializer` because
`System.Text.Json.JsonSerializer` exists, and make that migration look smaller
than it is, which is the one direction this tool must never be wrong in. So the
catalogue's own list of a package's types protects them, and where nobody says
what a package claims, nothing is dropped.

The trap row is the one that justifies four answers rather than two.
`System.Web.HttpContext` and `Microsoft.AspNetCore.Http.HttpContext` share a
word and nothing else, and that pair is the hardest part of an ASP.NET migration
rather than a rename. A first version of this looked for the name anywhere under
`Microsoft.AspNetCore` and reported exactly that as found, along with
`RouteData` resolving to Blazor's. Reported as correspondences they would send
somebody into the worst of the work believing it was done.

**And it only answers where the answer means something.** `log4net`'s successor
is Serilog, which nothing in the runtime carries, so every type of every
predecessor came back under "the framework does not have at all". Literally
true, and a reader concludes that twenty-two types are gone when what happened
is that the question could not be asked. A successor the framework does not
carry is now said to be a package rather than reported as an absence. Of the
five candidates Orchard's packages have, four are answerable and Serilog is not,
and `Microsoft.Extensions.Logging` beside it is, which is the pair that makes
the distinction worth drawing.

**Nothing is written back into the catalogue**, and a test asserts it. The
generated part has to stay visibly apart from the written part, or the
distinction that makes the catalogue worth trusting dissolves into it.

**A wrong assumption caught by reading it back.** The first version excluded
`System.Web` from "still there", on the reasoning that the whole family went
away with ASP.NET. Modern .NET keeps exactly two, `HttpUtility` and
`IHtmlString`, and Orchard uses the second more than a hundred times: the
exclusion reported two survivors as losses. The set is read from the target
framework, so nothing about the old one needs excluding.

**And one this exposed in older code, now mostly fixed.** The usage surface
attributes a type to a package because a file importing that package uses it.
The base library half is dealt with above. The test-framework half needed its
own answer, because no amount of asking the framework will say that `[Test]` is
NUnit's: NUnit is not in the framework either. A short hand-written list of what
a test framework supplies keeps that scaffolding out, exactly the way a short
list already keeps out what C# supplies. What remains is a third-party package's
types counted against another's, and that needs resolved symbols, which costs
the property that makes this usable on inherited code. It stays in
`docs/NEXT.md`.

---

## M14. Something to hand to somebody ✅

*The same program, in one file, with nothing to install.*

Today this is a web service, which is right for a team and wrong for the first
conversation. A single binary that opens a window and reads a folder removes the
sentence that ends most of those meetings: *where would it run?*

`./desktop.sh` builds it, `./desktop.sh win-x64` builds the Windows one, and
what comes out is 57 MB: the executable, the interface beside it, and the two
catalogues. No runtime, no web server, no database to install. Double-clicked,
it picks a port nobody is on, opens the reader's own browser and prints the
address in case it cannot.

**There is no second implementation of anything.** It is the same executable the
container runs, published self-contained with the built interface beside it and
serving both from one port. Which is also the acceptance test: pointed at
Orchard, the binary answers 3,634 uses, 24 types carrying four fifths, 76 per
cent covered and 121 left to decide, which is what the server answers to the
digit. Those are the figures as they stand after M20; the property being checked
is that the two agree, and they still do.

---

### The model is not in it, and that is the honest half

A local model is gigabytes and cannot be handed over on a memory stick. What
runs without one is most of this tool: the map, the diagrams, the risk ranking,
the mechanical conversions, the usage surface, the framework reading, the
decisions it cannot make for you, and the assessment. Questions and answers need
Ollama, and the program says so at startup rather than failing at the first
click.

### What single-file publishing broke, and how it was found

Three things, and none of them by a test. The binary was pointed at Orchard and
its answers compared with the server's.

**The framework reading was dead.** A self-contained single file embeds its
assemblies, so `TRUSTED_PLATFORM_ASSEMBLIES` had nothing to open and the
framework's own surface came back empty. The tool did not fail: it silently
reported its pre-M13 numbers, 4,379 uses where the server said 3,877, with no
error anywhere. **The same program giving different answers depending on how it
was built is worse than one that refuses to run**, so the surface now says it
could not read the framework instead of counting as though it had looked. The
packaging fix is `IncludeAllContentForSelfExtract`, which puts the assemblies
back on disk.

**The catalogues were not found.** They were beside the executable and the
program looked in `AppContext.BaseDirectory`, which in a single-file build is a
temporary extraction folder nowhere near it. Every package came back with no
candidate at all. Both catalogues now look beside the executable first.

**Characterization cannot work there at all.** Compiling a generated test needs
xunit and the subject as files a compiler can open, and there are none. It says
so rather than emitting a compilation with no references and reporting the
result as a test that failed.

### What is still missing

**Code signing.** Unsigned, the first thing a prospect sees is their own machine
telling them not to trust it, which is worse than no demonstration. A
certificate is a purchase and an identity check, not a build step, and nothing
here can substitute for it.

**A first-run experience for the model.** Today it says Ollama is absent. It
could offer to fetch it.

---

## M15. A crash that takes nothing with it ✅

*The behaviour comparison, in a process of its own.*

M12 runs the code it was handed inside the process that asked. `Observer` says
why that is acceptable, and says it as an assumption rather than a claim:

> A thread that will not stop cannot be killed in .NET, so the timeout abandons
> the wait rather than the work. The process this runs in is a short-lived
> command, which is what makes that acceptable.

A server is not a short-lived command. Read against a long-running API, the same
sentence says that every method containing a loop whose exit condition was an
operator watching a screen leaves a thread behind for the lifetime of the
service, and that the first stack overflow in somebody's legacy code takes the
whole thing down. Neither is catchable in process. Both end at a process
boundary.

So the route runs the command in a child and reads what it printed. There is no
second implementation: `equivalence --json` is the same run the terminal
version prints, written for a program rather than for a person.

### What crosses the boundary

Only the facts. What compiled, what was compared, what moved, what was passed
over. The sentences do not travel: the claim, the verdict and the grouped
refusals are recomputed on arrival from the facts that arrived, and the derived
members carry `[JsonIgnore]` to say so where a reader will look. A claim sent as
text could land beside numbers that no longer support it, and the one sentence
in this tool that must never be wrong is the one that says nothing moved.

### Measured

Against a running server with `ALLOW_RUNNING_CODE=true`, on a file whose method
recurses forever:

```
ran      : false
verified : false
claim    : Nothing was checked: the process running the comparison ended with
           code 134, which is what a stack overflow in the code under test looks
           like from here. It said: Stack overflow.
```

The next request was served normally. In process, the same file dumps core.

The tests were checked by putting the comparison back in process, and four of
them fail: the one that measures whether the abandoned call is still running,
the deadline, the missing path, and the crash, which does not fail so much as
end the test host where it stands. That last one is the milestone stated as
plainly as it can be: with the boundary removed, the thing running the tests
dies of the code under test.

### What this bounds, and what it does not

It bounds a crash, a hang, the memory, and what the code under test can see of
the process that asked. It does not bound the filesystem, the network, or
anything else the operating system gives every process this user runs. **It is a
blast radius, not a sandbox**, and `ALLOW_RUNNING_CODE` stays off by default for
exactly the reason it always did. The child is handed none of this tool's own
settings and runs from a temporary directory, which is hygiene rather than
containment and is described as such.

A real sandbox is a container or a virtual machine per comparison. That is a
deployment decision, it costs seconds per call, and it belongs to whoever runs
this against code they did not write.

---

## M16. The boundaries the code was written around ✅

*The characterization net tries the numbers the code names, not only the ones
this tool invents.*

M12 already did this for comparing a rewrite, and it was the change that turned
`years >= 3` rewritten as `> 3` from **reported unchanged over six calls** into
a caught regression. The net that writes test files did not do it, because it is
handed an assembly rather than a file: the source may be on another machine, may
have moved on, and on an inherited codebase may not be anywhere anybody can
find.

So the constants are read where they certainly are. `if (years >= 3)` leaves the
3 in the method body, and that body is in the assembly being characterized. A
`const int Minimum = 250` leaves no instruction at all, so the fields are read
too.

**Decoded rather than scanned.** An instruction stream cannot be searched for
bytes that look like constants: an operand holds any value, so `0x20` inside a
four-byte token reads as `ldc.i4` to anything stepping a byte at a time. The
walk decodes instruction by instruction, and the opcode table is built from the
runtime's own `OpCodes` rather than copied out here, because a hand-written
table of two hundred numbers has to be right and nothing would say when it was
not. The two readers, source and compiled, are checked against each other.

### Measured, because the note said to measure

A case here costs a reader rather than a millisecond: this net writes a file
somebody commits. Four mutations of a class with four boundaries, each edit the
kind that looks like tidying, with the suite generated from the original run
against each mutant.

| cases per method | invented only | reading the code | tests written |
|---|---|---|---|
| 4 *(the default)* | 0 of 4 | **2 of 4** | 16 either way |
| 6 | 1 of 4 | 2 of 4 | 23 / 24 |
| 8 | 1 of 4 | 2 of 4 | 23 / 32 |
| 10 | 1 of 4 | **4 of 4** | 23 / 38 |
| 12 | 1 of 4 | 4 of 4 | 23 / 44 |

Two things decide the default. **At four cases it is free**: the same sixteen
tests are written either way, because the cases were already being spent, just
on values that told nobody anything. And **inventing harder does not substitute
for it**: what this tool invents is six values long, so past the budget that
reaches all six, more of a reader's attention buys nothing. The invented column
never moves again.

Reaching the last two costs ten cases and more than doubles the file. That is a
trade for whoever reads and commits it, so it is `--cases` with the cheaper end
as the default rather than a decision made on their behalf.

### What the measurement found on the way

**The verifier reported clean without running anything.** It runs the cases it
was named, and a caller holding only a generated file names none, so it compiled
the suite, invoked nothing, found no failures and reported a suite that passed.
The repository's own `Everything_offered_compiled_and_passed`, the test that
re-checks the promise the whole approach rests on, had been unfalsifiable since
it was written. Naming none now runs everything, and a verification that
executed nothing can no longer call itself clean.

**Two plausible improvements measured as nothing.** Raising the per-type
allowance from eight values to twenty-four changed no outcome at any budget: the
ranking is by how often a value is mentioned, and a boundary written twice
already outranks the neighbours of everything else. Excluding the values this
tool invents before the allowance is spent changed no outcome either, because
the same values are dropped downstream anyway. That one was kept for a reason
the mutation test could not show and a direct one could: on a compiled method
full of the zeroes and ones a loop counter emits, it is the difference between
an allowance of eight delivering five useful values and delivering eight.

---

## M17. The same answer, wherever it is asked ✅

*One way to ask what a codebase uses, and that answer in the document that gets
handed over.*

### The command and the route disagreed by five hundred uses

`ApiSurface` takes the catalogue as an argument because it has to be able to
abstain: asked about a package nobody recorded, it must exclude nothing rather
than guess. That argument had a default, and a default is a second answer
waiting to be given.

It was given. The route built the catalogue and passed it. The command passed
nothing, took the abstaining behaviour, and printed a different number for the
same directory:

```
route     Microsoft.AspNet.Mvc: 3877 use(s) of 198 type(s), across 365 file(s)
command   Microsoft.AspNet.Mvc: 4379 use(s) of 267 type(s), across 365 file(s)
```

Thirteen per cent apart, and the larger one is the number M13 was built to stop
reporting, because it counts types the framework still supplies as a dead
package's work. The README published 3,877 at the time, so the command contradicted the
project's own measurement.

The rule M14 wrote down applies without a word changed: **the same program
giving different answers depending on how it was asked is worse than one that
refuses to answer.** So there is one way in now, `Surfaces`, which loads the
catalogue and applies it. The argument below it has no default any more, and a
caller that means to abstain says so out loud. The only six call sites that had
to change were tests, each of them a test of abstaining, and each now says so.

### The report did not contain the number that decides the size

Everything the assessment counted was what a solution *declares*: how many
projects reference a package, how many are unclassified. Its first repair step
read "78 packages nobody has classified, each one either a non-event or a
rewrite" and stopped there, while the tool already knew which of the two, and
would say so if you ran a second command.

So the reading is in the assessment, and the document has a section for it. On
Orchard:

```
3,634 uses of 190 types, across 365 files. 24 types and 134 files carry four
fifths of it.

Microsoft.AspNetCore.Mvc covers 76% of those calls. 138 types, 884 calls,
are not in the catalogue at all: unknown, which is not the same as fine.

Asked of the framework itself rather than of the catalogue: 17 types of that
column exist inside Microsoft.AspNetCore.Mvc under the same name, which is a
lead worth checking. 15 types exist somewhere unrelated, which is a trap
rather than an answer. 106 types the framework does not have at all. That
leaves 121 types still to decide.
```

Those are the figures M14's acceptance test used, now in the artefact rather
than behind a second command. It costs a second walk over every source file:
the report on Orchard went from 4.8 to 8.2 seconds and from 363 to 478 lines,
and a caller that does not want to pay for it can turn it off.

### Three items claimed to be missing and were not

`NEXT.md` listed hybrid search, streaming, and incremental indexing as things
this does not do yet. All three are in the code: an FTS5 index fused with the
cosine ranking, `text/event-stream` with token events, and an ingestion ledger
that skips a file whose content hash has not moved. The file that tells a reader
what is missing was wrong in three places out of seven, which is worse than
having no such file: a roadmap nobody trusts is read as marketing.

What is left there now is four items, one of which is a trade rather than a
task.

---

## M18. Marked against a migration that already happened ✅

*Nobody can evaluate a migration tool. This one can be.*

There is no correct answer to compare a migration tool against, which is why
every tool in this space is sold on adjectives. A codebase that exists in both
states removes the problem: the tool says something before the fact, and the
finished code says what the team did.

`hindsight <before> <after>` reads syntax on both sides and compiles nothing, so
it runs on a legacy tree that does not build, which is every legacy tree.

### The pair had to be verified before it could mark anything

**nopCommerce 3.90 to 4.00 is a real port.** 31 projects on `v4.5.1`, none
SDK-style, 425 files importing `System.Web` and none importing ASP.NET Core,
becoming 26 projects all SDK-style, two files left on `System.Web` and 416 on
ASP.NET Core. 252,270 lines becoming 260,319: **three per cent**, which is the
signature of a port rather than a rewrite, and what makes the correspondence
meaningful down to the file.

**eShopModernizing is not a framework-migration oracle**, and was proposed as
one here before being measured. Every one of its *Modernized* solutions still
targets .NET Framework 4.7.2 with zero files importing ASP.NET Core: Microsoft's
"modernizing" there means containers and Azure services, not a port. Marking a
tool against it would have measured the wrong decision. A pair has to be checked
for what kind of migration it was before it can mark anything.

### What the tool said, and what the team did

| package | before | proposed | covered | became | |
|---|---|---|---|---|---|
| `Microsoft.AspNet.Mvc` | 4,860 uses, 338 files | `Microsoft.AspNetCore.Mvc` | 68% | ported, 328 files | agreed |
| `Microsoft.AspNet.WebPages` | 72 uses, 4 files | `Microsoft.AspNetCore.Mvc.Razor` | 8% | ported, 4 files | **disagreed** |
| `Newtonsoft.Json` | 33 uses, 8 files | `System.Text.Json` | 6% | kept, 92 uses | agreed |
| `EntityFramework` | 74 uses | nothing recorded | | kept, 67 uses | not scored |
| `Autofac` | 46 uses | nothing recorded | | kept, 28 uses | not scored |

**Two of three.** Three claims on one product is thin, and the rate is reported
as a count rather than a percentage for that reason.

> **Superseded by M19.** Two more pairs took this to four of ten, and laid out
> together the ten claims showed that coverage was never predicting the
> decision. What it scores now is whether the package can live on the target at
> all. The numbers here are what was measured at the time and are left as they
> were.



**The miss is the useful part.** `Microsoft.AspNet.WebPages` was covered 8 per
cent and ported anyway, because it was four files. The coverage number says how
much of a move is a substitution and says nothing about how much there is to
move, and a dependency small enough gets rewritten whatever the number says.
Coverage without volume is half a judgement.

**A package the catalogue is silent about is never scored**, neither in the
numerator nor the denominator. It also names the gaps: `EntityFramework` has no
successor recorded, and EF6 to EF Core is among the most common migrations
there is.

### What the pair says about migrating at all

The team changed exactly one thing. They moved the web framework and kept the
ORM, the container, the JSON serialiser and the runtime: version 4.00 still
targets `net461`, and 22 files still import `System.Data.Entity` with none
importing EF Core. A professional team on a 250,000-line commercial product did
it in one slice, on .NET Framework, and moved the runtime later.

That is the strangler fig performed by people who had to ship, and it is the
direct answer to anyone expecting a codebase to arrive on modern .NET in one
move.

---

## M19. What is forced, and what is a choice ✅

*The tool was being marked on a question it was not answering.*

M18 scored the coverage number: above half covered, a team would substitute;
below it, they would not. Two of three on nopCommerce. Two more pairs were
added, **Umbraco 8.18 to 9.0** and **SmartStoreNET 4.2 to Smartstore 5.0**, both
verified as real ports, and the score fell to **four of ten**, worse than a coin.

Laid out, the ten claims were not ambiguous at all:

| | coverage | became |
|---|---|---|
| six `Microsoft.AspNet.*` packages | 8, 41, 45, 49, 56, 68% | every one of them moved |
| four chosen libraries | 3, 6, 12, 17% | two kept, one straddling, one moved |

A team moving to ASP.NET Core has no choice about `System.Web`. They port it
whether a successor covers eight per cent of their calls or sixty-eight.
**Coverage never predicted the decision. It estimates how much of a move is a
substitution rather than a rewrite, which is a cost, and it was being marked as
a behaviour.**

### The judgement that does predict it

`data/stranded.json`, hand-written like every other judgement here: does this
package have any life at all on the target? `System.Web.Mvc` does not and never
will. `Newtonsoft.Json` ships netstandard2.0 and runs unchanged, so moving off
it is a choice somebody makes rather than one the runtime makes for them.
Absence means unknown, and nothing is claimed about it.

### Held out, because the hypothesis came from the data

Three pairs generated it, so re-measuring on them is in-sample and says little.
**Orchard to Orchard Core was never looked at until the model was frozen.**

| | forced | a choice |
|---|---|---|
| nopCommerce *(port)* | 2 of 2 | 3 of 3 |
| Umbraco *(port)* | 5 of 5 | 1 of 1 |
| Smartstore *(port)* | 3 of 3 | 2 of 4 |
| **Orchard *(rewrite, held out)*** | **5 of 5** | **0 of 4** |
| | **15 of 15** | **6 of 12** |

**What the runtime forces held fifteen times out of fifteen**, across four real
migrations, two of which the model had never seen. What a team was free to
decide either way held six times out of twelve, which is to say it is not a
prediction.

And its failure is not noise. In the three ports it was six of eight, because a
port keeps what still runs. In the rewrite it was none of four, because a
rewrite keeps nothing: Orchard Core grew thirty-one per cent in lines and picked
new libraries for everything, Serilog over log4net, System.Text.Json over
Newtonsoft, its own container over Autofac.

### So the two are never blended

A single rate of twenty-one out of twenty-seven would hide the only thing worth
knowing. The command prints two, and says of the second that it is not a
prediction and should not be read as one.

That is also the honest shape of what this tool can sell. **It can tell you with
certainty what you have no choice about, and price it. What you have a choice
about is yours, and it should stop pretending otherwise.**

### Two smaller things

**A remainder is not a dependency.** A package on its way out leaves one:
Umbraco's MVC went from 1,106 uses to 18. Judged by presence, that reads as
kept. The cut is a fifth, and the number barely matters, which is the point:
across four ports the packages on their way out sat at 1.6, 2.5 and 11 per cent
and the ones kept at 78 or more, several above a hundred because they were used
harder afterwards. Nothing measured falls in between.

**The catalogue lookup was written twice** and was about to be written a third
time. It is in `Catalogues` now, one copy, which matters because getting it
wrong is silent: a single-file publish extracts to a temporary folder, and that
is how M14 shipped a binary where every package came back with no candidate.

---

## M20. One level down, and what was found there ✅

*The package question was: does this dependency move. This one is: when the
catalogue says `ActionResult` becomes `IActionResult`, is that what a team
wrote?*

`correspondences <before> <after>` reads both trees and holds every recorded
correspondence the old code exercised against what turns up in the new one.

### Going a level down found a defect in the level above

The candidate list came back full of nopCommerce's own attributes:
`FormValueRequired`, `PublicAntiForgery`, `AdminAntiForgery`. A solution's own
code, counted as ASP.NET MVC.

C# declares `FormValueRequiredAttribute` and every use of it is written
`[FormValueRequired]`. The reader already knew this and recorded uses under the
short spelling on purpose. **The declaration side was never given the same
treatment**, so the declared name never matched the used one and a solution's
own attributes went out as the package's.

| | before | after |
|---|---|---|
| nopCommerce 3.90, `Microsoft.AspNet.Mvc` | 4,860 uses | 4,443 |
| Orchard, `Microsoft.AspNet.Mvc` | 3,877 uses of 198 types | **3,634 of 190** |
| Orchard, types carrying four fifths | 28 | **24** |
| Orchard, covered by the successor | 71% | **76%** |
| Orchard, still to decide | 129 | **121** |

Six per cent on the figure this repository has published since M13. **Every test
passed.** There was no test: the reader's own comment described the two
spellings and only half the rule was implemented, and nothing anywhere compared
a name against a real migration until now. The published numbers above are
corrected in place; where an older section used them to tell the story of a
different defect they are left as they were, marked.

### What the catalogue got right

Across the four pairs, counting only correspondences the old code actually used:

| | confirmed | recorded | candidates found |
|---|---|---|---|
| nopCommerce | 22 | 35 | 21 |
| Umbraco | 33 | 47 | 27 |
| Smartstore | 46 | 59 | 34 |
| Orchard | 36 | 54 | 28 |
| | **137** | **195** | **110** |

**A counterpart that does not turn up is not a wrong entry.** The team may
simply never have needed it, and nothing here can tell the two apart. So the
two are reported apart and the second is never called an error.

### And what it is missing

**110 names** the old code used, the catalogue does not mention, and the
successor has under the same name. `TagBuilder`, `ViewContext`,
`ModelBindingContext`, `HelperResult`, `ActionDescriptor`: types that kept their
name and changed namespace, which is transcription rather than judgement, and
transcription is what leaves a hundred types sitting in the column nobody has
looked at.

They are **candidates for a person to sign, never entries written back by a
machine.** A name surviving into an unrelated namespace is the trap M13 exists
to catch, which is why only the successor's own namespace is looked in. They
also carry the attribution approximation the README has always declared: a file
importing two packages cannot be split between them by syntax alone.

---

## M21. How much of it the tool can actually do ✅

*Comparing what the tool **produces** to what the team produced, which is a
different question from comparing what it says.*

M18 to M20 marked the tool's advice. This one marks its work. On nopCommerce
3.90 the answer was **3 project files out of 31**, where the team converted all
twenty-six of theirs. Twelve per cent of the mechanical work that was available.

### The reasons it refused were generated code

Twenty-eight projects were turned down. Three rules had been written as though
every case were the hard case:

**A project that cannot port was refused the format change.** Those are two
different questions: the SDK format is about the file, porting is about the
packages. The team settled it by doing exactly that, putting all twenty-six
projects into the SDK format and leaving them on `net461` with EF6.
`Nop.Web` was among the twenty-eight this tool called blocked, and in 4.00 it is
SDK format on .NET Framework. The fact is kept and demoted to a caveat: the work
is done, and the note says what it does not fix.

**Empty build targets counted as build logic.** Visual Studio writes an empty
`BeforeBuild` and `AfterBuild` into every pre-SDK project ever made. Of
nopCommerce 3.90's 94 targets, **61 were empty or commented out**. A target with
no steps has no steps to lose; one with a task in it still refuses, because
dropping build logic silently is the one thing this conversion must never do.

**Imports out of the packages folder counted as bespoke.** The package brought
its own targets and the project file was edited to point into the restore
folder. PackageReference brings them in on its own. That was **26 of the 30**
non-standard imports, every one of them the same file. The guard NuGet writes
for them goes with them.

### Measured

| | before | after |
|---|---|---|
| nopCommerce 3.90, projects converted | 3 of 31 | **29 of 31** |
| what the team converted | | 26 |

`git apply --check` accepts the twenty-nine-project patch against the real tree.

**The two it still refuses are `Nop.Web` and `Nop.Admin`**, the two web
applications, and for reasons that are real: genuinely custom build targets, a
project flavour, and the `Microsoft.WebApplication.targets` imports. Those are
the two projects where the hosting model changes, which is exactly the work a
person should be doing.

### And the report was saying something else

It counted a project convertible only when nothing it referenced was a dead end,
while the conversion had stopped caring: a report saying three sat above a
command offering twenty-nine. On Orchard it ran the other way, sixteen promised
against thirteen the conversion accepts. Both are now one question with one
answer, which is the rule M17 exists for and which had already been broken again.

### How far this can go, since that is the objective

Measured on nopCommerce, by weight of the artefact:

| | 3.90 | 4.00 | share |
|---|---|---|---|
| project files and config | 12,932 lines | 1,671 | **3.9%** |
| Razor views | 67,008 | 66,673 | 20% |
| C# source | 252,270 | 260,319 | 76% |

Two things follow. The **Razor views came through nearly untouched**, half a per
cent apart: a fifth of the product needed almost no work at all. And the
mechanical project plumbing, now at ninety-four per cent of projects, is four
per cent of the lines.

So a target of "ninety-nine per cent automated" is the wrong shape, and aiming
at it produces a dishonest tool. **The ceiling is what is mechanical, and
mechanical is not most of the lines.** The honest objective is different and
better: everything mechanical done, everything else named, priced and handed
over as a short list. A decision-maker facing twenty questions that each carry a
measured cost and a recommendation is under far less load than one facing three
with nothing attached. The load is the uncertainty per decision, not the count.

---

## M22. The two conversions that have to compose ✅

*M21 raised one of them and broke the pair.*

The SDK conversion drops references pointing into the packages folder and says
PackageReference will replace them, so run on its own it produces a project file
with no packages in it. That was survivable while it offered three projects. It
offered twenty-nine after M21, and the packages conversion offered three,
**declining the rest silently on a bare `return null`** for the same reason the
SDK conversion had just stopped using: a dependency with no path to modern .NET.

So twenty-six of the twenty-nine would have come out unrestorable, and the
advice printed beside them, *convert packages first*, could not be followed.

A package with no future still has to be declared, and declaring it the modern
way costs nothing. Demoted to a caveat, as next door: **3 of 31 becomes 31 of
31.**

### Checked by running both, with real git

On the actual nopCommerce 3.90 tree: `packages`, applied, then `sdk`, applied.
`Nop.Data.csproj` comes out as

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net451</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="EntityFramework" Version="6.1.3" />
    <PackageReference Include="EntityFramework.SqlServerCompact" Version="6.1.3" />
    <PackageReference Include="Microsoft.SqlServer.Compact" Version="4.0.8876.1" />
  </ItemGroup>
  ...
```

Complete, and the packages.config is gone. The order is now printed by the
command rather than left to be discovered, and there is a test that applies both
patches with git and reads the file that comes out.

### Held against what the team wrote

Their `Nop.Data.csproj` in 4.00 differs in exactly four places, and every one of
them is a decision rather than missing work:

| | this tool | the team |
|---|---|---|
| target | `net451`, faithful to the original | `net461` |
| package versions | 6.1.3, faithful | 6.2.0 |
| `EntityFramework` | declared | dropped, transitive on their target |
| package metadata | none | copyright, description, licence, repository |

**And the tool produced their first decision on its own.** The packages
conversion warns, on all thirty-one projects, that the solution targets v4.5.1
and PackageReference wants 4.6.1. nopCommerce 3.90 is v4.5.1 and 4.00 is
`net461`. The target bump was not housekeeping, it was the prerequisite, and
this says so before anybody has moved.

That is the shape argued for at the end of M21: everything mechanical done, and
what remains handed over named, with its reason attached. It is currently said
thirty-one times as a footnote, which is one decision printed as thirty-one
pieces of noise, and that is the next thing worth fixing.

---

## M23. One decision, said once ✅

*The objective was to take load off the person deciding. The conversions were
adding to it.*

On nopCommerce 3.90 the notes beside a patch came to roughly **two hundred
lines for thirty-one projects**: eight sentences repeated, one per project, in
the order the projects happened to be walked. Among them, printed thirty-one
times, was the line that decides the whole shape of the work, that
PackageReference wants a target this solution does not have.

**A decision repeated thirty-one times is one decision and thirty pieces of
noise.**

### A caveat now has a key and a nature

The key is what makes the same caveat from thirty-one projects one line. It is
never shown: the sentence carries counts and package names, so no two projects
write it the same way, and grouping by the sentence would leave things exactly
as they were.

The nature is the part that matters. **A consequence** is something the
conversion did and the reader checks: build configurations dropped, items now
globbed from the folder. **A decision** is something nobody can settle for
them. There are five, and they are now named once each:

| | |
|---|---|
| `target-below-461` | PackageReference wants 4.6.1 and this solution targets less |
| `still-blocked` | packages with no path forward, whatever the format change does |
| `password-in-connection-string` | copied as found, and it must not be committed |
| `duplicate-keys` | declared twice with different values, and which won depended on the host |
| `undeclared-keys` | read by the code and declared nowhere, so no value was invented |

### Measured

| | before | after |
|---|---|---|
| `convert packages` notes | ~200 lines | **11** |
| `convert sdk` notes | ~150 lines | **17** |

Decisions print first under their own heading, and where the sentences within a
group differ the line says **one shown**: being told twenty-nine projects raised
something is only useful alongside whether they raised it about the same thing.

### One thing found in my own work

The first version of the test that pins which caveats are decisions built its
own `Caveat` objects and asserted the flags it had just set. A test that cannot
fail, which is what M20 found in this repository three milestones ago and fixed.
It now reads the marks off a real conversion, and removing one makes it fail.

---

## M24. A decision nobody wrote down is a decision nobody made ✅

*M23 named five of them. They were named in a terminal.*

The report is the one artefact somebody keeps. It carried an ordered plan and
**none of the decisions that plan depends on**. Searched for them, on
nopCommerce 3.90:

| | mentions in the report |
|---|---|
| `4.6.1` | 0 |
| `PackageReference` | 0 |
| a connection string carrying a password | 0 |
| keys declared twice with different values | 0 |
| keys read by the code and declared nowhere | 0 |

Its second repair step said *convert the project files that can take the modern
format, 29 projects*, without saying that all thirty-one have to move to 4.6.1
first for the packages half to work at all.

### They are read off the conversions, not restated

The assessment runs them and keeps the caveats marked as decisions. Nothing is
written twice, so a caveat that stops being a decision stops appearing, and one
that becomes a decision appears without anybody remembering to add it.

On nopCommerce the document now says, before the order because the order assumes
it:

```
## What only you can decide

**31 projects.** Targets v4.5.1. PackageReference is supported from 4.6.1
onwards; on an older target the restore may behave differently.

**28 projects, one shown.** Still depends on packages with no path to modern
.NET: Microsoft.AspNet.Mvc, Microsoft.AspNet.Razor, ...

**Across the solution.** 8 key(s) are read by the code and declared nowhere.
They are not in this file either, because inventing a value is the one thing
that would make it wrong.
```

Orchard gets three of its own, including one key declared in more than one
config file with different values, where which one won depended on which
application loaded them.

### The grouping moved out of the terminal

It lived in the command that printed it, which made it look like a concern of
the terminal. Two callers group caveats now, and two groupings would eventually
disagree about what counts as the same caveat, so it sits beside `Caveat` in the
analysis where both can reach it.

### What it costs

The assessment now runs the packages conversion and the configuration survey as
well. On Orchard the report went from **8.2 to 12.4 seconds**. It is a document
somebody generates once and reads for an hour, and there is no section without
something to put in it: a heading over an empty list would read as *there is
nothing to decide*, which is never true.

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
