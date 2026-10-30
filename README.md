# TiaMcp

MCP server for Siemens TIA Portal V21+ projects — reads and writes directly to TIA Portal via the Openness API, designed for a fast, LLM- and human-friendly workflow.

Special features that make TiaMcp fast for LLM's and human-readable friendly:
- STL is read/written as text AWL
- PLC tables are converted to/from Excel-compatible CSV
- HMI tables are converted to/from Excel-compatible CSV
- HMI alarms are converted to/from Excel-compatible CSV
- GRAPH is converted to/from compact text DSL (Domain Specific Language)

## Format reference

| Format / Language | File extension(s) | Read | Write |
|---|---|---|---|
| SCL | `.s7dcl` / `.s7res` | Yes | Yes |
| FBD | `.s7dcl` / `.s7res` | Yes | Yes |
| LAD | `.s7dcl` / `.s7res` | Yes | Yes |
| Global DB (`DATA_BLOCK`) | `.s7dcl` / `.s7res` | Yes | Yes |
| GRAPH (S7-GRAPH/SFC) | `.graph.il` | Yes | Partial |
| Instance-DB | `.s7dcl` + read-only `.interface.txt` (resolved member list) | Yes | Create-only |
| STL (whole-block) | `.awl` | Yes | Yes |
| STL (embedded in mixed FBD/LAD/SCL block) | `.stl-networks.awl` (or `.stl-networks.xml` fallback) + `.s7dcl`/`.s7res` | Yes | Through `.xml` only |
| UDT (PLC data type) | `.s7dcl` / `.s7res` | Yes | Yes |
| PLC tag table | `.csv`, Excel-compatible (synthesized) | Yes | Yes |
| HMI tag table (WinCC Unified) | `.csv`, Excel-compatible (synthesized) | Yes | Yes |
| HMI alarms (WinCC Unified) | `.csv`, Excel-compatible (synthesized) | Yes | Yes |
| Screen (WinCC Unified), incl. faceplates | `.json` (synthesized from the object model) | Yes | Yes |
| PLC alarm text list (user list) | `.csv`, Excel-compatible (synthesized from TIA's Excel export) | Yes | Yes (existing lists) |
| Classic WinCC (Comfort/Advanced/RT Advanced): screens, screen templates, pop-up screens, tag tables, text lists | `.xml`, native Openness XML | Yes | Yes |
| Raw export of any block, UDT or PLC tag table, next to or instead of the formats above | `.xml`, SimaticML as Openness exports it | Yes | Yes |

Each item line of `list_project` ends with the formats it can be read in, e.g. `tag table 'Tags' [csv, xml]`; pass such a line to `read_source_tree` to read those formats. A write uses the one format whose files were edited, and refuses an item edited in two.

All source is read and written as files in a [source tree](#source-tree) on disk, so an LLM reads only what it needs and edits with small diffs instead of passing whole blocks through its context.

## Prerequisites

- TIA Portal V21 installed, with Openness access enabled for your Windows user (Openness API group membership).
- .NET Framework 4.8.

## Building

```
dotnet build TiaMcp.sln
```

Produces `src\TiaMcp.Host\bin\Debug\net48\TiaMcp.Host.exe`, the MCP stdio server entry point. `dotnet test TiaMcp.sln` runs the unit tests (no TIA Portal needed).

## Registering with Claude Code

Copy [.mcp.json.example](.mcp.json.example) to `.mcp.json` and point `command` at wherever you extracted or built `TiaMcp.Host.exe`:

```json
{
  "mcpServers": {
    "tia": {
      "command": "C:\\path\\to\\TiaMcp.Host.exe",
      "args": []
    }
  }
}
```

## Tools

All MCP tools exposed by the server, grouped by area.

### Connection & project

| Tool | Purpose |
|---|---|
| `tia_connect` | Attach to an open project or Multiuser local session, or open one by path (`.apXX` or `.alsXX`). With several instances running it picks the one by the project it has open; pass `processId` (from `list_tia_instances`) when that isn't clear. `startPortal: true` starts the TIA Portal GUI when none is running and opens `projectPath` in it |
| `list_tia_instances` | List running TIA Portal instances with no Openness prompt: PID, open project, GUI or not, and the attached Openness clients. Warns when the user isn't in the `Siemens TIA Openness` group |
| `project_status` | Report unsaved-changes state, author, last-saved info; for a Multiuser local session also whether the server has newer changes, and the marked objects. Openness can't refresh a local session: do that in the TIA Portal GUI |
| `save_project` / `save_project_as` | Save in place, or save a copy to a new folder; for Multiuser local sessions, save is local-only and Save As is unsupported |
| `close_project` | Close the connected project; closing a Multiuser local session does not commit it to the server |
| `list_project` | List PLC and HMI software with their blocks (language, consistency), UDTs, tag tables and empty groups — names only, fast; filter by text or software |

### PLC blocks

| Tool | Purpose |
|---|---|
| `create_plc_instance_db` | Create an instance-DB bound to an FB type, without writing its source |
| `delete_plc_block` | Delete a single block by name (any language, including GRAPH and instance-DBs) — recovery path for a block stuck INCONSISTENT |
| `rename_plc_block` | Rename an existing block (any language, including instance-DBs) in place — safe even with real dependents |
| `create_plc_group` / `delete_plc_group` / `rename_plc_group` | Manage block group folders |
| `compile_plc` | Compile a PLC's software, report structured errors/warnings |

### PLC tag tables

| Tool | Purpose |
|---|---|
| `rename_plc_tag` | Rename a single tag, keeping references in blocks intact |
| `create_plc_tag_table` | Create a new, empty PLC tag table in a group (or the root) |
| `delete_plc_tag_table` / `rename_plc_tag_table` | Delete or rename a PLC tag table by name |
| `create_plc_tag_table_group` / `delete_plc_tag_table_group` / `rename_plc_tag_table_group` | Manage PLC tag table group folders |

### PLC UDTs (data types)

| Tool | Purpose |
|---|---|
| `delete_plc_udt` / `rename_plc_udt` | Delete (fails if still used as a member type elsewhere), or rename, a UDT |
| `create_plc_udt_group` / `delete_plc_udt_group` / `rename_plc_udt_group` | Manage UDT group folders |

### HMI tags

| Tool | Purpose |
|---|---|
| `rename_hmi_tag` | Rename a single HMI tag, keeping references in alarms intact |
| `create_hmi_tag_table` | Create a new, empty HMI tag table in a group (or the root) |
| `delete_hmi_tag_table` / `rename_hmi_tag_table` | Delete or rename a WinCC Unified HMI tag table by name |
| `create_hmi_tag_table_group` / `delete_hmi_tag_table_group` / `rename_hmi_tag_table_group` | Manage HMI tag table group folders |

### HMI alarms

| Tool | Purpose |
|---|---|
| `delete_hmi_alarm_class` | Delete a WinCC Unified HMI alarm class by name (fails if an alarm still references it) |
| `delete_hmi_alarm` | Delete a discrete or analog HMI alarm by name |

### HMI screens and faceplates (WinCC Unified)

Screen content is read and written through the source tree; these tools manage screens and help with the JSON.

| Tool | Purpose |
|---|---|
| `delete_hmi_screen` / `rename_hmi_screen` | Delete or rename a screen |
| `create_hmi_screen_group` / `delete_hmi_screen_group` / `rename_hmi_screen_group` | Manage screen group folders |
| `compile_hmi` | Compile an HMI's software (WinCC Unified or classic), report errors/warnings with their path (screen > item > property) |
| `describe_hmi_item_type` | Properties, events and dynamization options per screen item type |
| `list_library_types` / `read_faceplate_type` | Project library types and versions / a faceplate's interface |

### Source tree (reading and writing all source)

| Tool | Purpose |
|---|---|
| `read_source_tree` | Export source to disk, folder structure mirroring the TIA Portal project tree — the whole project, or only the items or groups you name |
| `write_source_tree` | Write what was edited or added in the tree back into the project, refusing items changed in TIA Portal since they were read; dry-run preview |

Full argument descriptions are on each tool via MCP `[Description]` attributes — see `src\TiaMcp.Mcp\TiaTools.cs`.

## WinCC Unified screens

Openness has no export format for Unified screens, so in the source tree a screen is `Screens/<group>/<name>.json`, JSON built from the Openness object model. Keys are the exact Openness property names; lower-case keys are reserved for items, dynamizations, events and the item name/type:

```json
{
  "name": "Overview", "Width": 1920, "Height": 1080,
  "items": [
    { "name": "Title", "type": "Text", "Left": 20, "Top": 10, "Width": 400, "Height": 40,
      "Text": "Overview", "Font": { "Size": 20 } },
    { "name": "Setpoint", "type": "IOField", "Left": 20, "Top": 80, "Width": 120, "Height": 30,
      "dynamizations": { "ProcessValue": "Tank1_Setpoint" } },
    { "name": "Start", "type": "Button", "Left": 160, "Top": 80, "Width": 100, "Height": 30,
      "Text": "Start", "events": { "Tapped": "Tags('Tank1_Start').Write(true);" } },
    { "name": "Motor_1", "type": "FaceplateContainer", "ContainedType": "V1.0.0\\FP_Motor", "Left": 300, "Top": 80,
      "Interface": { "Motor": "Motor_1", "Label": "M1" } }
  ]
}
```

- A read leaves out values still at their default. Defaults are sampled from a scratch screen inside a transaction that is rolled back, so the project is not changed.
- A write sets every key in the file; leaving a property, dynamization or event out doesn't reset it (set the default value, or `null` to remove a dynamization or script). Items are matched by name: new names are created, removed items are deleted. Failures are reported per property and the rest still applies.
- A new screen is created by adding a new `.json` file. Openness can't copy a Unified screen, so a copy is a new file too.
- Faceplates must be in the **project library**. `list_library_types` gives the `ContainedType` (`V<version>\<type>`), `read_faceplate_type` the interface. A UDT-typed interface property takes the HMI tag name as its value; texts are plain text. An interface property bound to a tag is an object, `{"type": "Tag", "Tag": "<HMI tag>"}`: a plain string is a static value.
- Writing is slow: every item goes through Openness separately, so creating, copying or force-writing a screen of a few hundred items takes about a minute. An edited screen only gets its changed properties written, by comparing the file with its copy as last read (`.base/` in the tree).
- `HmiSystemDiagnosisControl.ScriptDiagnosisOverviewText` is never read or written: reading it crashes TIA Portal V21.

## GRAPH (S7-GRAPH/SFC) blocks

GRAPH blocks read/write as a compact text DSL instead of raw graphical XML:

```
GRAPH_BLOCK "ExampleProcess_SFC"
INTERFACE
  VAR_INPUT ... END_VAR
END_INTERFACE
PREOPERATIONS ... END_PREOPERATIONS
SEQUENCE "Sequence1"
  STEP 100 "StepName" [INIT] MAX_TIME=T#10S WARN_TIME=T#9S
    ACTION S: #SomeOutput
    SUPERVISION: #cond1 AND NOT #cond2
    INTERLOCK: ...
  END_STEP
  TRANSITION 100 "Trans100": (#iq_Sensor.Status.InActive AND NOT #iq_NextStep.Enabled)
  BRANCHES ... END_BRANCHES
  CONNECTIONS
    STEP 100 -> TRANSITION 100
  END_CONNECTIONS
END_SEQUENCE
END_GRAPH_BLOCK
```

Only step/transition attributes, actions, and matched-by-number conditions are writable. Interface, `PREOPERATIONS`, `BRANCHES`, and `CONNECTIONS` (topology) are read-only in this version. GRAPH blocks must also be consistent (compiled) before they can be read or written at all — that's a Siemens API requirement, not a TiaMcp restriction.

A new `.graph.il` file in the source tree is created by cloning an existing GRAPH block of the PLC and reshaping it — Openness has no from-scratch GRAPH creation API.

## Source tree

`read_source_tree` exports PLC blocks, tag tables, UDTs and alarm text lists, WinCC Unified HMI tag tables, screens and alarms, and classic WinCC screens, screen templates, pop-up screens, tag tables and text lists to disk, in folders mirroring the TIA Portal project tree — the whole project, or only the items or groups you name:

```
<targetDirectory>/
  <PlcName>/
    Program blocks/<group path>/
      Main.s7dcl
      Main.s7res
    PLC tags/<group path>/
      Default tag table.csv
    PLC data types/<group path>/
      SomeUdt.s7dcl
    PLC alarm text lists/
      Textlist_ErrorsMessages.csv
  <HmiName>/
    HMI tags/<group path>/
      Default tag table.csv/xml                   (Unified/classic)
    HMI alarms/                                   (Unified only)
      DiscreteAlarms.csv
      AnalogAlarms.csv
      AlarmClasses.csv
    Screens/<group path>/
      Overview.json/xml                           (Unified/classic)
    Screen templates/<group path>/                (classic only)
      Template.xml
    Pop-up screens/<group path>/                  (classic only)
      Popup.xml
    Text lists/                                   (classic only)
      States.xml
  _export_summary.txt
  _manifest.json
  .base/                                          (Unified screens as last read)
```

A `/` in a group name gives a folder per part (`Valve fwd / rev` → `Valve fwd/ rev/`), and folder names lose trailing spaces and dots, as Windows does. A new file in such a folder goes to the group read into it; the group tools take the name as it is (`groupPath: "Valve fwd / rev/Sub"`).

Read and edit the files with your own file tools, then call `write_source_tree`. It writes back only what changed since the read — edited files, plus new files as new blocks, UDTs, tag tables or Unified screens — in dependency order, and then reads the written items back so the files show what TIA Portal made of them. Removing a row from a tag table CSV deletes that tag (a dry run names the tags it would delete); deleting a file does not delete the item (use the `delete_*` tools). An item whose write failed halfway keeps your edit in its file; fix the cause and write it again. A WinCC Unified tag table bound to a connection the HMI doesn't have is refused as a whole, naming the connections it does have.

WinCC Unified HMI tag tables leave out the length of String/WString tags by default, because reading it costs an Openness call per tag. Read with `hmiTagLength: true` to get a `Length` column; the tree remembers the setting. Only internal tags take a written length; a PLC-bound tag has its PLC tag's.

An open HMI tag editor in TIA Portal (e.g. "Show all tags") slows every Openness call on that HMI down, a read or write of its tag tables about eight times. Close it while working through TiaMcp.

PLC alarm text lists: one CSV per user list (`From`, `To`, one `Text [language]` column per project language). TIA Portal's Excel import replaces whole lists, so a write starts from a fresh export, changes only the lists being written and imports that; removing a row deletes that entry (not when writing a tree read from another project). Lists can't be created through Openness - create a new list in TIA Portal first. System lists (`SYSTEM_*`) are not exported.

Classic WinCC objects are native Openness XML (the `DocumentInfo` block is left out so an unchanged object reads back identically). They are written in dependency order (text lists, tag tables, templates and pop-ups, screens); a screen whose `<Number>` is already used by another screen is refused before the import, since TIA Portal V21 terminates itself on that. Graphic lists are not exported: exporting one crashed TIA Portal V21 (`InvalidCastException` in `UpdateOpenLinks`); a full read reports them as SKIPPED.

`_manifest.json` (version 2; an older version 1 manifest is still read) records what each item looked like when it was read, and in which formats. An item that was changed in TIA Portal since then is refused rather than overwritten (`force` overrides this).

In a Multiuser local session, `read_source_tree` also records each item's mark state: marked by me, marked by another user, or not up to date with the server (shown in `_export_summary.txt` and the read result). It costs an Openness call per object, about 35 ms per block, UDT or screen and per tag or alarm of a table: roughly a minute for a project of a few hundred blocks and a thousand tags. `write_source_tree` checks the items it writes again and refuses those marked by another user or not up to date (`force` overrides this). Openness can't refresh a local session: refresh it in the TIA Portal GUI, then read again. A tree read from one project can also be written in full into another, e.g. to restore it into an empty PLC.

## License

AGPL-3.0 (see [LICENSE](LICENSE)). Contributions require signing the [CLA](CLA.md) — see [CONTRIBUTING.md](CONTRIBUTING.md).

## Not (yet) supported

- Hardware config.
- Explicit block numbering (manual vs. automatic, specific number).

## Known limitations

- A block with STL embedded in a mixed FBD/LAD/SCL block can only be written through its raw `.xml`; whole-block STL is fully supported (see the STL section above).
- TIA Portal V21's `.s7dcl` import gets some blocks wrong without an error. `read_source_tree` warns about each one (`WARNING` line), and `write_source_tree` refuses to write such a `.s7dcl`; the message gives the workaround (often: write the block's `.xml` instead):
  - an array access inside an index in a LAD/FBD network (`"DB_A".arr["DB_B".structArr[1].idx]`) is mangled into one quoted name;
  - a project FB or UDT named like a system type (`TON`, `TOF`, `TP`, `TONR`, `CTU`, `CTD`, `CTUD`, `R_TRIG`, `F_TRIG`) is replaced by the system type;
  - an AT overlay becomes an independent variable (`.s7dcl` has no AT; the read marks the overlay's line with a `// TiaMcp: AT` comment).
- TIA Portal V21 terminates when it overwrites a block with a textual interface (`S7_EditorMode := "[SCL,TextualInterface]"`) that was exported more than once since the project was opened or the block was last written. `write_source_tree` counts the exports and refuses such a write; save, close and reopen the project (`tia_connect` with `projectPath`) and write right away. After attaching to an already open project the count is unknown, so these writes are refused until the project is reopened through TiaMcp.
- GRAPH blocks can only be created by cloning an existing one (see the GRAPH section above), not from scratch.
- Classic WinCC (Comfort/Advanced/RT Advanced): alarms are not reachable through Openness; graphic lists are skipped (export crashes TIA Portal V21); objects can't be renamed or deleted through TiaMcp yet.
- PLC alarm text lists: only existing user lists can be written; the screen-number check for classic screens exports every screen once per write (about a minute for ~50 screens).
