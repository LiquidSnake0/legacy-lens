# What this does not do yet

In rough order of how much each would improve the answers.

---

## 1. Hybrid search

The largest gap, and the one specific to code.

Vector search is weak on rare identifiers. Someone typing `PriceEngine` wants
that exact token, and an embedding, which encodes meaning, has no particular
reason to favour an exact match on a proper noun it never saw during training.
Lexical search is simply better at that job.

The fix is to run both and fuse the scores. SQLite ships FTS5, so this costs no
new dependency: a full-text index over the same chunks, a BM25 score, and
reciprocal rank fusion against the cosine ranking.

Expect this to matter more than any amount of tuning elsewhere.

## 2. Recalibrating the score floor on a real codebase

Done once, on this repository. Four answerable questions scored 0.59 to 0.68 at
the top; three unanswerable ones scored 0.00, 0.46 and 0.47. The floor sits at
0.52, inside the gap. The original 0.4 was intuition, and it filtered nothing ,
embedding spaces are anisotropic, so an unrelated question still scores near 0.5.

Seven questions against a 21-file repository is thin, and the number is tied to
`nomic-embed-text`. Repeat the measurement on a large legacy codebase, and again
after any change of embedding model.

If the two clusters ever overlap, an absolute floor cannot separate them and a
relative one is needed as well, discard anything below some fraction of the best
score for that query, which adapts to how hard the question is.

## 3. Opening a citation

The frontend exists now (Angular 22, in `web/`), and it shows each citation with
its path, line range and score. What it does not do is open them: clicking a
citation should show the excerpt, or hand off to an editor via a `vscode://`
link. Right now the reader has to find the file themselves, which is most of the
friction left in the loop.

## 4. Streaming

`OllamaChatClient` asks for `stream: false` and waits. On a CPU-only machine a
3B model takes tens of seconds to produce an answer, and watching nothing happen
for that long feels broken. Server-sent events from the API, token by token.

## 5. Reranking

A cross-encoder rescoring the top twenty candidates, comparing each against the
question directly rather than through separately-computed vectors. Markedly more
accurate and markedly slower, worth it only once there is hardware to spare.

## 6. Incremental indexing

Re-indexing currently walks and embeds everything. Chunk ids are stable, so
skipping files whose modification time predates the last index is
straightforward and turns a full re-run into seconds.

---

## Known approximations

Written down because they are deliberate, and because someone reading the code
will find them anyway.

- **Brace counting ignores strings and comments.** `CodeChunker` counts `{` and
  `}` wherever they appear. It is picking somewhere plausible to cut, not
  parsing; a real parser costs one implementation per language, in a tool whose
  purpose is reading code in languages nobody chose.
- **Indentation-based languages chunk poorly.** Python has no braces, so the
  boundary quality heuristic falls back to blank lines alone. Tracking
  indentation depth would fix it.
- **Brute-force similarity search.** Deliberate up to roughly a million vectors
 , see the README. `IVectorStore` exists so that changing it is a new class.
- **The over-fetch multiplier in `Retriever` is currently free.** The store
  scores every chunk regardless. It is there for a store that does not exist yet.

## Observed, not yet fixed

- **A 1.5B model follows the "say you do not know" rule badly.** On a question
  it had answered correctly from the excerpts, `qwen2.5-coder:1.5b` appended a
  contradictory closing sentence claiming the answer was not present. The
  retrieval and the citations were right; the model tacked the escape hatch on
  as a formula. Larger models do this less. Worth measuring across model sizes
  before rewording the prompt, since the prompt is not obviously at fault.
