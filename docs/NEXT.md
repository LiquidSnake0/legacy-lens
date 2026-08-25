# What this does not do yet

In rough order of how much each would improve the answers.

---

## 1. Recalibrating the score floor on a real codebase

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

## 2. Opening a citation

The frontend exists now (Angular 22, in `web/`), and it shows each citation with
its path, line range and score. What it does not do is open them: clicking a
citation should show the excerpt, or hand off to an editor via a `vscode://`
link. Right now the reader has to find the file themselves, which is most of the
friction left in the loop.

## 3. Reranking

A cross-encoder rescoring the top twenty candidates, comparing each against the
question directly rather than through separately-computed vectors. Markedly more
accurate and markedly slower, worth it only once there is hardware to spare.

## 4. Attributing a type to the package it actually came from, exactly

The usage surface counts a type as a package's when it appears in a type
position in a file that imports that package. That is cheap, needs no
compilation, and works on a solution that does not build, which is why it is
what it is.

**The cheap half of this shipped with M13.** Names the framework still supplies
itself are no longer attributed to a dead package, and a test framework's own
attributes are not either. That was measured on Orchard: of the 219 types the
catalogue never mentioned for `Microsoft.AspNet.Mvc`, 69 were base library types
the file merely used, and the most-used name in the "exists nowhere" column was
`Test`, which is NUnit's attribute.

What is left is the exact answer, and it is not a cheaper version of the same
thing. Deciding that a type came from one package rather than another needs
resolved symbols, which needs the solution to compile, which is the one property
that makes this usable on inherited code at all. So this stays written down
rather than planned: taking it would trade the tool's reach for its precision,
and the reach is the point.

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
