# Roadmap

Legacy Lens answers questions about code. The next milestones extend that in one
direction: **reading a codebase and reporting what is actually there.**

Nothing here writes code. Editing agents already exist and are built by teams
with years of head start. The gap worth filling is elsewhere: analysis of
inherited .NET, running locally, for organisations that are not allowed to send
their source anywhere.

A rule that holds across every milestone:

> **The tool never guesses what it can measure.**
> Roslyn and git supply the facts. The model turns them into sentences. Anything
> the model asserts on its own carries a citation, or it does not ship.

---

## M0. Hold up on a real codebase

*The prerequisite. Nothing below is demonstrable until this is true.*

Indexing this repository takes 48 seconds for 21 files. A real legacy solution
has 500 to 2000 files. Linear scaling puts that in hours, which is not a demo,
it is an overnight job.

- Index a genuinely old .NET Framework solution and measure honestly
- Batch and parallelise embedding calls where Ollama allows it
- Incremental indexing: chunk ids are already stable, so skipping files whose
  mtime predates the last run is straightforward
- Persist the projection so a restart does not re-index

**Done when** a 1000-file solution indexes in minutes and re-indexes in seconds.

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

Crossing three signals that are all already on disk:

| Signal | Source |
|---|---|
| Size and complexity | Roslyn: lines, cyclomatic complexity, nesting depth |
| Change frequency | git log over the last 24 months |
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

Roslyn gives types, members, inheritance and interface implementations as facts.
The model's only job is grouping and naming the clusters.

- One diagram per namespace or per feature, not one unreadable diagram
- Inheritance and implementation from the symbol graph
- Mermaid `classDiagram` output

**Effort:** a weekend, most of it spent deciding what to leave out.

---

## M5. Demo comfort

*Small things that decide whether a demonstration lands.*

- **Streaming.** A CPU-bound model takes tens of seconds. Watching nothing
  happen reads as a crash. Server-sent events, token by token.
- **Open a citation.** Clicking a source should show the excerpt or hand off to
  an editor. Today the reader has to find the file themselves, which is most of
  the friction left in the loop.
- **A single command to start.** `docker run` and nothing else.

**Effort:** an evening each.

---

## Deliberately out of scope

**Writing or refactoring code.** Cursor, Copilot Workspace, aider and others do
this, with teams behind them. Competing there means losing quietly.

**Cloud hosting.** The entire premise is that the code does not leave the
machine. A hosted version would contradict the one thing this tool offers.

**Languages beyond .NET, at first.** The chunker is already language-agnostic,
but Roslyn is not. Depth on one ecosystem beats a shallow pass over five.
