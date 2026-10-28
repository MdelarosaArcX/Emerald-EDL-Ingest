# Project Operating Guide (Claude Code + Graphify + Obsidian)

## Core Workflow Rules
- ALWAYS run Graphify first before suggesting code modifications or refactors (`npx graphify .` or your specific project command).
- Read `graphify-out/GRAPH_REPORT.md` to analyze architectural boundaries, incoming dependencies, and "god nodes" before writing files.
- Never introduce circular dependencies or violate the module paths mapped by Graphify.

## Obsidian Integration
- If an Obsidian vault path or local docs folder exists, ensure core architecture insights are mirrored or updated there.
- Use `graphify . --obsidian` if requested to generate standalone node stubs for the vault.

## Build & Test Commands
- **Update Graph:** `npx graphify .`
- **Build Project:** `npm run build`
- **Run Tests:** `npm test`
