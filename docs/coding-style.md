# Coding style — hermes-winui

The conventions any code added to this repo should follow. Lifted from
the project owner's hand-written style + engineering hygiene that
prevents regressions. If you're an LLM working in this repo: match
this, not your defaults.

## Naming

- **camelCase for local variables and method names**, even in languages
  where snake_case is idiomatic. Locals: `replacementCmd` not
  `replacement_cmd`. Methods: `executeSolution` not `execute_solution`.
- **PascalCase for classes/types**, **camelCase for folders**
  (`buildCmd/`, `validateCmd/`).
- Respect language-mandated conventions for things the language owns —
  C# private fields stay `_underscore`, Python public APIs stay
  snake_case when a library expects it. Everything *you* name → camelCase.
- **Human-readable names always.** No `mgr`, `proc`, `ctx`, `hdlr`,
  `tmp2`, `dataX`. Names read like the noun they represent:
  `windowStateManager`, `transcriptScrollViewer`, `pendingHandlers`.
- Single-letter loop variables (`i`, `c`) are fine in tight loops; once
  a variable lives past ~5 lines it earns a real name.
- **Pick one naming convention per file and stick to it.** Consistency
  inside a single file beats matching outside conventions.

## File organization

- **Folder per command/feature.** New behavior = new folder or new
  file, never grow a catch-all dispatcher.
- **Thin entry / fat worker split.** CLI declarations / wire-up live
  in one file, the actual work in a sibling Functions file. Replicate
  this pattern anywhere there's a clear declaration-vs-implementation
  divide.
- **One handler per file.** Extensible by adding files, not by editing
  shared dispatchers.

## Decomposition & function size

- **Decompose by concept, not by line count.** Split when there's a
  coherent subtask that deserves a name (`computeStickyOffset`,
  `validateManifest`), or when reading top-to-bottom requires you to
  scroll back and forget what you were doing. A linear 40-line function
  flowing in one direction is often clearer than 4 helpers you have to
  chase across the file.
- **Rough thresholds (smells, not hard limits):** past ~80 lines or
  3+ levels of nesting → suspect. 500 lines → unambiguously broken.
  The fix isn't "split into 10 equal chunks" — find the real concepts
  hiding inside and lift them out as named units.
- **Helpers used in exactly one place are fine** when they carry
  meaningful naming weight. They are NOT fine when they're "I
  extracted this to feel virtuous" and the parent now reads worse.
- **Concrete classes over framework patterns.** Prefer a class with
  explicit methods over interface + strategy + factory dances. Add the
  abstraction layer only when there are genuinely 2+ implementations
  or a real testing seam that justifies it. Decompose into *things*,
  not *frameworks*.

## Code shape

- **Named intermediate locals over chain golf.** If 2+ method calls
  feed into each other, name the intermediate. Naming intermediate
  results is part of the documentation.
- **Defensive type narrowing at boundaries.** Validate at inputs from
  external sources (file reads, network responses, user input); skip
  it on purely-internal hops where the type is obvious.
- **Vertical breathing room.** Blank line between methods, blank line
  inside a method between conceptual phases.

## Comments

- **Sparse but conversational.** Most lines don't need a comment. When
  a comment does exist, it explains *why* something is shaped a
  non-obvious way — not what the code does (the name should do that).
  Good: "Re-arm throttle because each change should reset the 150ms
  window." Bad: "// substitute variables" on a method called
  `substituteVariables`.
- **No multi-paragraph XML / docstring blocks on every public method.**
  A one-line `<summary>` for a genuinely non-obvious public API is
  fine. Exhaustive `<param>` / `<returns>` on self-explanatory methods
  is noise that makes code feel auto-generated.
- **TODOs must be actionable.** A scoped, specific TODO is fine
  ("TODO: handle non-debian distros"). "Maybe revisit this someday"
  belongs in an issue, not a code comment.
- **Strip commented-out alternatives and debug prints before commit**
  unless the commented line earns its keep as a one-sentence
  intent-comment explaining the choice.

## Errors & UX

- **Error messages name the thing AND the context.** "FAILED at step
  'X': {ex.message}" — the reader needs to know *where* in the flow
  the failure happened, not just *what* went wrong.
- **No silent fallbacks.** Don't swallow exceptions to keep going
  unless the fallback is genuinely the right behavior. Errors propagate
  with context, they don't get hidden.
- **`begin / try` blocks include a narrative.** Print what failed, what
  completed before the failure, and a status. The user sees a story,
  not a stack trace.
- **Dry-run / preview is first-class** for any command with side
  effects. Threaded through as a constructor arg or method param with
  a default. Not a debug afterthought — a real feature.
- **Colorized narrative output** where applicable. Green for success,
  red for error, yellow for warning, cyan for dry-run, magenta for
  validation. Output is part of the UX.

## Cleverness budget

- **Macros / metaprogramming when they pay for themselves.** Reach for
  metaprogramming when it eliminates real maintenance cost (e.g. an
  auto-dispatch pattern that removes a switch you'd otherwise have to
  edit on every new handler). Don't reach for it because it's clever.

## Diff hygiene

- **Keep the diff focused.** When fixing a bug, don't drive-by-refactor
  unrelated code in the same patch — even if it's tempting. Separate
  commits for bug fixes vs style cleanup. Makes review and bisect
  actually work.

## Summary

Write the code an experienced solo developer would write if they had a
code-review buddy who gently pushed back on commented-out alternatives,
leftover TODOs, and mixed naming — who insisted that the few comments
that do exist earn their keep by explaining intent — and who decomposed
by *concept*, not by line count.
