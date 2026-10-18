# Design

TiaMcp makes a TIA Portal project workable for an LLM by mirroring it as a **source tree**: plain files on disk that the LLM reads and edits with its own file tools, and that are written back into the project. Follow these rules for every new object type or format. Parts marked *future* are the target design but not implemented yet; don't assume they exist, but don't build against them either.

## Principles

1. **Content goes through files, never through tool calls.** Block code, tables and screens are only read and written through `read_source_tree` / `write_source_tree`. Other MCP tools handle the object's life cycle only: connect, list, create/rename/delete, groups, compile.
2. **The tree mirrors the project tree:** `<Device>/<Section>/<group path>/<Item>.<ext>`, using the section names TIA Portal shows (`Program blocks`, `PLC tags`, `Screens`, ...).
3. **Every object becomes a file when Openness allows it.** If Openness has no export, build the file from the object model (Unified screens, HMI tags, alarms).
4. **The format is chosen from this ladder, best option first:**
   1. TIA's own text format (`.s7dcl/.s7res`, `.awl`).
   2. A standard text format: CSV for anything tabular (Excel-compatible, UTF-8 with BOM), JSON for object models.
   3. A small text DSL, only when no standard format fits (GRAPH `.graph.il`).
   4. Native Openness XML, as the fallback for a kind that doesn't have a converter yet.

   Derived information that helps the LLM but can't be imported goes in a read-only companion file (`.interface.txt`, `.stl-networks.awl`).
5. **The raw export is always available too.** Where Openness has one (SimaticML `.xml`), a kind offers it as a second format after its own: complete where a conversion is lossy, and writable. See "Formats".
6. **Files must round-trip.** Reading and then writing an unedited file changes nothing. Output is deterministic: stable order, values at their default left out, noise stripped (e.g. `DocumentInfo`). After a write, the written items are read back, and that becomes the new baseline.
7. **`_manifest.json` records the state as read.** It holds per item its status and formats, and per file its hash and format. A write imports only what was edited, refuses items changed in TIA Portal since the read (unless `force`), and creates new files as new items. Deleting a file never deletes an item (use the `delete_*` tools); removing a row inside a table file does delete that row.
8. **Writes fail per item, never as a whole.** Items are written in dependency order (UDTs, tags, blocks, text lists; HMI tags before screens). Inputs known to crash TIA Portal are refused before the import. Every write supports `dryRun`.
9. **Tool results are compact and fit the LLM's context.** Return counts plus every line that isn't a success. A full report goes to a file in the tree (`_import_report.txt`, `_project_list.txt`).
10. **No customer data in the repo.** Examples, tests and docs use made-up names.

## Layers

| Project | Role |
|---|---|
| `TiaMcp.Formats` | File format converters (CSV, GRAPH DSL, the screen JSON diff) and the records they convert, the format ids (`SourceFormat`) and the manifest. No Openness or MCP dependency. |
| `TiaMcp.Openness` | All Openness API calls, run on one STA thread (`StaDispatcher`). No MCP code. |
| `TiaMcp.Mcp` | MCP tools, the source tree and the manifest. One handler per kind in `SourceKinds/`. `TiaSession` is the only path into Openness. |
| `TiaMcp.Host` | stdio entry point, Openness assembly resolving. |
| `tests/TiaMcp.Tests` | Unit tests for `TiaMcp.Formats`. They run without TIA Portal. Samples are made up; real exports stay outside the repo. |

A converter is pure: text in, records or text out. Every converter has round-trip tests (`parse(format(x)) == x`, unedited write is a no-op) and tests that bad input gives a `FormatException` with the line, not a crash.

## Kind handlers

Each kind of item (block, UDT, tag table, HMI screen, ...) is one class implementing `ISourceKind` in `src/TiaMcp.Mcp/SourceKinds/`: its name, section, device family (PLC, WinCC Unified, classic WinCC), formats, and how it is listed, exported and written. `SourceKinds.All` registers them in write order, so what an item references is written first. `list_project`, `read_source_tree`, `write_source_tree`, new-file detection and the item-line parsing loop over this registry; `TiaTools` has no per-kind code.

## Formats

A format is named like its file extension: `s7dcl` (with its `.s7res` and companions), `graph.il`, `awl`, `csv`, `json`, `xml` (`SourceFormat`). A kind lists its formats with its own first and `xml` last; an item has its own format plus `xml` where the kind has one (a block's own format follows from its language).

- `list_project` ends each item line with the formats it can be read in, e.g. `tag table 'Motors' [csv, xml]`; `_export_summary.txt` with the formats it has in the tree. Passing such a line to `read_source_tree` reads those formats. Without one, an item is read in the formats it already has in the tree, else its own; the read-back after a write and the conflict check use the formats in the tree.
- **One Openness export per item.** A read asks `TiaConnection` for the formats it needs and gets one document per format. Where the own format is made from the XML export (tag tables, GRAPH, the title cross-check of code blocks), that same export is kept as the `.xml`; a second export only happens where the own format comes from another Openness call (ExportAsDocuments, Generate source) and the XML is wanted too.
- **A write uses one format per item:** the one whose files were edited since the read. An item edited in more than one format, or new in the tree in more than one, is refused. The read-back refreshes every format in the tree.
- An XML export needs a consistent block or UDT (`ISourceKind.XmlNeedsCompile`), so a write compiles the PLC before reading such items back as XML.
- A `json` file is built from the object model and written property by property, which takes minutes for a large screen. So a read also keeps it as read under `.base/` in the tree, and a write sends only what differs from that copy (`ScreenJson.ChangedSince`). The copy is used only when the project still matches it: the item passed the conflict check (`WriteContext.Baseline`, not with `force`) and the copy's hash matches the manifest. Otherwise everything is written.

`_manifest.json` (version 2): per item `"formats": ["csv", "xml"]` (what it can be read in, as `list_project` shows), and per file an object `{ "hash": "...", "format": "csv" }`, so that later fields (multiuser state, timestamps) can be added without another version. A version 1 manifest (path → bare hash) is still read and saved as version 2.

## Adding an object type

Touch, in this order:
1. `TiaMcp.Formats`: the converter and its records, with tests in `tests/TiaMcp.Tests` (not needed for native Openness XML).
2. `TiaConnection.<Area>.cs`: list, export and import, in each of the kind's formats; reuse one XML export for all formats made from it.
3. `TiaSession`: an async wrapper for each new call.
4. The handler: a class implementing `ISourceKind`, registered in `SourceKinds.All` at its place in the write order, and its name in `SourceKinds.ListOrder`.
5. Lifecycle tools, if Openness supports them.
6. README (format table) and STATUS (object table).

## Future (target design, not implemented)

- **Smaller files.** `TiaConnection` is split per kind instead of having a few large files.
- **`TiaSession.Run(c => c.X())`** replaces the one-line async wrappers.
- **Generic lifecycle tools** (`rename_item`, `delete_item` with a kind parameter) replace the per-kind rename/delete/group tools, through the kind handlers.
- **Classic WinCC as JSON**, like Unified screens, with the XML kept as its raw format.

When something in this section is implemented, move it into the sections above and update "Adding an object type".
