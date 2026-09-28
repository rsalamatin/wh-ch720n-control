---
name: code-comments
description: Comment policy for this repository's C# code — which comments and XML docs to keep and which to delete. Use when writing or editing C# in src/ or tests/, when reviewing code, or when asked to clean up / prune comments.
---

# Code comments policy

The default is **no comment**. Names, types and tests carry the meaning. A comment must earn its place by telling the reader something the code cannot.

## Keep

- **Why, not what.** Non-obvious reasons, constraints or trade-offs, for example why a lock is *not* disposed or why a buffer is bounded.
- **Protocol and hardware facts** that are not visible in the code:
  - behaviour seen on the real WH-CH720N (for example "device retransmits un-ACKed frames");
  - deviations from the spec or the reference, with the source (`ProtocolV2.cpp:129`).
- **Safety notes.** Opcode 0x22 is POWER OFF on V1. Generation gating. Anything that prevents a destructive command.
- **Concurrency and lifetime invariants.** Who owns what, what must happen before what, which thread something runs on.
- **Justification for an intentional rule break**, such as catching `Exception` at a top-level event boundary.
- **XML docs on public contract types** that other layers code against: `ITransport`, `IHeadphoneDevice`, `DeviceState`, `ProtocolSession`, `V2CommandSet`. Keep them only when they state something the signature doesn't:
  - semantics ("returns 0 on remote close");
  - thrown exceptions;
  - value ranges;
  - threading.

## Delete

- Comments and XML docs that restate the name or the code:
  - `/// <summary>Creates the exception.</summary>`
  - `// increment index`
  - `/// <summary>Gets the name.</summary>`
- XML docs on `private`/`internal` members, unless they state a non-obvious invariant. If they do, turn them into a short `//` comment.
- `/// <inheritdoc />` and empty or boilerplate `<param>`/`<returns>` tags.
- Process notes:
  - track or agent names ("Track A");
  - "fixed per review";
  - "added for Phase 2";
  - dates and authors.
- Commented-out code, section banners (`// ---- helpers ----`), and `#region`.
- `// Arrange` / `// Act` / `// Assert` markers in tests. Separate the three phases with blank lines instead.
- TODOs. Either do the work or describe the limitation as a why-comment.

## Style

- Keep comments short, usually one line. Put them above the code they explain, not at the end of the line.
- If you need a comment to explain *what* a block does, first try extracting a well-named method.
- When changing code, update or delete the comments it touches. A stale comment is worse than none.

## Pruning existing code

1. Work file by file, and only on files you own (parallel agents: respect folder ownership).
2. Apply the Keep and Delete lists above. If unsure, keep the comment only when deleting it would lose a fact.
3. Rebuild with your lane: `dotnet build <project> -p:Lane=<name>`. `TreatWarningsAsErrors` is on, so a removed `#pragma` or a broken `cref` surfaces here.
4. Run the tests of the touched project.
