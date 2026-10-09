# Implementation status

Implementation status per object type. **List** = enumerate all objects of that type; **Read/Update** = the object's content, through the source tree; **Rename/Delete** = the object itself.

✅ supported · ⚠️ partial (see notes) · ❌ not supported · — not applicable

For object types TiaMcp doesn't support yet, the notes say what the Openness API (TIA Portal V21) offers for them.

## PLC

| Object | List | Create | Read | Update | Rename | Delete | Notes |
|---|---|---|---|---|---|---|---|
| Block: SCL / FBD / LAD / global DB | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | `.s7dcl` / `.s7res` |
| Block: GRAPH | ✅ | ⚠️ | ✅ | ⚠️ | ✅ | ✅ | `.graph.il`; create clones an existing GRAPH block as template; topology editing is limited |
| Block: STL (whole block) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | `.awl` |
| Block: embedded STL networks | ✅ | ❌ | ✅ | ❌ | ✅ | ✅ | Read-only sidecar (`.stl-networks.awl`); rename/delete act on the whole block |
| Instance-DB | ✅ | ✅ | ✅ | ❌ | ✅ | ✅ | Created from its `.s7dcl` or bound to an FB; read adds the resolved member list (`.interface.txt`) |
| Block group | ⚠️ | ✅ | — | — | ✅ | ✅ | Only visible through the group paths of the blocks in it; empty groups are not listed |
| UDT (PLC data type) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | `.s7dcl` / `.s7res` |
| UDT group | ⚠️ | ✅ | — | — | ✅ | ✅ | Empty groups are not listed |
| Tag table | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | `.csv` |
| Tag | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | Through the tag table CSV (a removed row deletes the tag); rename via `rename_plc_tag` (keeps references) |
| Tag table group | ✅ | ✅ | — | — | ✅ | ✅ | Empty groups are listed as `Group/` |
| User constant | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: create, delete, import/export |
| Watch table | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: create, delete, import/export |
| Watch/force table group | ❌ | ❌ | — | — | ❌ | ❌ | Openness: create, delete |
| Force table | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: import/export only |
| External source (`.scl` / `.awl` / `.db`) | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: create from file, delete, generate blocks from a source |
| External source group | ❌ | ❌ | — | — | ❌ | ❌ | Openness: create, delete |
| Technology object (PID, motion, counter) | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: create, delete, import/export |
| Technology object group | ❌ | ❌ | — | — | ❌ | ❌ | Openness: create, delete |
| PLC alarm text list | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: create, delete, Excel export |
| PLC alarm class | ❌ | — | ❌ | ❌ | — | — | Openness: import/export only |
| Supervision (ProDiag) settings | — | — | ❌ | ❌ | — | — | Openness: import/export only |
| Software unit (S7-1500) + relations | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: create, delete |
| OPC UA server interface / reference namespace | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: create, delete, import/export |

## HMI (WinCC Unified)

| Object | List | Create | Read | Update | Rename | Delete | Notes |
|---|---|---|---|---|---|---|---|
| HMI tag table | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | `.csv` |
| HMI tag | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | Through the tag table CSV (a removed row deletes the tag); rename via `rename_hmi_tag` (keeps alarm references) |
| HMI tag table group | ✅ | ✅ | — | — | ✅ | ✅ | Empty groups are listed as `Group/` |
| Alarm class | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ | `AlarmClasses.csv` |
| Alarm (discrete / analog) | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ | `DiscreteAlarms.csv` / `AnalogAlarms.csv` |
| Screen | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: create, delete; content via properties (no text export) |
| Screen group | ❌ | ❌ | — | — | ❌ | ❌ | Openness: create, delete |
| Script module (global) | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: import/export |
| Text list / graphic list | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: delete, import/export |
| HMI connection | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: create, delete |
| Data log / alarm log / logging tag | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: create, delete |

## Project

| Object | List | Create | Read | Update | Rename | Delete | Notes |
|---|---|---|---|---|---|---|---|
| TIA Portal instance | ✅ | — | — | — | — | — | |
| Project | — | ❌ | ✅ | ✅ | ❌ | ❌ | Open/close, status, save, save as |
| Source tree | — | — | ✅ | ✅ | — | — | All source is read and written through it; partial reads; writes back only changes, refuses items changed in TIA Portal since the read; never deletes items |

## Hardware & library

| Object | List | Create | Read | Update | Rename | Delete | Notes |
|---|---|---|---|---|---|---|---|
| Device + device group | ⚠️ | ❌ | ❌ | ❌ | ❌ | ❌ | `list_project` lists PLC and WinCC Unified software only (Classic WinCC panels not supported). Openness: create, delete |
| Device item (module) | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: create (plug), delete; attributes via properties |
| Subnet / IO system | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: create, delete, connect nodes |
| Sync domain / MRP domain | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: create, delete |
| Web server / OPC UA user | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: create, delete |
| Library master copy + folder | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: create, delete |
| Library type + version + folder | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: create from blocks/UDTs, delete, export |
