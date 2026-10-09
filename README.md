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
| STL (embedded in mixed FBD/LAD/SCL block) | `.stl-networks.awl` (or `.stl-networks.xml` fallback) + `.s7dcl`/`.s7res` | Yes | No |
| UDT (PLC data type) | `.s7dcl` / `.s7res` | Yes | Yes |
| PLC tag table | `.csv`, Excel-compatible (synthesized) | Yes | Yes |
| HMI tag table (WinCC Unified) | `.csv`, Excel-compatible (synthesized) | Yes | Yes |
| HMI alarms (WinCC Unified) | `.csv`, Excel-compatible (synthesized) | Yes | Yes |

All source is read and written as files in a [source tree](#source-tree) on disk, so an LLM reads only what it needs and edits with small diffs instead of passing whole blocks through its context.

## Prerequisites

- TIA Portal V21 installed, with Openness access enabled for your Windows user (Openness API group membership).
- .NET Framework 4.8.

## Building

```
dotnet build TiaMcp.sln
```

Produces `src\TiaMcp.Host\bin\Debug\net48\TiaMcp.Host.exe`, the MCP stdio server entry point.

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
| `tia_connect` | Attach to the open TIA Portal instance (or open a project by `projectPath`), index PLC names — pass `processId` (from `list_tia_instances`) when more than one instance is running |
| `list_tia_instances` | List running TIA Portal instances (PID + window title) with no Openness prompt, to pick a `processId` for `tia_connect` |
| `project_status` | Report unsaved-changes state, author, last-saved info |
| `save_project` / `save_project_as` | Save in place, or save a copy to a new folder |
| `close_project` | Close the connected project |
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

### Source tree (reading and writing all source)

| Tool | Purpose |
|---|---|
| `read_source_tree` | Export source to disk, folder structure mirroring the TIA Portal project tree — the whole project, or only the items or groups you name |
| `write_source_tree` | Write what was edited or added in the tree back into the project, refusing items changed in TIA Portal since they were read; dry-run preview |

Full argument descriptions are on each tool via MCP `[Description]` attributes — see `src\TiaMcp.Mcp\TiaTools.cs`.

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

`read_source_tree` exports PLC blocks, tag tables and UDTs and WinCC Unified HMI tag tables and alarms to disk, in folders mirroring the TIA Portal project tree — the whole project, or only the items or groups you name:

```
<targetDirectory>/
  <PlcName>/
    Program blocks/
      <group path>/
        Main.s7dcl
        Main.s7res
    PLC tags/
      <group path>/
        Default tag table.csv
    PLC data types/
      <group path>/
        SomeUdt.s7dcl
  <HmiName>/
    HMI tags/
      <group path>/
        Default tag table.csv
    HMI alarms/
      DiscreteAlarms.csv
      AnalogAlarms.csv
      AlarmClasses.csv
  _export_summary.txt
  _manifest.json
```

Read and edit the files with your own file tools, then call `write_source_tree`. It writes back only what changed since the read — edited files, plus new files as new blocks, UDTs or tag tables — in dependency order, and then reads the written items back so the files show what TIA Portal made of them. Removing a row from a tag table CSV deletes that tag; deleting a file does not delete the item (use the `delete_*` tools).

`_manifest.json` records what each item looked like when it was read. An item that was changed in TIA Portal since then is refused rather than overwritten (`force` overrides this). A tree read from one project can also be written in full into another, e.g. to restore it into an empty PLC.

## License

AGPL-3.0 (see [LICENSE](LICENSE)). Contributions require signing the [CLA](CLA.md) — see [CONTRIBUTING.md](CONTRIBUTING.md).

## Not (yet) supported

- Hardware config.
- Explicit block numbering (manual vs. automatic, specific number).

## Known limitations

- STL embedded in a mixed FBD/LAD/SCL block is read-only; whole-block STL is fully supported (see the STL section above).
- GRAPH blocks can only be created by cloning an existing one (see the GRAPH section above), not from scratch.
- Classic WinCC (Comfort/Advanced/RT) — its Openness object model has no alarm objects and no tag `Create` factory.
