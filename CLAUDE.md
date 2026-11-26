# Project Operating Guide (Claude Code + Graphify + Obsidian)

## Core Workflow Rules
- ALWAYS run Graphify first before suggesting code modifications or refactors (`graphify update .`; see Build & Test Commands).
- Graphify is the Python package `graphifyy` (installed with `uv tool install --python 3.13 graphifyy`). The npm package `graphify` is an unrelated random-graph generator: never run `npx graphify`.
- Read `graphify-out/GRAPH_REPORT.md` to analyze architectural boundaries, incoming dependencies, and "god nodes" before writing files.
- Never introduce circular dependencies or violate the module paths mapped by Graphify.

## Obsidian Integration
- If an Obsidian vault path or local docs folder exists, ensure core architecture insights are mirrored or updated there.
- Use `graphify export obsidian --dir <vault folder>` if requested to generate node notes and a canvas for the vault.

## Build & Test Commands
- **Update Graph (incremental, offline):** `graphify update .`
- **Rebuild Graph from scratch (offline):** `graphify extract . --code-only` then `graphify cluster-only .`
- Set `GRAPHIFY_BACKEND=none` when running these so no code is sent to an LLM service.
- **Build Project:** `npm run build`
- **Run Tests:** `npm test`
