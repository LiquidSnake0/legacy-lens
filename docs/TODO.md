# The four pieces left to implement

Everything around these is done — HTTP, storage, containers, CI. These four are
the ones a technical interviewer will actually ask about, so they are the ones
worth writing yourself.

Each has failing tests already written. `dotnet test` tells you when you are
done.

---

## 1. `CodeChunker.Split()` — how do you cut up source code?

`src/LegacyLens.Api/Ingestion/CodeChunker.cs`

Cutting every 500 characters is what most tutorials do, and it destroys the
thing that makes code retrievable: a method split across two chunks is now two
fragments that mean nothing on their own.

Things to decide, and to be able to defend:

- **Where to cut.** Blank lines and brace depth returning to zero are cheap
  approximations of "end of a logical unit". A real parser (Roslyn for C#) is
  better and costs a dependency per language.
- **How big.** The embedding model has a token limit. Too small and a chunk
  carries no context; too large and the vector averages out into mush.
- **Overlap.** Repeating a few lines between neighbouring chunks stops a
  definition from falling exactly on a boundary. It also inflates the index.
- **What to attach.** A chunk that says `return _rate * qty;` is useless. The
  file path and enclosing type name usually need to be prepended to the text
  that gets embedded — the chunk you *store* and the text you *embed* do not
  have to be identical.

## 2. `VectorMath.CosineSimilarity()` — how do you compare two vectors?

`src/LegacyLens.Api/Storage/VectorMath.cs`

Twenty lines. Expect to be asked why cosine and not Euclidean distance, and to
know that on already-normalised vectors the two rank identically — so the
answer is about cost and about whether magnitude carries meaning.

Worth doing second: `System.Numerics.Vector<float>` makes this several times
faster for a handful of extra lines, and it is a good thing to have measured
rather than assumed.

## 3. `PromptBuilder.Build()` — how do you stop it inventing things?

`src/LegacyLens.Api/Generation/PromptBuilder.cs`

This is where the hallucination question is answered, and the answer is
concrete, not hopeful:

- Instruct the model to answer **only** from the supplied excerpts.
- Instruct it to say it does not know when the excerpts do not cover the
  question. Models comply with this far better when the instruction is explicit
  and repeated at the end of the prompt.
- Label every excerpt with its path and line range, and require citations in the
  answer. A claim without a citation is a claim you can spot.
- Budget the context. Chunks are appended until the token budget is spent, best
  match first — so truncation drops the worst evidence, never the best.

## 4. `Retriever.RetrieveAsync()` — how many chunks, and which?

`src/LegacyLens.Api/Generation/Retriever.cs`

Top-K alone is a blunt instrument. Two cheap improvements worth having an
opinion about:

- **A score floor.** If nothing scores above ~0.4, the honest response is that
  the codebase does not appear to contain an answer — better than feeding the
  model six irrelevant chunks and letting it improvise.
- **Per-file capping.** Ten chunks from one file crowd out the file that
  actually holds the answer.

---

## Then

- Angular frontend in `web/` — `ng new web --routing --style=scss`
- Streaming responses, so the answer appears as it is generated
- `docker compose` profile that runs the whole thing on a mounted repo
