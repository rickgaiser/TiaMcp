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
| Instance-DB | full member list (no file export) | Yes | Create-only |
| STL (whole-block) | `.awl` | Yes | Yes |
| STL (embedded in mixed FBD/LAD/SCL block) | `.stl-networks.awl` (or `.stl-networks.xml` fallback) + `.s7dcl`/`.s7res` | Yes | No |
| UDT (PLC data type) | `.s7dcl` (no `.s7res`) | Yes | Yes |
| PLC tag table | `.csv`, Excel-compatible (synthesized) | Yes | Yes |
| HMI tag table (WinCC Unified) | `.csv`, Excel-compatible (synthesized) | Yes | Yes |
| HMI alarms (WinCC Unified) | `.csv`, Excel-compatible (synthesized) | Yes | Yes |
| Source tree export/import (whole project) | native extension per item (`.txt` appended only when an item has none) | Yes | Yes |

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

### PLC blocks

| Tool | Purpose |
|---|---|
| `list_plc_devices` | List PLC devices in the project |
| `list_plc_blocks` | List program blocks with group path, language, consistency |
| `read_plc_block` | Read a block's source — `.s7dcl`/`.s7res` for SCL/FBD/LAD, `.graph.il` for GRAPH, full member list for instance-DBs |
| `write_plc_block` | Overwrite an existing block's source in place; doesn't auto-compile |
| `create_plc_block` | Create a new block (any language) in a group, from source text — `graphTemplateBlockName` for GRAPH, `stlBlock` for whole-block STL |
| `create_plc_instance_db` | Create an instance-DB bound to an FB type |
| `delete_plc_block` | Delete a single block by name (any language, including GRAPH and instance-DBs) — recovery path for a block stuck INCONSISTENT |
| `rename_plc_block` | Rename an existing block (any language, including instance-DBs) in place — safe even with real dependents |
| `create_plc_group` / `delete_plc_group` / `rename_plc_group` | Manage block group folders |
| `compile_plc` | Compile a PLC's software, report structured errors/warnings |
| `search_plc` | Substring search over block, tag-table, and UDT names |

### PLC tag tables

| Tool | Purpose |
|---|---|
| `list_plc_tag_tables` / `read_plc_tag_table` | List/read PLC tag tables |
| `write_plc_tag_table` | Create or update tags by name/data type/logical address |
| `create_plc_tag_table` | Create a new, empty PLC tag table in a group (or the root) |
| `delete_plc_tag_table` / `rename_plc_tag_table` | Delete or rename a PLC tag table by name |
| `create_plc_tag_table_group` / `delete_plc_tag_table_group` / `rename_plc_tag_table_group` | Manage PLC tag table group folders |

### PLC UDTs (data types)

| Tool | Purpose |
|---|---|
| `list_plc_data_types` / `read_plc_udt` | List/read UDTs (PLC data types) |
| `write_plc_udt` | Overwrite an existing UDT's field list in place |
| `create_plc_udt` | Create a new UDT in an existing group (or the root) |
| `delete_plc_udt` / `rename_plc_udt` | Delete (fails if still used as a member type elsewhere), or rename, a UDT |
| `create_plc_udt_group` / `delete_plc_udt_group` / `rename_plc_udt_group` | Manage UDT group folders |

### HMI tags

| Tool | Purpose |
|---|---|
| `list_hmi_devices` | List WinCC Unified HMI devices in the project |
| `list_hmi_tag_tables` / `read_hmi_tag_table` | List/read WinCC Unified HMI tag tables |
| `write_hmi_tag_table` | Create or update HMI tags by name/data type/address/PLC binding |
| `create_hmi_tag_table` | Create a new, empty HMI tag table in a group (or the root) |
| `delete_hmi_tag_table` / `rename_hmi_tag_table` | Delete or rename a WinCC Unified HMI tag table by name |
| `create_hmi_tag_table_group` / `delete_hmi_tag_table_group` / `rename_hmi_tag_table_group` | Manage HMI tag table group folders |

### HMI alarms

| Tool | Purpose |
|---|---|
| `list_hmi_alarm_classes` / `write_hmi_alarm_class` | List, or create/update, WinCC Unified HMI alarm classes |
| `delete_hmi_alarm_class` | Delete a WinCC Unified HMI alarm class by name (fails if an alarm still references it) |
| `list_hmi_alarms` / `read_hmi_alarm` | List (discrete + analog, unified) or read one WinCC Unified HMI alarm |
| `write_hmi_alarm` | Create or update a discrete or analog HMI alarm |
| `delete_hmi_alarm` | Delete a discrete or analog HMI alarm by name |

### Source tree (whole-project export/import)

| Tool | Purpose |
|---|---|
| `source_tree` | Export the whole project's readable source to disk (each file keeping its native extension; tag tables as `.csv`), folder structure mirroring the TIA Portal project tree |
| `write_source_tree` | Write a `source_tree` export back into the project — the whole tree, or a trimmed subset of `_export_summary.txt` — with independent control over creating missing items and overwriting existing ones, plus a dry-run preview |

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

Only step/transition attributes, actions, and matched-by-number conditions are writable. Interface, `PREOPERATIONS`, `BRANCHES`, and `CONNECTIONS` (topology) are read-only in this version. GRAPH blocks must also be consistent (compiled) before `read_plc_block`/`write_plc_block` will work at all — that's a Siemens API requirement, not a TiaMcp restriction.

New GRAPH blocks are created via `create_plc_block`'s `graphTemplateBlockName` (clones an existing GRAPH block and reshapes it — Openness has no from-scratch GRAPH creation API).

## Source tree export

`source_tree` dumps every PLC block/tag table/UDT and every WinCC Unified HMI tag table/alarm the server can read to disk, one call for the whole project, in folders mirroring the TIA Portal project tree:

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
```

The source tree is LLM friendly and allows for many fast local queries or modifications on the codebase. `write_source_tree` can then be used to write the changes back to the TIA portal project.

## License

AGPL-3.0 (see [LICENSE](LICENSE)). Contributions require signing the [CLA](CLA.md) — see [CONTRIBUTING.md](CONTRIBUTING.md).

## Not (yet) supported

- Hardware config.
- Explicit block numbering (manual vs. automatic, specific number).

## Known limitations

- STL embedded in a mixed FBD/LAD/SCL block is read-only; whole-block STL is fully supported (see the STL section above).
- GRAPH blocks can only be created by cloning an existing one (see the GRAPH section above), not from scratch.
- Classic WinCC (Comfort/Advanced/RT) — its Openness object model has no alarm objects and no tag `Create` factory.
