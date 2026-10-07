# Implementation status

Implementation status per object type. **List** = enumerate all objects of that type; **Read/Update** = the object's content; **Rename/Delete** = the object itself.

✅ supported · ⚠️ partial (see notes) · ❌ not supported · — not applicable

For object types TiaMcp doesn't support yet, the notes say what the Openness API (TIA Portal V21) offers for them.

## PLC

| Object | List | Create | Read | Update | Rename | Delete | Notes |
|---|---|---|---|---|---|---|---|
| Block: SCL / FBD / LAD / global DB | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | `.s7dcl` / `.s7res` |
| Block: GRAPH | ✅ | ⚠️ | ✅ | ⚠️ | ✅ | ✅ | `.graph.il`; create clones an existing GRAPH block as template; topology editing is limited |
| Block: STL (whole block) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | `.awl` |
| Block: embedded STL networks | ✅ | ❌ | ✅ | ❌ | ✅ | ✅ | Read-only sidecar (`.stl-networks.awl`); rename/delete act on the whole block |
| Instance-DB | ✅ | ✅ | ✅ | ❌ | ✅ | ✅ | Created bound to an FB; read returns the resolved member list |
| Block group | ⚠️ | ✅ | — | — | ✅ | ✅ | Only visible through the group paths of the blocks in it; empty groups are not listed |
| UDT (PLC data type) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | `.s7dcl` |
| UDT group | ⚠️ | ✅ | — | — | ✅ | ✅ | Empty groups are not listed |
| Tag table | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | `.csv` |
| Tag | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | Through the tag table CSV; delete via `deleteMissing`; rename via `rename_plc_tag` (keeps references) |
| Tag table group | ✅ | ✅ | — | — | ✅ | ✅ | Empty groups are listed as `Group/` |
| User constant | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: create, delete, import/export |
| Watch table | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: create, delete, import/export |
| Watch/force table group | ❌ | ❌ | — | — | ❌ | ❌ | Openness: create, delete |
| Force table | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: import/export only |
| External source (`.scl` / `.awl` / `.db`) | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: create from file, delete, generate blocks from a source |
| External source group | ❌ | ❌ | — | — | ❌ | ❌ | Openness: create, delete |
| Technology object (PID, motion, counter) | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: create, delete, import/export |
| Technology object group | ❌ | ❌ | — | — | ❌ | ❌ | Openness: create, delete |
| PLC alarm text list | ✅ | ❌ | ✅ | ⚠️ | ❌ | ❌ | `.xlsx` via TIA's text list export/import; import replaces the complete content of every list in the file. Openness: create, delete |
| PLC alarm class | ❌ | — | ❌ | ❌ | — | — | Openness: import/export only |
| Supervision (ProDiag) settings | — | — | ❌ | ❌ | — | — | Openness: import/export only |
| Software unit (S7-1500) + relations | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: create, delete |
| OPC UA server interface / reference namespace | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: create, delete, import/export |

## HMI (WinCC Unified)

| Object | List | Create | Read | Update | Rename | Delete | Notes |
|---|---|---|---|---|---|---|---|
| HMI tag table | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | `.csv` |
| HMI tag | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | Through the tag table CSV; delete via `deleteMissing`; rename via `rename_hmi_tag` (keeps alarm references) |
| HMI tag table group | ✅ | ✅ | — | — | ✅ | ✅ | Empty groups are listed as `Group/` |
| Alarm class | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ | The list includes all properties |
| Alarm (discrete / analog) | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ | |
| Screen | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: create, delete; content via properties (no text export) |
| Screen group | ❌ | ❌ | — | — | ❌ | ❌ | Openness: create, delete |
| Script module (global) | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: import/export |
| Text list / graphic list | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: delete, import/export |
| HMI connection | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: create, delete |
| Data log / alarm log / logging tag | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: create, delete |

## HMI (classic WinCC: Comfort/Advanced, RT Advanced)

| Object | List | Create | Read | Update | Rename | Delete | Notes |
|---|---|---|---|---|---|---|---|
| Screen / screen template / popup screen | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | Openness `.xml`; create/update via import (`overwriteExisting`); duplicate screen numbers are rejected before import |
| HMI tag table | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | Openness `.xml` |
| Text list | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | Openness `.xml` |
| Graphic list | ✅ | ✅ | ⚠️ | ✅ | ❌ | ❌ | Export crashed TIA Portal V21, so it is opt-in only |
| Screen / template / popup / tag table folder | ✅ | ✅ | — | — | ❌ | ❌ | |
| Alarm | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Not reachable via classic Openness |

## Project

| Object | List | Create | Read | Update | Rename | Delete | Notes |
|---|---|---|---|---|---|---|---|
| TIA Portal instance | ✅ | — | — | — | — | — | |
| Project | — | ❌ | ✅ | ✅ | ❌ | ❌ | Open/close, status, save, save as |
| Source tree (whole project) | — | — | ✅ | ✅ | — | — | Export to disk / write back; never deletes |

## Hardware & library

| Object | List | Create | Read | Update | Rename | Delete | Notes |
|---|---|---|---|---|---|---|---|
| Device + device group | ⚠️ | ❌ | ❌ | ❌ | ❌ | ❌ | `list_plc_devices` / `list_hmi_devices` / `list_hmi_classic_devices` list PLC, WinCC Unified and classic WinCC devices. Openness: create, delete |
| Device item (module) | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: create (plug), delete; attributes via properties |
| Subnet / IO system | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: create, delete, connect nodes |
| Sync domain / MRP domain | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: create, delete |
| Web server / OPC UA user | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: create, delete |
| Library master copy + folder | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: create, delete |
| Library type + version + folder | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | Openness: create from blocks/UDTs, delete, export |
