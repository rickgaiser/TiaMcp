# Agent instructions

Instructions for every coding agent working on this repository. CLAUDE.md only imports this file; keep project rules here and personal preferences in your own user-level settings.

# Design
Read [DESIGN.md](DESIGN.md) before adding or changing an object type or file format. When a "Future" item from it is implemented, move it into the design sections.

# Build and install
The MCP server runs straight from the build output (`src\TiaMcp.Host\bin\Debug\net48`), so a running `TiaMcp.Host.exe` (from this or another agent session) locks its DLLs and the build fails.
1. `Stop-Process -Name TiaMcp.Host -Force`
2. `dotnet build TiaMcp.sln`
3. Restart the TiaMcp MCP server in your client (Claude Code: `/mcp`), then call `tia_connect` again.

`dotnet test TiaMcp.sln` runs the converter unit tests, without TIA Portal. To also round-trip real GRAPH exports, set `TIAMCP_GRAPH_SAMPLES` to a folder outside the repo holding raw GRAPH `.xml` exports. Everything that talks to Openness still has to be verified against a live TIA Portal project.

# Documentation
After code changes, update README.md and STATUS.md where a new user needs to know. Keep them complete but compact: not every code change, only what a new user needs.

# Customer information
Never put customer data from TIA Portal projects (PLC, block, tag, screen or alarm names and texts, etc.) into the repository, including commit messages. It may be under NDA. Use made-up names in examples, tests and docs.
