using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using TiaMcp.Openness;

namespace TiaMcp.Mcp;

[McpServerToolType]
public sealed class TiaTools
{
    private readonly TiaSession _session;

    public TiaTools(TiaSession session)
    {
        _session = session;
    }

    /// <summary>
    /// The MCP SDK's default unhandled-exception path collapses any thrown exception into
    /// a generic "An error occurred invoking 'X'." with no detail, which is useless for an
    /// AI (or a person) trying to diagnose what actually went wrong - e.g. an unknown/stale
    /// plcName, or two concurrent tool calls racing on the same underlying Openness session.
    /// Every tool method below is wrapped in this so the real exception message and type
    /// always come back in the tool result text instead of being swallowed.
    /// </summary>
    private static async Task<string> Safe(Func<Task<string>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            return $"ERROR ({ex.GetType().Name}): {ex.Message}";
        }
    }

    [McpServerTool(Name = "tia_connect")]
    [Description("Attach to a running TIA Portal instance. With no arguments, attaches to whatever project is already open in the GUI - this only works when exactly one TIA Portal instance is running. If more than one instance is running, this returns an error listing their process ids instead of guessing or attaching to any of them (attaching is what triggers Openness's 'allow connection' dialog, so probing every instance would prompt all of them) - call list_tia_instances first, then call this again with processId set to the one you want. Pass projectPath to open a specific project (.apXX file) by path in the targeted instance - use this when the project you need isn't the one currently open. Call this first, and call it again (with no projectPath) if a PLC/device was renamed in the GUI or a tool starts reporting an unknown plcName - the PLC name index is only built at connect time.")]
    public Task<string> Connect(
        [Description("Optional full path to a .apXX project file to open. Omit to use whatever project is already open in TIA Portal.")] string? projectPath = null,
        [Description("Optional process id (PID) of the specific running TIA Portal instance to attach to, from list_tia_instances. Required when more than one TIA Portal instance is running; omit when only one is running.")] int? processId = null) => Safe(async () =>
    {
        var result = await _session.ConnectAsync(projectPath, processId);
        if (!result.Success)
        {
            return $"Connect failed: {result.Error}";
        }
        return $"Connected to project '{result.ProjectName}' at {result.ProjectPath}.";
    });

    [McpServerTool(Name = "list_tia_instances")]
    [Description("List running TIA Portal GUI instances (process id and window title, if visible) without attaching to any of them. Use this before tia_connect when more than one TIA Portal instance is running, to pick the right one via processId without triggering an Openness 'allow connection' prompt on every instance. Window title may be blank for a headless/orphaned instance or one not currently showing a project window - attach to it with tia_connect's processId to see what project (if any) it actually has open.")]
    public Task<string> ListInstances() => Safe(async () =>
    {
        var instances = await _session.ListInstancesAsync();
        if (instances.Count == 0)
        {
            return "No running TIA Portal instance found. Open TIA Portal first.";
        }
        return string.Join("\n", instances.Select(i => $"- PID {i.ProcessId}: {i.WindowTitle ?? "(no window title - headless or not currently showing a project)"}"));
    });

    [McpServerTool(Name = "save_project_as")]
    [Description("Save the currently connected project to a new folder, like TIA Portal's own 'Save project as' - creates a copy at the target location and continues working on that copy. Use this before risky changes if you want an on-disk checkpoint distinct from the original project.")]
    public Task<string> SaveProjectAs(
        [Description("Target directory for the new project copy. Must not already contain a project.")] string targetDirectory) => Safe(async () =>
    {
        var result = await _session.SaveProjectAsAsync(targetDirectory);
        if (!result.Success)
        {
            return $"Save As failed: {result.Error}";
        }
        return $"Saved project as '{result.ProjectName}' at {result.ProjectPath}.";
    });

    [McpServerTool(Name = "save_project")]
    [Description("Save the currently connected project in place (equivalent to Ctrl+S in the TIA Portal GUI). Block/tag/group writes made through this server only live in the open session until this is called - call it after a batch of changes you want persisted to disk.")]
    public Task<string> SaveProject() => Safe(async () =>
    {
        var result = await _session.SaveProjectAsync();
        return result.Success ? "Saved." : $"Save failed: {result.Error}";
    });

    [McpServerTool(Name = "close_project")]
    [Description("Close the currently connected project (equivalent to closing the project in the TIA Portal GUI). Unsaved changes are lost unless save_project was called first - this does not save automatically. Use this before tia_connect with a different projectPath if TIA Portal refuses to open a second project while one is already open.")]
    public Task<string> CloseProject() => Safe(async () =>
    {
        var result = await _session.CloseProjectAsync();
        return result.Success ? "Closed." : $"Close failed: {result.Error}";
    });

    [McpServerTool(Name = "project_status")]
    [Description("Report the connected project's save state: whether it has unsaved changes, and who/when it was last saved.")]
    public Task<string> ProjectStatus() => Safe(async () =>
    {
        var s = await _session.GetProjectStatusAsync();
        return $"Project '{s.Name}' at {s.Path}\n" +
               $"Unsaved changes: {(s.IsModified ? "YES" : "no")}\n" +
               $"Author: {s.Author}\n" +
               $"Last modified: {s.LastModified:u} by {s.LastModifiedBy}\n" +
               $"Version: {s.Version}";
    });

    [McpServerTool(Name = "list_plc_devices")]
    [Description("List PLC devices in the connected project.")]
    public Task<string> ListDevices() => Safe(async () =>
    {
        if (!_session.IsConnected) return "Not connected. Call tia_connect first.";
        var devices = (await _session.ListDevicesAsync())
            .Where(d => d.PlcSoftwareName != null)
            .Select(d => $"- {d.DeviceName} / {d.ItemName} -> PLC software '{d.PlcSoftwareName}'");
        return string.Join("\n", devices);
    });

    [McpServerTool(Name = "list_plc_blocks")]
    [Description("List all program blocks (with group path, language, and consistency) for a given PLC software name.")]
    public Task<string> ListBlocks([Description("PLC software name, from list_plc_devices")] string plcName) => Safe(async () =>
    {
        var blocks = await _session.ListBlocksAsync(plcName);
        return string.Join("\n", blocks.Select(b =>
            $"{(string.IsNullOrEmpty(b.GroupPath) ? "" : b.GroupPath + "/")}{b.Name}  [{b.Language}]{(b.Consistent ? "" : "  (INCONSISTENT)")}"));
    });

    [McpServerTool(Name = "read_plc_block")]
    [Description(@"Read a block's source as text. SCL blocks return SCL source. LAD/FBD blocks return the .s7dcl network notation plus a .s7res multilingual text file. GRAPH (S7-GRAPH/SFC) blocks return a synthesized '<name>.graph.il' DSL - GRAPH_BLOCK/INTERFACE/SEQUENCE/STEP/TRANSITION/CONNECTIONS keywords, with step actions shown near-verbatim and interlock/supervision/transition conditions rendered as SCL-style boolean text (AND/OR/NOT of tag references; uncommon FBD elements like comparisons or timers fall back to a generic 'PartName(args)' rendering, flagged as such rather than guessed). GRAPH also requires the block to be consistent first - if not, this returns an actionable error telling you to call compile_plc. For an instance-DB, also includes a '<name>.interface.txt' listing its full resolved parameter list (name : datatype, dot-qualified for nested FBs), read directly from the instance's live interface - this works even when the underlying FB type itself can't be exported (e.g. it's STL, per below).

STL support: a block whose entire declared language is STL returns real AWL mnemonic text as '<name>.awl' - TIA Portal's own ""Generate source"" output, the same plain-text format the GUI itself produces. A block whose primary language is FBD/LAD/SCL but that contains one or more embedded STL networks (common - TIA Portal's own export refuses the whole block if even one network inside is STL) still returns normal .s7dcl/.s7res for the non-STL content, with the STL network(s) appended separately as real AWL text, '<name>.stl-networks.awl'; if that rendering isn't producible, it falls back to '<name>.stl-networks.xml' (raw XML filtered to just those networks) instead, so the read never loses data.

Reconstructing GUI network order from the two sidecars: every network in the block - STL or not - is numbered 1-based by its position among ALL of the block's networks (matching the TIA Portal GUI's own numbering). Each STL network in '<name>.stl-networks.awl' is preceded by a '// Network N[: Title]' comment with that number; each STL network's placeholder slot in .s7dcl carries the same number implicitly by its position among the file's own NETWORK blocks in document order (a placeholder is any NETWORK whose leading attribute block has 'S7_Language := ""STL""' - it's otherwise empty, with a 'S7_NetworkTitle' pointing at a '.s7res' id you can resolve for the same title text as a cross-check). To read the block in GUI order: walk .s7dcl's NETWORK blocks in order, counting every one (STL placeholder or real) to get each one's N; whenever N matches an 'S7_Language := ""STL""' placeholder, that network's actual logic is network N in the AWL sidecar (or, if that sidecar wasn't produced, the correspondingly-labeled '<!-- Network N: ... -->' comment in '<name>.stl-networks.xml'). write_plc_block can write whole-block STL back (pass the '<name>.awl' text back as dclContent); embedded STL networks within a mixed FBD/LAD/SCL block are still read-only regardless of which sidecar format came back.

Do not call this tool multiple times concurrently for the same connection - calls share one underlying TIA Portal session and must run one at a time (call sequentially, not in a parallel batch).")]
    public Task<string> ReadBlock(
        [Description("PLC software name, from list_plc_devices")] string plcName,
        [Description("Block name, from list_plc_blocks")] string blockName) => Safe(async () =>
    {
        var result = await _session.ReadBlockAsync(plcName, blockName);
        if (!result.Success)
        {
            return $"Read failed: {result.Error}";
        }

        return string.Join("\n\n", result.Documents.Select(d => $"--- {d.FileName} ---\n{d.Content}"));
    });

    [McpServerTool(Name = "write_plc_block")]
    [Description(@"Overwrite an existing block's source in place (does not create new blocks). For SCL/FBD/LAD, pass back the .s7dcl content from read_plc_block, edited; pass resContent too if read_plc_block returned a .s7res file for this block. Does not auto-compile - call compile_plc afterward. Refuses (safely, no changes made) if the .s7res has the known duplicate multilingual-text-ID export defect - in that case, edit the block manually in the TIA Portal GUI instead.

For GRAPH blocks, pass back the '<name>.graph.il' text from read_plc_block as dclContent (resContent is unused for GRAPH - leave it out). This path auto-recompiles after writing and reports errors in the result. Editing an existing STEP/TRANSITION's attributes/actions/conditions is fully supported. Adding a brand-new STEP or TRANSITION number (one not already in the original) creates it, including the required Interface-section bookkeeping member TIA Portal's own GUI also creates alongside it. Removing a STEP or TRANSITION entirely is also supported (its element, and any CONNECTIONS line referencing it, are deleted - if a SEQUENCE's CONNECTIONS block was left out of the text you send back, dangling references left behind by a removed step/transition are cleaned up automatically; if it was included, every reference in it is validated and the write is refused if one points at a step/transition that no longer exists). BRANCHES/CONNECTIONS topology is otherwise fully rewritten from what you send back when that SEQUENCE's block is present in the text. The INTERFACE section itself is still read-only (aside from the automatic per-step/transition bookkeeping member just mentioned). A SUPERVISION/INTERLOCK/TRANSITION condition is only rewritten if its text actually changed from what read_plc_block would show now; if the new text still contains a construct read_plc_block had to fall back to generic 'PartName(args)' rendering for (comparisons, timers, etc.), the whole write is refused with no changes made rather than guessing - edit that specific condition in the TIA Portal GUI instead.

For a block whose entire declared language is STL, pass back the '<name>.awl' text read_plc_block returned as dclContent, edited (resContent is unused for STL - leave it out); this goes through TIA Portal's own ""Generate blocks from source"" mechanism, so it must stay valid AWL syntax and keep the same block name. Refuses outright (no changes made) for a block that instead contains one or more embedded STL networks within an otherwise FBD/LAD/SCL block, since a .s7dcl can't represent STL content at all - edit that case in the TIA Portal GUI instead until it's supported.")]
    public Task<string> WriteBlock(
        [Description("PLC software name, from list_plc_devices")] string plcName,
        [Description("Block name, from list_plc_blocks")] string blockName,
        [Description("The .s7dcl file content (SCL source, or LAD/FBD NETWORK/RUNG notation), or for GRAPH blocks the '<name>.graph.il' text from read_plc_block, or for whole-block-STL blocks the '<name>.awl' text from read_plc_block")] string dclContent,
        [Description("The .s7res file content, if read_plc_block returned one for this block; omit otherwise (always omit for GRAPH and STL)")] string? resContent = null) => Safe(async () =>
    {
        var docs = new List<BlockDocument> { new($"{blockName}.s7dcl", dclContent) };
        if (!string.IsNullOrEmpty(resContent))
        {
            docs.Add(new BlockDocument($"{blockName}.s7res", resContent!));
        }

        var result = await _session.WriteBlockAsync(plcName, blockName, docs);
        return $"{(result.Success ? "Success" : "FAILED")}\n{string.Join("\n", result.Messages)}";
    });

    [McpServerTool(Name = "create_plc_block")]
    [Description(@"Create a new FC, FB, global DB, or GRAPH (S7-GRAPH/SFC) block, targeting a name that doesn't exist yet. To instantiate a library FB as an instance-DB (e.g. a Typical from a library), use create_plc_instance_db instead - this tool cannot do that. Fails if a block with this name already exists anywhere in the PLC (names are unique per-PLC). Does not auto-compile - call compile_plc afterward: referencing a tag that doesn't exist yet is fine here and only surfaces as a compile error later, so PLC logic can be written before I/O tag tables are complete.

For SCL, write dclContent by hand (see the required header shape below). For FBD/LAD, do NOT hand-write NETWORK/RUNG notation from scratch - read_plc_block an existing block of the same language first (ideally a similar one in this project) to get real, valid dclContent/resContent to model the new block on, then adapt it. Freehand graphical-language notation is easy to get subtly wrong in ways that only surface as an import error.

The outer '{ S7_EditorMode := ""SCL"" }' attribute block immediately before FUNCTION/FUNCTION_BLOCK/DATA_BLOCK is REQUIRED for SCL blocks - omitting it fails with ""Please use either 'S7_PreferredLanguage' or 'S7_EditorMode' pragma to import the block."" Do not omit it even though it looks redundant with the '{ S7_Language := ""SCL"" }' pragma inside the body. FBD/LAD blocks carry their own equivalent pragma already, from whatever block you copied dclContent/resContent from - don't add the SCL one.

SCL FC:
{
    S7_EditorMode := ""SCL""
}
FUNCTION ""Name"" : Void
    { S7_Language := ""SCL"" }
    NETWORK
        ...
    END_NETWORK
END_FUNCTION

SCL FB:
{
    S7_EditorMode := ""SCL""
}
FUNCTION_BLOCK ""Name""
    VAR_INPUT
        In1 : Bool;
    END_VAR
    { S7_Language := ""SCL"" }
    NETWORK
        ...
    END_NETWORK
END_FUNCTION_BLOCK

SCL global DB (no BEGIN block, no inner language pragma - just the outer header plus VAR/END_VAR):
{
    S7_EditorMode := ""SCL""
}
DATA_BLOCK ""Name""
    VAR
        Tag1 : Bool;
    END_VAR
END_DATA_BLOCK

GRAPH: set graphTemplateBlockName instead of writing dclContent from scratch. GRAPH has no document-import route through Openness at all (see read_plc_block's description), and TIA's own block-creation API rejects every language but ProDiag when creating a block outright - so a brand-new GRAPH block can only be made by cloning an existing one, then reshaping the clone. Passing graphTemplateBlockName does exactly that: it clones the named existing GRAPH block (any GRAPH block in this PLC works as the template - its own step/transition count is irrelevant, since the clone is immediately reshaped) via TIA's library MasterCopy mechanism, renames the clone to blockName, compiles it, then applies dclContent to it as '<name>.graph.il' text through the same reshaping pipeline write_plc_block uses for GRAPH blocks (see write_plc_block's description for that DSL, and GraphConverter's format reference in README.md) - so dclContent here must be GRAPH IL text, not SCL/FBD/LAD, and resContent must be omitted. Write dclContent by reading an existing GRAPH block with read_plc_block first and adapting its '.graph.il' text (same guidance as FBD/LAD - don't hand-write this DSL from scratch). If cloning/compiling the template succeeds but reshaping to dclContent fails, the block is left in place as an exact copy of the template (under blockName) rather than rolled back - fix dclContent and call write_plc_block to retry the reshape.

Whole-block STL: set stlBlock to true instead of using the SCL header shapes above. dclContent must then be real AWL mnemonic text (the '<name>.awl' format read_plc_block returns for an existing STL block - read one first and adapt it, don't hand-write AWL from scratch) whose FUNCTION/FUNCTION_BLOCK/DATA_BLOCK header name matches blockName exactly; resContent must be omitted. This goes through TIA Portal's own ""Generate blocks from source"" mechanism (PlcExternalSource.GenerateBlocksFromSource), not ImportFromDocuments - which rejects AWL syntax outright (a plain create with AWL content fails cleanly with a .s7dcl syntax error at the TITLE line). groupPath places the new block in that group; empty string for the root, matching every other language here.")]
    public Task<string> CreateBlock(
        [Description("PLC software name, from list_plc_devices")] string plcName,
        [Description("Group path to create the block in, e.g. 'Group1/Subgroup'; empty string for the root")] string groupPath,
        [Description("New block name")] string blockName,
        [Description("For SCL/FBD/LAD: the .s7dcl file content. For GRAPH (graphTemplateBlockName set): the '<name>.graph.il' text describing the desired step/transition/branch/connection layout - same DSL read_plc_block/write_plc_block use for GRAPH blocks. For whole-block STL (stlBlock set): the '<name>.awl' AWL mnemonic text from read_plc_block")] string dclContent,
        [Description("The .s7res file content, if modeling this block on one that read_plc_block returned a .s7res for; omit for SCL and always omit for GRAPH/STL")] string? resContent = null,
        [Description("Name of an existing GRAPH block in this PLC to clone as the structural starting point, from list_plc_blocks - set this to create a GRAPH block instead of SCL/FBD/LAD; omit for every other language")] string? graphTemplateBlockName = null,
        [Description("Set true to create a whole-block-STL block from AWL text (dclContent) instead of SCL/FBD/LAD/GRAPH; omit/false for every other language")] bool stlBlock = false) => Safe(async () =>
    {
        if (!string.IsNullOrEmpty(graphTemplateBlockName))
        {
            if (!string.IsNullOrEmpty(resContent))
            {
                return "resContent must be omitted when creating a GRAPH block (graphTemplateBlockName set) - GRAPH blocks carry no separate .s7res file.";
            }

            var graphResult = await _session.CreateGraphBlockAsync(plcName, groupPath, blockName, graphTemplateBlockName!, dclContent);
            return $"{(graphResult.Success ? "Success" : "FAILED")}\n{string.Join("\n", graphResult.Messages)}";
        }

        if (stlBlock)
        {
            if (!string.IsNullOrEmpty(resContent))
            {
                return "resContent must be omitted when creating a whole-block-STL block (stlBlock set) - STL blocks carry no separate .s7res file.";
            }

            var stlDocs = new List<BlockDocument> { new($"{blockName}.awl", dclContent) };
            var stlResult = await _session.CreateStlBlockAsync(plcName, groupPath, blockName, stlDocs);
            return $"{(stlResult.Success ? "Success" : "FAILED")}\n{string.Join("\n", stlResult.Messages)}";
        }

        var docs = new List<BlockDocument> { new($"{blockName}.s7dcl", dclContent) };
        if (!string.IsNullOrEmpty(resContent))
        {
            docs.Add(new BlockDocument($"{blockName}.s7res", resContent!));
        }

        var result = await _session.CreateBlockAsync(plcName, groupPath, blockName, docs);
        return $"{(result.Success ? "Success" : "FAILED")}\n{string.Join("\n", result.Messages)}";
    });

    [McpServerTool(Name = "list_plc_data_types")]
    [Description("List all PLC data types (UDTs), with group path, for a given PLC software name.")]
    public Task<string> ListPlcDataTypes([Description("PLC software name, from list_plc_devices")] string plcName) => Safe(async () =>
    {
        var types = await _session.ListPlcTypesAsync(plcName);
        return string.Join("\n", types.Select(t =>
            $"{(string.IsNullOrEmpty(t.GroupPath) ? "" : t.GroupPath + "/")}{t.Name}"));
    });

    [McpServerTool(Name = "read_plc_udt")]
    [Description("Read a UDT's (PLC data type's) definition as plain text: 'TYPE Name : STRUCT ... END_STRUCT; END_TYPE', plus a .s7res file when members have comments (pass that back as resContent to write_plc_udt/create_plc_udt). A field typed as another UDT is shown as '_.OtherTypeName' (the leading '_.' is TIA Portal's own real export syntax for a UDT reference, not a rendering choice by this server) - read that UDT separately if you need its fields too, same as how a block's own interface shows a UDT-typed member by name only.")]
    public Task<string> ReadUdt(
        [Description("PLC software name, from list_plc_devices")] string plcName,
        [Description("UDT name, from list_plc_data_types")] string typeName) => Safe(async () =>
    {
        var result = await _session.ReadUdtAsync(plcName, typeName);
        if (!result.Success)
        {
            return $"Read failed: {result.Error}";
        }

        return string.Join("\n\n", result.Documents.Select(d => $"--- {d.FileName} ---\n{d.Content}"));
    });

    [McpServerTool(Name = "write_plc_udt")]
    [Description(@"Overwrite an existing UDT's definition in place (does not create new UDTs). Pass back the text from read_plc_udt, edited. Does not auto-compile - call compile_plc afterward; a UDT change can affect every block that references it, so check compile results carefully.

Member comments go through an { S7_MLC := ""MLC_x"" } attribute plus the matching entry in resContent; '//' comments in the source are dropped by TIA Portal.

TYPE
    Name : STRUCT
        Field1 : Bool;
        Field2 : Int;
        Nested : _.SomeOtherUdt;
    END_STRUCT;
END_TYPE")]
    public Task<string> WriteUdt(
        [Description("PLC software name, from list_plc_devices")] string plcName,
        [Description("UDT name, from list_plc_data_types")] string typeName,
        [Description("The full 'TYPE ... END_TYPE' text, edited")] string content,
        [Description("The .s7res file content, if read_plc_udt returned one for this UDT; omit otherwise")] string? resContent = null) => Safe(async () =>
    {
        var docs = UdtDocuments(typeName, content, resContent);
        var result = await _session.WriteUdtAsync(plcName, typeName, docs);
        return $"{(result.Success ? "Success" : "FAILED")}\n{string.Join("\n", result.Messages)}";
    });

    [McpServerTool(Name = "create_plc_udt")]
    [Description(@"Create a new UDT (PLC data type). Fails if a UDT with this name already exists anywhere in the PLC (names are unique per-PLC). The target group must already exist - use create_plc_udt_group first, or pass an empty groupPath for the root. Does not auto-compile - call compile_plc afterward.

The type name inside the content must match typeName exactly - that's where TIA Portal actually reads the name from (the parameter is just which file to import).

Member comments go through an { S7_MLC := ""MLC_x"" } attribute plus the matching entry in resContent; '//' comments in the source are dropped by TIA Portal.

TYPE
    Name : STRUCT
        Field1 : Bool;
        Field2 : Int;
    END_STRUCT;
END_TYPE")]
    public Task<string> CreateUdt(
        [Description("PLC software name, from list_plc_devices")] string plcName,
        [Description("Group path to create the UDT in, e.g. 'Types/Subgroup'; empty string for the root")] string groupPath,
        [Description("New UDT name")] string typeName,
        [Description("The full 'TYPE ... END_TYPE' text")] string content,
        [Description("The .s7res file content, if modeling this UDT on one that read_plc_udt returned a .s7res for; omit otherwise")] string? resContent = null) => Safe(async () =>
    {
        var docs = UdtDocuments(typeName, content, resContent);
        var result = await _session.CreateUdtAsync(plcName, groupPath, typeName, docs);
        return $"{(result.Success ? "Success" : "FAILED")}\n{string.Join("\n", result.Messages)}";
    });

    private static List<BlockDocument> UdtDocuments(string typeName, string content, string? resContent)
    {
        var docs = new List<BlockDocument> { new($"{typeName}.s7dcl", content) };
        if (!string.IsNullOrEmpty(resContent)) docs.Add(new BlockDocument($"{typeName}.s7res", resContent!));
        return docs;
    }

    [McpServerTool(Name = "delete_plc_udt")]
    [Description("Delete a UDT (PLC data type) by name. Fails with TIA Portal's own error if the UDT is still used as a member type by another UDT or block interface. This is destructive - confirm with the user before calling.")]
    public Task<string> DeleteUdt(
        [Description("PLC software name, from list_plc_devices")] string plcName,
        [Description("UDT name to delete, from list_plc_data_types")] string typeName) => Safe(async () =>
    {
        var result = await _session.DeleteUdtAsync(plcName, typeName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "rename_plc_udt")]
    [Description("Rename an existing UDT (PLC data type) in place. Fails if a UDT with the new name already exists (names are unique per-PLC). Does not auto-compile - call compile_plc afterward, since a UDT rename can affect every block that references it.")]
    public Task<string> RenameUdt(
        [Description("PLC software name, from list_plc_devices")] string plcName,
        [Description("UDT name to rename, from list_plc_data_types")] string typeName,
        [Description("New UDT name")] string newName) => Safe(async () =>
    {
        var result = await _session.RenameUdtAsync(plcName, typeName, newName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "create_plc_udt_group")]
    [Description("Create a new UDT (PLC data type) group (folder) under a given parent group path.")]
    public Task<string> CreateUdtGroup(
        [Description("PLC software name, from list_plc_devices")] string plcName,
        [Description("Parent group path, e.g. 'Types'; empty string for the root")] string parentGroupPath,
        [Description("Name of the new group")] string groupName) => Safe(async () =>
    {
        var result = await _session.CreateTypeGroupAsync(plcName, parentGroupPath, groupName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "delete_plc_udt_group")]
    [Description("Delete a UDT (PLC data type) group (folder) and everything in it, including nested UDTs and subgroups. This is destructive - confirm with the user before calling.")]
    public Task<string> DeleteUdtGroup(
        [Description("PLC software name, from list_plc_devices")] string plcName,
        [Description("Full group path to delete, e.g. 'Types/Subgroup'")] string groupPath) => Safe(async () =>
    {
        var result = await _session.DeleteTypeGroupAsync(plcName, groupPath);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "rename_plc_udt_group")]
    [Description("Rename an existing UDT (PLC data type) group (folder) in place, without moving it to a different parent.")]
    public Task<string> RenameUdtGroup(
        [Description("PLC software name, from list_plc_devices")] string plcName,
        [Description("Full group path to rename, e.g. 'Types/Subgroup'")] string groupPath,
        [Description("New group name")] string newName) => Safe(async () =>
    {
        var result = await _session.RenameTypeGroupAsync(plcName, groupPath, newName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "create_plc_instance_db")]
    [Description("Create an instance-DB bound to a specific FB type (e.g. instantiate a Typical FB from a library for one piece of equipment). This is the correct way to do this - instance-DB source text isn't accepted by create_plc_block's SCL-import route (TIA Portal's importer rejects it with a syntax error on the interface/BEGIN section; instance-DBs must be created via this dedicated Openness API instead). Does not auto-compile - call compile_plc afterward.")]
    public Task<string> CreateInstanceDb(
        [Description("PLC software name, from list_plc_devices")] string plcName,
        [Description("Group path to create the DB in, e.g. 'Instances'; empty string for the root")] string groupPath,
        [Description("New instance-DB name")] string dbName,
        [Description("Name of the FB type to instantiate (must already exist in this PLC, e.g. a library FB from list_plc_blocks)")] string instanceOfFbName) => Safe(async () =>
    {
        var result = await _session.CreateInstanceDbAsync(plcName, groupPath, dbName, instanceOfFbName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "create_plc_group")]
    [Description("Create a new block group (folder) under a given parent group path.")]
    public Task<string> CreateGroup(
        [Description("PLC software name, from list_plc_devices")] string plcName,
        [Description("Parent group path, e.g. 'Group1'; empty string for the root")] string parentGroupPath,
        [Description("Name of the new group")] string groupName) => Safe(async () =>
    {
        var result = await _session.CreateBlockGroupAsync(plcName, parentGroupPath, groupName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "delete_plc_group")]
    [Description("Delete a block group (folder) and everything in it, including nested blocks and subgroups. This is destructive - confirm with the user before calling.")]
    public Task<string> DeleteGroup(
        [Description("PLC software name, from list_plc_devices")] string plcName,
        [Description("Full group path to delete, e.g. 'Group1/Subgroup'")] string groupPath) => Safe(async () =>
    {
        var result = await _session.DeleteBlockGroupAsync(plcName, groupPath);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "rename_plc_group")]
    [Description("Rename an existing block group (folder) in place, without moving it to a different parent.")]
    public Task<string> RenamePlcGroup(
        [Description("PLC software name, from list_plc_devices")] string plcName,
        [Description("Full group path to rename, e.g. 'Group1/Subgroup'")] string groupPath,
        [Description("New group name")] string newName) => Safe(async () =>
    {
        var result = await _session.RenamePlcGroupAsync(plcName, groupPath, newName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "delete_plc_block")]
    [Description("Delete a single block (any language, including a GRAPH block or an instance-DB) by name, without touching its containing group or sibling blocks. Use this to recover a block that write_plc_block refuses to touch (e.g. a GRAPH block stuck INCONSISTENT - see write_plc_block/create_plc_block's GRAPH notes) by deleting it and recreating it with create_plc_block/create_plc_instance_db. To rename a block instead, use rename_plc_block. An instance-DB still bound to an FB/FC must be deleted before the FB/FC itself - deleting the type block first fails with TIA's own error naming the dependency. This is destructive - confirm with the user before calling.")]
    public Task<string> DeleteBlock(
        [Description("PLC software name, from list_plc_devices")] string plcName,
        [Description("Block name to delete, from list_plc_blocks")] string blockName) => Safe(async () =>
    {
        var result = await _session.DeleteBlockAsync(plcName, blockName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "rename_plc_block")]
    [Description("Rename an existing block (any language, including a GRAPH block or an instance-DB) in place. Fails if a block with the new name already exists (names are unique per-PLC). Safe even on a block with real dependents (callers, a bound instance-DB): TIA resolves block/DB references by internal object identity, not by name text, so callers automatically follow the rename. The block and its dependents go temporarily INCONSISTENT, same as any block edit - call compile_plc afterward to resolve that.")]
    public Task<string> RenamePlcBlock(
        [Description("PLC software name, from list_plc_devices")] string plcName,
        [Description("Block name to rename, from list_plc_blocks")] string blockName,
        [Description("New block name")] string newName) => Safe(async () =>
    {
        var result = await _session.RenamePlcBlockAsync(plcName, blockName, newName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "list_plc_tag_tables")]
    [Description("List all PLC tag tables (with group path) for a given PLC software name. Groups that contain no tag table anywhere below them are listed as their path with a trailing '/', e.g. 'IO/Spare/'.")]
    public Task<string> ListTagTables([Description("PLC software name, from list_plc_devices")] string plcName) => Safe(async () =>
    {
        var tables = await _session.ListTagTablesAsync(plcName);
        var groups = await _session.ListTagTableGroupsAsync(plcName);
        return FormatTableList(tables.Select(t => (t.GroupPath, t.Name)).ToList(), groups);
    });

    // One line per table as 'group/path/name', plus 'group/path/' for every group with no table
    // anywhere below it - otherwise empty groups would be invisible.
    private static string FormatTableList(IReadOnlyList<(string GroupPath, string Name)> tables, IReadOnlyList<string> groups)
    {
        var lines = tables.Select(t => $"{(string.IsNullOrEmpty(t.GroupPath) ? "" : t.GroupPath + "/")}{t.Name}").ToList();
        lines.AddRange(groups
            .Where(g => !tables.Any(t => t.GroupPath == g || t.GroupPath.StartsWith(g + "/")))
            .Select(g => g + "/"));
        return string.Join("\n", lines);
    }

    [McpServerTool(Name = "read_plc_tag_table")]
    [Description("Read all tags in a PLC tag table, as Excel-compatible CSV text (comma-separated, quoted fields where needed, CRLF line endings, header row). Columns: Name, DataType, LogicalAddress, Comment, ExternalAccessible, ExternalVisible, ExternalWritable (the last three are the 'Accessible/Visible/Writable from HMI/OPC UA/Web API' flags, True/False). The output can be edited and passed back to write_plc_tag_table.")]
    public Task<string> ReadTagTable(
        [Description("PLC software name, from list_plc_devices")] string plcName,
        [Description("Tag table name, from list_plc_tag_tables")] string tableName) => Safe(async () =>
    {
        var result = await _session.ReadTagTableAsync(plcName, tableName);
        if (!result.Success)
        {
            return $"Read failed: {result.Error}";
        }

        return FormatTagTable(result.Tags);
    });

    [McpServerTool(Name = "write_plc_tag_table")]
    [Description(@"Create, update and optionally delete tags in an existing PLC tag table from CSV text, in the same format read_plc_tag_table returns (header row required; columns Name, DataType, LogicalAddress, Comment, ExternalAccessible, ExternalVisible, ExternalWritable - only Name and DataType are required, the rest may be omitted).
Rows matching an existing tag name are updated in place; other rows are created as new tags. An empty or missing LogicalAddress/Comment/flag cell leaves that value unchanged (Comment is written in the project editing language). Flags accept True/False/1/0; ExternalVisible/ExternalWritable require ExternalAccessible=True.
By default tags not in the CSV are left alone. With deleteMissing=true they are DELETED, so the table ends up exactly matching the CSV - this is destructive, confirm with the user before calling. Changing a tag's name in the CSV is a delete + create, which breaks references to it in blocks - use rename_plc_tag instead.
The input is validated first (duplicate names, bad booleans, missing Name/DataType); on a validation error nothing is written.")]
    public Task<string> WriteTagTable(
        [Description("PLC software name, from list_plc_devices")] string plcName,
        [Description("Tag table name, from list_plc_tag_tables")] string tableName,
        [Description("CSV text with a header row, as returned by read_plc_tag_table")] string csv,
        [Description("Delete tags that are in the table but not in the CSV (default false)")] bool deleteMissing = false) => Safe(async () =>
    {
        TagSpec[] tags;
        try { tags = ParseTagCsv(csv); }
        catch (FormatException ex) { return $"FAILED: {ex.Message}\nNothing was written."; }

        var result = await _session.WriteTagTableAsync(plcName, tableName, tags, deleteMissing);
        return $"{(result.Success ? "Success" : "Completed with failures")}\n{string.Join("\n", result.Messages)}";
    });

    [McpServerTool(Name = "rename_plc_tag")]
    [Description("Rename a single PLC tag in place, in whichever tag table it lives. Unlike changing the name in a write_plc_tag_table CSV (a delete + create), this keeps references to the tag in blocks intact; read_plc_block shows the new name in those blocks only after compile_plc. Fails if a tag with the new name already exists (names are unique per-PLC).")]
    public Task<string> RenameTag(
        [Description("PLC software name, from list_plc_devices")] string plcName,
        [Description("Current tag name")] string tagName,
        [Description("New tag name")] string newName) => Safe(async () =>
    {
        var result = await _session.RenameTagAsync(plcName, tagName, newName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "create_plc_tag_table")]
    [Description("Create a new, empty PLC tag table. Fails if a tag table with this name already exists anywhere in the PLC (names are unique per-PLC, not per-group). Use write_plc_tag_table afterward to add tags to it.")]
    public Task<string> CreateTagTable(
        [Description("PLC software name, from list_plc_devices")] string plcName,
        [Description("Group path to create the table under, e.g. 'IO/Group1'; empty string for the root")] string groupPath,
        [Description("Name of the new tag table")] string tableName) => Safe(async () =>
    {
        var result = await _session.CreateTagTableAsync(plcName, groupPath, tableName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "delete_plc_tag_table")]
    [Description("Delete a PLC tag table (and all its tags) by name. This is destructive - confirm with the user before calling.")]
    public Task<string> DeleteTagTable(
        [Description("PLC software name, from list_plc_devices")] string plcName,
        [Description("Tag table name to delete, from list_plc_tag_tables")] string tableName) => Safe(async () =>
    {
        var result = await _session.DeleteTagTableAsync(plcName, tableName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "rename_plc_tag_table")]
    [Description("Rename an existing PLC tag table in place. Fails if a tag table with the new name already exists (names are unique per-PLC).")]
    public Task<string> RenameTagTable(
        [Description("PLC software name, from list_plc_devices")] string plcName,
        [Description("Tag table name to rename, from list_plc_tag_tables")] string tableName,
        [Description("New tag table name")] string newName) => Safe(async () =>
    {
        var result = await _session.RenameTagTableAsync(plcName, tableName, newName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "create_plc_tag_table_group")]
    [Description("Create a new PLC tag table group (folder) under a given parent group path.")]
    public Task<string> CreateTagTableGroup(
        [Description("PLC software name, from list_plc_devices")] string plcName,
        [Description("Parent group path, e.g. 'IO'; empty string for the root")] string parentGroupPath,
        [Description("Name of the new group")] string groupName) => Safe(async () =>
    {
        var result = await _session.CreateTagTableGroupAsync(plcName, parentGroupPath, groupName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "delete_plc_tag_table_group")]
    [Description("Delete a PLC tag table group (folder) and everything in it, including nested tag tables and subgroups. This is destructive - confirm with the user before calling.")]
    public Task<string> DeleteTagTableGroup(
        [Description("PLC software name, from list_plc_devices")] string plcName,
        [Description("Full group path to delete, e.g. 'IO/Group1'")] string groupPath) => Safe(async () =>
    {
        var result = await _session.DeleteTagTableGroupAsync(plcName, groupPath);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "rename_plc_tag_table_group")]
    [Description("Rename an existing PLC tag table group (folder) in place, without moving it to a different parent.")]
    public Task<string> RenameTagTableGroup(
        [Description("PLC software name, from list_plc_devices")] string plcName,
        [Description("Full group path to rename, e.g. 'IO/Group1'")] string groupPath,
        [Description("New group name")] string newName) => Safe(async () =>
    {
        var result = await _session.RenameTagTableGroupAsync(plcName, groupPath, newName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "list_hmi_devices")]
    [Description("List WinCC Unified HMI devices in the connected project. Classic WinCC (Comfort/Advanced/RT) panels are not supported - Openness exposes no typed tag/alarm object model for them (tags are name-only with generic attribute access, and there are no alarm objects at all), so only WinCC Unified devices appear here.")]
    public Task<string> ListHmiDevices() => Safe(async () =>
    {
        if (!_session.IsConnected) return "Not connected. Call tia_connect first.";
        var devices = (await _session.ListHmiDevicesAsync())
            .Select(d => $"- {d.DeviceName} / {d.ItemName} -> HMI software '{d.HmiSoftwareName}'");
        return string.Join("\n", devices);
    });

    [McpServerTool(Name = "list_hmi_tag_tables")]
    [Description("List all WinCC Unified HMI tag tables (with group path) for a given HMI software name. Groups that contain no tag table anywhere below them are listed as their path with a trailing '/', e.g. 'Plant/Spare/'.")]
    public Task<string> ListHmiTagTables([Description("HMI software name, from list_hmi_devices")] string hmiName) => Safe(async () =>
    {
        var tables = await _session.ListHmiTagTablesAsync(hmiName);
        var groups = await _session.ListHmiTagTableGroupsAsync(hmiName);
        return FormatTableList(tables.Select(t => (t.GroupPath, t.Name)).ToList(), groups);
    });

    [McpServerTool(Name = "read_hmi_tag_table")]
    [Description("Read all tags in a WinCC Unified HMI tag table, as Excel-compatible CSV text. Columns: Name, DataType, Address, Connection, PlcName, PlcTag, Comment, AcquisitionCycle. The output can be edited and passed back to write_hmi_tag_table.")]
    public Task<string> ReadHmiTagTable(
        [Description("HMI software name, from list_hmi_devices")] string hmiName,
        [Description("HMI tag table name, from list_hmi_tag_tables")] string tableName) => Safe(async () =>
    {
        var result = await _session.ReadHmiTagTableAsync(hmiName, tableName);
        if (!result.Success)
        {
            return $"Read failed: {result.Error}";
        }

        return FormatHmiTagTable(result.Tags);
    });

    [McpServerTool(Name = "write_hmi_tag_table")]
    [Description(@"Create, update and optionally delete tags in an existing WinCC Unified HMI tag table from CSV text, in the same format read_hmi_tag_table returns (header row required; columns Name, DataType, Address, Connection, PlcName, PlcTag, Comment, AcquisitionCycle - only Name and DataType are required, the rest may be omitted).
Rows matching an existing tag name are updated in place; other rows are created as new tags. An empty or missing Address/Connection/PlcTag/Comment/AcquisitionCycle cell leaves that value unchanged (Comment is written in the project editing language). AcquisitionCycle takes the cycle name as read_hmi_tag_table shows it (e.g. T500ms, T1s, T2s) and can only be changed on PLC-bound tags; internal tags are fixed at T1s.
PLC binding: set Connection (an existing HMI connection name, e.g. from another tag via read_hmi_tag_table) and PlcTag (the PLC tag's name, dot-qualified for a nested DB member). PlcName is derived by TIA Portal from the connection and is ignored on write. When both Connection and PlcTag are set, DataType is IGNORED and TIA Portal derives the type from the PLC binding (as the GUI does) - this is also what makes struct/UDT-typed PLC tags bindable as a single HMI tag, so any placeholder DataType will do. DataType is only used for internal tags (no Connection/PlcTag).
By default tags not in the CSV are left alone. With deleteMissing=true they are DELETED, so the table ends up exactly matching the CSV - this is destructive, confirm with the user before calling. Changing a tag's name in the CSV is a delete + create - use rename_hmi_tag instead.
The input is validated first (duplicate names, missing Name/DataType); on a validation error nothing is written.")]
    public Task<string> WriteHmiTagTable(
        [Description("HMI software name, from list_hmi_devices")] string hmiName,
        [Description("HMI tag table name, from list_hmi_tag_tables")] string tableName,
        [Description("CSV text with a header row, as returned by read_hmi_tag_table")] string csv,
        [Description("Delete tags that are in the table but not in the CSV (default false)")] bool deleteMissing = false) => Safe(async () =>
    {
        HmiTagSpec[] tags;
        try { tags = ParseHmiTagCsv(csv); }
        catch (FormatException ex) { return $"FAILED: {ex.Message}\nNothing was written."; }

        var result = await _session.WriteHmiTagTableAsync(hmiName, tableName, tags, deleteMissing);
        return $"{(result.Success ? "Success" : "Completed with failures")}\n{string.Join("\n", result.Messages)}";
    });

    [McpServerTool(Name = "rename_hmi_tag")]
    [Description("Rename a single WinCC Unified HMI tag in place, in whichever tag table it lives. Unlike changing the name in a write_hmi_tag_table CSV (a delete + create), this renames the existing tag object. Fails if a tag with the new name already exists (names are unique per-HMI).")]
    public Task<string> RenameHmiTag(
        [Description("HMI software name, from list_hmi_devices")] string hmiName,
        [Description("Current tag name")] string tagName,
        [Description("New tag name")] string newName) => Safe(async () =>
    {
        var result = await _session.RenameHmiTagAsync(hmiName, tagName, newName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "create_hmi_tag_table")]
    [Description("Create a new, empty WinCC Unified HMI tag table. Fails if a tag table with this name already exists anywhere in the HMI (names are unique per-HMI, not per-group). Use write_hmi_tag_table afterward to add tags to it.")]
    public Task<string> CreateHmiTagTable(
        [Description("HMI software name, from list_hmi_devices")] string hmiName,
        [Description("Group path to create the table under; empty string for the root")] string groupPath,
        [Description("Name of the new HMI tag table")] string tableName) => Safe(async () =>
    {
        var result = await _session.CreateHmiTagTableAsync(hmiName, groupPath, tableName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "delete_hmi_tag_table")]
    [Description("Delete a WinCC Unified HMI tag table (and all its tags) by name. This is destructive - confirm with the user before calling.")]
    public Task<string> DeleteHmiTagTable(
        [Description("HMI software name, from list_hmi_devices")] string hmiName,
        [Description("HMI tag table name to delete, from list_hmi_tag_tables")] string tableName) => Safe(async () =>
    {
        var result = await _session.DeleteHmiTagTableAsync(hmiName, tableName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "rename_hmi_tag_table")]
    [Description("Rename an existing WinCC Unified HMI tag table in place. Fails if a tag table with the new name already exists (names are unique per-HMI).")]
    public Task<string> RenameHmiTagTable(
        [Description("HMI software name, from list_hmi_devices")] string hmiName,
        [Description("HMI tag table name to rename, from list_hmi_tag_tables")] string tableName,
        [Description("New HMI tag table name")] string newName) => Safe(async () =>
    {
        var result = await _session.RenameHmiTagTableAsync(hmiName, tableName, newName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "create_hmi_tag_table_group")]
    [Description("Create a new WinCC Unified HMI tag table group (folder) under a given parent group path.")]
    public Task<string> CreateHmiTagTableGroup(
        [Description("HMI software name, from list_hmi_devices")] string hmiName,
        [Description("Parent group path; empty string for the root")] string parentGroupPath,
        [Description("Name of the new group")] string groupName) => Safe(async () =>
    {
        var result = await _session.CreateHmiTagTableGroupAsync(hmiName, parentGroupPath, groupName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "delete_hmi_tag_table_group")]
    [Description("Delete a WinCC Unified HMI tag table group (folder) and everything in it, including nested tag tables and subgroups. This is destructive - confirm with the user before calling.")]
    public Task<string> DeleteHmiTagTableGroup(
        [Description("HMI software name, from list_hmi_devices")] string hmiName,
        [Description("Full group path to delete")] string groupPath) => Safe(async () =>
    {
        var result = await _session.DeleteHmiTagTableGroupAsync(hmiName, groupPath);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "rename_hmi_tag_table_group")]
    [Description("Rename an existing WinCC Unified HMI tag table group (folder) in place, without moving it to a different parent.")]
    public Task<string> RenameHmiTagTableGroup(
        [Description("HMI software name, from list_hmi_devices")] string hmiName,
        [Description("Full group path to rename")] string groupPath,
        [Description("New group name")] string newName) => Safe(async () =>
    {
        var result = await _session.RenameHmiTagTableGroupAsync(hmiName, groupPath, newName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "list_hmi_alarm_classes")]
    [Description("List WinCC Unified HMI alarm classes (name, priority, log, id, whether it's a built-in system class) for a given HMI software name. Alarm classes have no folder/group concept in Openness, even if TIA Portal's GUI shows them organized into folders.")]
    public Task<string> ListHmiAlarmClasses([Description("HMI software name, from list_hmi_devices")] string hmiName) => Safe(async () =>
    {
        var classes = await _session.ListHmiAlarmClassesAsync(hmiName);
        return FormatCsv(
            new[] { "Name", "Priority", "Log", "Id", "IsSystem" },
            classes.Select(c => new[] { c.Name, c.Priority.ToString(), c.Log ?? "", c.Id.ToString(), c.IsSystem.ToString() }));
    });

    [McpServerTool(Name = "write_hmi_alarm_class")]
    [Description("Create or update a WinCC Unified HMI alarm class by name, priority, and log. Updates in place if the name already exists, creates it otherwise.")]
    public Task<string> WriteHmiAlarmClass(
        [Description("HMI software name, from list_hmi_devices")] string hmiName,
        [Description("Alarm class name")] string name,
        [Description("Priority (0-255); omit to leave unchanged on an existing class")] int? priority = null,
        [Description("Log name; omit to leave unchanged on an existing class")] string? log = null) => Safe(async () =>
    {
        var result = await _session.WriteHmiAlarmClassAsync(hmiName, new HmiAlarmClassSpec(name, priority, log));
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "delete_hmi_alarm_class")]
    [Description("Delete a WinCC Unified HMI alarm class by name. Fails with TIA Portal's own error if any alarm still references this class. This is destructive - confirm with the user before calling.")]
    public Task<string> DeleteHmiAlarmClass(
        [Description("HMI software name, from list_hmi_devices")] string hmiName,
        [Description("Alarm class name to delete, from list_hmi_alarm_classes")] string name) => Safe(async () =>
    {
        var result = await _session.DeleteHmiAlarmClassAsync(hmiName, name);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "list_hmi_alarms")]
    [Description("List WinCC Unified HMI alarms (discrete and analog, unified into one CSV with a Type column) for a given HMI software name, as Excel-compatible CSV text. Analog-only fields (Condition, ConditionValue) are blank for discrete alarms and vice versa. Only EventText/InfoText are exposed (WinCC Unified's EventText1-9 alternate-text slots are not).")]
    public Task<string> ListHmiAlarms(
        [Description("HMI software name, from list_hmi_devices")] string hmiName,
        [Description("Optional filter: 'Discrete' or 'Analog'; omit for both")] string? type = null) => Safe(async () =>
    {
        var alarms = await _session.ListHmiAlarmsAsync(hmiName, type);
        return FormatCsv(
            new[] { "Type", "Name", "AlarmClass", "EventText", "InfoText", "TriggerAddress", "Condition", "ConditionValue", "RaisedStateTag", "AuditClass", "Area", "Origin" },
            alarms.Select(a => new[]
            {
                a.Type, a.Name, a.AlarmClass ?? "", a.EventText ?? "", a.InfoText ?? "", a.TriggerAddress ?? "",
                a.Condition ?? "", a.ConditionValue ?? "", a.RaisedStateTag ?? "", a.AuditClass ?? "", a.Area ?? "", a.Origin ?? ""
            }));
    });

    [McpServerTool(Name = "read_hmi_alarm")]
    [Description("Read one WinCC Unified HMI alarm's full detail (discrete or analog) as key:value lines.")]
    public Task<string> ReadHmiAlarm(
        [Description("HMI software name, from list_hmi_devices")] string hmiName,
        [Description("'Discrete' or 'Analog'")] string type,
        [Description("Alarm name, from list_hmi_alarms")] string alarmName) => Safe(async () =>
    {
        var a = await _session.ReadHmiAlarmAsync(hmiName, type, alarmName);
        if (a == null) return $"Alarm '{alarmName}' (type '{type}') not found in HMI '{hmiName}'.";
        return $"Type: {a.Type}\n" +
               $"Name: {a.Name}\n" +
               $"AlarmClass: {a.AlarmClass}\n" +
               $"EventText: {a.EventText}\n" +
               $"InfoText: {a.InfoText}\n" +
               $"TriggerAddress: {a.TriggerAddress}\n" +
               $"Condition: {a.Condition}\n" +
               $"ConditionValue: {a.ConditionValue}\n" +
               $"RaisedStateTag: {a.RaisedStateTag}\n" +
               $"AuditClass: {a.AuditClass}\n" +
               $"Area: {a.Area}\n" +
               $"Origin: {a.Origin}";
    });

    [McpServerTool(Name = "write_hmi_alarm")]
    [Description("Create or update a WinCC Unified HMI alarm (discrete or analog). Updates in place if the name already exists (within its type), creates it otherwise. Only fields you pass are changed; omit a field to leave it unchanged on an existing alarm. Analog alarms additionally use Condition (one of: 'LowerLimit', 'UpperLimit', 'Equal', 'NotEqual', 'LowerLimitOrEqual', 'UpperLimitOrEqual' - the real WinCC Unified HmiAlarmCondition enum member names) and ConditionValue (a plain number, e.g. '80' or '80.5' - parsed as a double; a non-numeric value fails that field). Each field is set independently - a failure on one field is reported without aborting the rest. EventText/InfoText are passed as plain text; internally they're stored as a small HTML fragment ('<body><p>...</p></body>') which this tool handles transparently. raisedStateTag IS how you bind an alarm's trigger via Openness, despite the GUI showing the trigger as three separate fields (Trigger tag / Trigger bit / Connection of trigger tag): setting it to an existing tag name makes the read-only TriggerAddress auto-compute to the correct bit address. Only verified for the common case (a dedicated bool tag at bit 0, already referenced by some existing alarm) - not verified for a PLC signal with no existing HMI-alarm binding yet, or for a non-zero trigger bit/explicit connection. triggerAddress itself (the raw combined string, e.g. 'Foo.Bar.x0') remains a read-only computed field and cannot be set directly via Openness - passing it returns a clear failure message for that field only, the rest of the write still applies - use raisedStateTag instead.")]
    public Task<string> WriteHmiAlarm(
        [Description("HMI software name, from list_hmi_devices")] string hmiName,
        [Description("'Discrete' or 'Analog'")] string type,
        [Description("Alarm name")] string name,
        [Description("Name of an existing alarm class, from list_hmi_alarm_classes")] string? alarmClass = null,
        [Description("Alarm event text, plain text")] string? eventText = null,
        [Description("Alarm info text, plain text")] string? infoText = null,
        [Description("NOT SETTABLE via Openness - passing this returns a failure message for this field only (see tool description); kept so a full record can still be assembled from list_hmi_alarms output. Use raisedStateTag to actually bind the alarm's trigger.")] string? triggerAddress = null,
        [Description("Analog only: 'LowerLimit', 'UpperLimit', 'Equal', 'NotEqual', 'LowerLimitOrEqual', or 'UpperLimitOrEqual'")] string? condition = null,
        [Description("Analog only: the condition's comparison value, as a plain number (e.g. '80' or '80.5')")] string? conditionValue = null,
        [Description("The tag whose state drives this alarm - this IS the alarm's trigger tag (setting it makes the read-only TriggerAddress auto-compute to match), not merely a status readback, despite the GUI showing the trigger as separate Trigger tag/bit/connection fields. Can be a dotted member-path into a structured (UDT-typed) HMI tag (e.g. 'Device1.Alarm.Fault', letting one whole-struct tag back several distinct alarms), but the base tag ('Device1' in the example) must already exist as an HMI tag first; if it doesn't, this tool detects the unresolved trigger after the write (TriggerAddress stays blank) and reports it as a failure rather than a false success.")] string? raisedStateTag = null,
        [Description("Audit class")] string? auditClass = null,
        [Description("Area")] string? area = null,
        [Description("Origin")] string? origin = null) => Safe(async () =>
    {
        var spec = new HmiAlarmSpec(type, name, alarmClass, eventText, infoText, triggerAddress, condition, conditionValue, raisedStateTag, auditClass, area, origin);
        var result = await _session.WriteHmiAlarmAsync(hmiName, spec);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "delete_hmi_alarm")]
    [Description("Delete a WinCC Unified HMI alarm (discrete or analog) by name. This is destructive - confirm with the user before calling.")]
    public Task<string> DeleteHmiAlarm(
        [Description("HMI software name, from list_hmi_devices")] string hmiName,
        [Description("'Discrete' or 'Analog'")] string type,
        [Description("Alarm name to delete, from list_hmi_alarms")] string alarmName) => Safe(async () =>
    {
        var result = await _session.DeleteHmiAlarmAsync(hmiName, type, alarmName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "compile_plc")]
    [Description("Compile a PLC's software and report errors/warnings.")]
    public Task<string> Compile([Description("PLC software name, from list_plc_devices")] string plcName) => Safe(async () =>
    {
        var result = await _session.CompileAsync(plcName);
        var messages = string.Join("\n", result.Messages);
        return $"State: {result.State}  Errors: {result.ErrorCount}  Warnings: {result.WarningCount}\n{messages}";
    });

    [McpServerTool(Name = "search_plc")]
    [Description("Search block, tag-table, and UDT names (substring match) for a given PLC.")]
    public Task<string> Search(
        [Description("PLC software name, from list_plc_devices")] string plcName,
        [Description("Text to search for in block, tag table, and UDT names")] string text) => Safe(async () =>
    {
        var (blocks, tables, types) = await _session.SearchAsync(plcName, text);
        var lines = new List<string>();
        lines.AddRange(blocks.Select(b => $"[block] {(string.IsNullOrEmpty(b.GroupPath) ? "" : b.GroupPath + "/")}{b.Name}"));
        lines.AddRange(tables.Select(t => $"[tag table] {(string.IsNullOrEmpty(t.GroupPath) ? "" : t.GroupPath + "/")}{t.Name}"));
        lines.AddRange(types.Select(t => $"[udt] {(string.IsNullOrEmpty(t.GroupPath) ? "" : t.GroupPath + "/")}{t.Name}"));
        return lines.Count == 0 ? "No matches." : string.Join("\n", lines);
    });

    [McpServerTool(Name = "read_source_tree")]
    [Description(@"Export project source to disk, into folders that mirror the TIA Portal project tree: <targetDirectory>/<PlcName>/Program blocks/<group path>/..., .../PLC tags/..., .../PLC data types/..., and <targetDirectory>/<HmiSoftwareName>/HMI tags/... and .../HMI alarms/ (DiscreteAlarms.csv, AnalogAlarms.csv, AlarmClasses.csv - fixed files, alarms have no groups in Openness). Read the files with your file tools; edit them and call write_source_tree to write the changes back.

Pass items to read only part of the project (much faster than a full read): e.g. one block to look at or refresh, or a group. A read overwrites the selected items' files, including unsaved edits to them. An item that no longer exists in the project (deleted or renamed in TIA Portal) has its files deleted.

Files per item: blocks and UDTs keep TIA Portal's own document format (.s7dcl, plus .s7res holding the comment/title texts its S7_MLC attributes refer to); a GRAPH block is '<name>.graph.il', a whole-block-STL block '<name>.awl' (TIA Portal's own 'Generate source' text). Read-only companions, never written back: '<name>.interface.txt' (an instance DB's resolved interface) and '<name>.stl-networks.awl'/'.xml' (the STL networks inside an otherwise FBD/LAD/SCL block; the .s7dcl keeps an empty placeholder NETWORK with S7_Language := ""STL"" at each one's position). Tag tables and alarms are Excel-compatible CSV with a UTF-8 BOM.

An item that fails to export (e.g. an inconsistent block - GRAPH blocks must be compiled first) is reported, and files from an earlier read of it are renamed with a '.stale' suffix rather than left looking current. '_export_summary.txt' lists every item in the tree; '_manifest.json' is write_source_tree's bookkeeping - don't edit it. Returns counts, failures, and for a partial read each item's files.")]
    public Task<string> ReadSourceTree(
        [Description("Root directory of the source tree. Created if it doesn't exist; keep using the same one for the project.")] string targetDirectory,
        [Description("Optional: read only these items - 'Name', 'group/path/Name', or 'group/path/' for everything below a group (case-insensitive; for HMI alarms 'DiscreteAlarms', 'AnalogAlarms', 'AlarmClasses' or 'HMI alarms/'). Omit to read everything.")] string[]? items = null,
        [Description("Optional: read only this PLC or HMI software name")] string? deviceName = null) => Safe(async () =>
    {
        if (!_session.IsConnected) return "Not connected. Call tia_connect first.";

        var selection = items is { Length: > 0 } ? items : null;
        var partial = selection != null || deviceName != null;
        Directory.CreateDirectory(targetDirectory);
        var previous = SourceTreeManifest.Load(targetDirectory);
        if (partial && previous?.Project != null && previous.Project != _session.ProjectName)
        {
            return $"'{targetDirectory}' holds the source tree of project '{previous.Project}', not of the connected project '{_session.ProjectName}'. " +
                   "Read the whole project (no items/deviceName) to replace it, or use another directory.";
        }

        var startedAt = DateTime.Now;
        var (exported, devices) = await ExportItems(selection == null ? null : (_, kind, groupPath, name) => Selects(selection, kind, groupPath, name), deviceName);
        if (devices.Count == 0) return deviceName == null ? "No PLC or HMI devices found." : $"No PLC or HMI software named '{deviceName}'.";

        var manifest = previous ?? new SourceTreeManifest();
        WriteExport(targetDirectory, manifest, exported);

        // Items in the part that was read which the project doesn't have (any more).
        var gone = manifest.Items
            .Where(i => (deviceName == null || string.Equals(i.Device, deviceName, StringComparison.OrdinalIgnoreCase))
                        && (selection == null || Selects(selection, i.Kind, i.GroupPath, i.Name))
                        && !exported.Any(e => i.Is(e.Device, e.Kind, e.Name)))
            .ToList();
        foreach (var item in gone) RemoveFromTree(targetDirectory, manifest, item);

        manifest.Project = _session.ProjectName;
        manifest.LastRead = startedAt;
        manifest.Save(targetDirectory);
        var summaryPath = Path.Combine(targetDirectory, "_export_summary.txt");
        File.WriteAllText(summaryPath, FormatSummary(targetDirectory, manifest));

        var result = new StringBuilder();
        result.AppendLine($"Read {exported.Count} item(s) into {targetDirectory} in {(DateTime.Now - startedAt).TotalSeconds:0} s: " +
                          $"{exported.Count(e => e.Ok)} ok, {exported.Count(e => !e.Ok)} failed, {gone.Count} removed.");
        foreach (var item in exported)
        {
            if (!item.Ok) result.AppendLine($"  FAILED  {Describe(item.Kind, item.GroupPath, item.Name)}: {item.Error}");
            else if (partial) result.AppendLine($"  OK      {Describe(item.Kind, item.GroupPath, item.Name)}: {string.Join(", ", item.Files.Select(f => f.Path))}");
        }
        foreach (var item in gone) result.AppendLine($"  REMOVED {Describe(item)} - no longer in the project, its files were deleted");
        result.AppendLine($"Full item list: {summaryPath}");
        return result.ToString();
    });

    // One parsed 'OK'/'FAILED'/'STALE' line from read_source_tree's _export_summary.txt (or a
    // hand-trimmed copy/inline excerpt of it) - see write_source_tree. A plain class rather than a
    // record: this project (net48) has no IsExternalInit polyfill for record/init-only support.
    private sealed class ImportListItem
    {
        public ImportListItem(string deviceName, string kind, string groupPath, string itemName, bool ok)
        {
            DeviceName = deviceName;
            Kind = kind;
            GroupPath = groupPath;
            ItemName = itemName;
            Ok = ok;
        }

        public string DeviceName { get; }
        public string Kind { get; }
        public string GroupPath { get; }
        public string ItemName { get; }
        public bool Ok { get; }

        public bool Is(string device, string kind, string name) =>
            string.Equals(DeviceName, device, StringComparison.OrdinalIgnoreCase)
            && Kind == kind
            && string.Equals(ItemName, name, StringComparison.OrdinalIgnoreCase);
    }

    [McpServerTool(Name = "write_source_tree")]
    [Description(@"Write a source tree from read_source_tree back into the connected project: every item whose files were edited since the last read, and every new file - a new block, UDT or tag table is created by adding its file in the right folder (the folder gives the group, the file name the item's name; for a new block or UDT start from an existing file of the same kind). Written items are read back into the tree afterward, so their files show what TIA Portal made of them. Not compiled - call compile_plc afterward, and after an FB interface change read the instance DBs again. Deleting a file does not delete the item in the project (use the delete_* tools).

Refuses (FAILED, nothing written for that item) to overwrite an item that was changed in TIA Portal since it was read, or that exists in the project but was never read into the tree; force=true overwrites anyway. A tree read from another project (e.g. to restore it into an emptied copy) is written in full, without these checks.

Writing rules per file type:
- SCL block: the '{ S7_EditorMode := ""SCL"" }' attribute block before FUNCTION/FUNCTION_BLOCK/DATA_BLOCK is required. FBD/LAD: never hand-write NETWORK/RUNG notation from scratch - copy a similar existing block's .s7dcl/.s7res and adapt it.
- Comments and titles: an { S7_MLC := ""MLC_x"" } attribute (S7_NetworkTitle/S7_NetworkComment for networks) referring to an entry in the .s7res; '//' comments in the source are dropped by TIA Portal. Keep text ids unique within a .s7res.
- GRAPH ('.graph.il'): step/transition attributes, actions and conditions are writable, steps/transitions can be added or removed, and BRANCHES/CONNECTIONS of a SEQUENCE are rewritten when present; INTERFACE and PREOPERATIONS are read-only. A condition still containing a construct shown as generic 'PartName(args)' is refused. A new GRAPH block is made by cloning an existing GRAPH block of the PLC (picked automatically) and reshaping it. The write recompiles the block and reports errors.
- Whole-block STL ('.awl'): AWL text as TIA Portal's 'Generate source' produces it, block name in the header unchanged. A block with embedded STL networks can't be written.
- Instance DB: its .s7dcl ('DATA_BLOCK ""x"" ... : FB_y'); the FB must exist.
- PLC tag table CSV: Name, DataType, LogicalAddress, Comment, ExternalAccessible, ExternalVisible, ExternalWritable (True/False). Rows update or add tags; an empty cell leaves that value unchanged; tags missing from the CSV are kept. Renaming a tag in the CSV makes a new tag - use rename_plc_tag.
- HMI tag table CSV: Name, DataType, Address, Connection, PlcName, PlcTag, Comment, AcquisitionCycle. Bind to a PLC tag with Connection + PlcTag (dot-qualified for a DB member); DataType then follows from the PLC tag. PlcName is ignored on write. AcquisitionCycle (e.g. T1s) only on PLC-bound tags.
- HMI alarms CSV: rows update or add alarms by name. RaisedStateTag binds the trigger (a tag or member path whose base HMI tag exists); TriggerAddress is computed and read-only. EventText/InfoText are plain text. AlarmClasses.csv: Name, Priority, Log; system classes accept no Priority change.

Writes in dependency order: UDTs, then tag tables, then blocks, with UDTs and blocks each split into layers by what their source references (nested UDTs first; global DBs and called FBs before their callers and instance DBs), compiling the PLC between UDT layers - imports can fail, or a UDT can silently lose nested start values, when what they reference isn't there yet.

itemList limits what is considered, in _export_summary.txt's format (a trimmed copy's path, or the lines inline); all=true writes those items even if unchanged. Set dryRun to preview what would be created/updated/skipped/refused.

Returns (and also writes to '<sourceDirectory>/_import_report.txt') one CREATED/UPDATED/SKIPPED/FAILED line per item, grouped by device, with a final counts line.")]
    public Task<string> WriteSourceTree(
        [Description("Root directory of the source tree, as passed to read_source_tree.")] string sourceDirectory,
        [Description("Optional: consider only these items - a path to a (trimmed) copy of _export_summary.txt, or that list text inline. Omit to consider the whole tree, including new files.")] string? itemList = null,
        [Description("Write every item considered, not only those changed or added since read_source_tree. Default false.")] bool all = false,
        [Description("Create items that don't yet exist in the connected project. Default true.")] bool createMissing = true,
        [Description("Overwrite items that already exist in the connected project. Default true.")] bool overwriteExisting = true,
        [Description("Also overwrite items that were changed in TIA Portal since read_source_tree, discarding those changes. Default false.")] bool force = false,
        [Description("Preview only - report what would happen without writing any changes.")] bool dryRun = false) => Safe(async () =>
    {
        if (!_session.IsConnected) return "Not connected. Call tia_connect first.";

        var manifest = SourceTreeManifest.Load(sourceDirectory);
        // A tree read from another project (e.g. restoring into an emptied copy) isn't a working
        // copy of this one: all of it is written, and nothing is compared with or refreshed from it.
        var workingCopy = manifest != null && manifest.Project == _session.ProjectName;
        var notes = new List<string>();

        List<ImportListItem> items;
        var invalidCount = 0;
        if (!string.IsNullOrEmpty(itemList) || manifest == null)
        {
            var listPath = string.IsNullOrEmpty(itemList) ? Path.Combine(sourceDirectory, "_export_summary.txt") : itemList!;
            if (string.IsNullOrEmpty(itemList) && !File.Exists(listPath))
                return $"No itemList given and no source tree found at '{sourceDirectory}'. Run read_source_tree first, or pass itemList.";

            var allItems = ParseSourceTreeList(File.Exists(listPath) ? File.ReadAllText(listPath) : itemList!);
            if (allItems.Count == 0) return "No items recognized in the given list.";
            invalidCount = allItems.Count(i => !i.Ok);
            items = allItems.Where(i => i.Ok).ToList();
        }
        else
        {
            items = manifest.Items.Where(i => i.Ok)
                .Select(i => new ImportListItem(i.Device, i.Kind, i.GroupPath, i.Name, true))
                .Concat(NewItemsInTree(sourceDirectory, manifest))
                .ToList();
        }

        if (manifest == null)
            notes.Add("No _manifest.json in the tree (read before this version): every listed item is written, without change or conflict checks.");
        else if (!workingCopy)
            notes.Add($"The tree was read from project '{manifest.Project}', not the connected '{_session.ProjectName}': every listed item is written, and nothing is compared with or refreshed from this project.");

        if (workingCopy && !all)
        {
            var changed = new List<ImportListItem>();
            var unchanged = 0;
            foreach (var item in items)
            {
                var entry = manifest!.Find(item.DeviceName, item.Kind, item.ItemName);
                var state = entry == null ? DiskState.Changed : CompareWithDisk(sourceDirectory, entry);
                if (state == DiskState.Changed) changed.Add(item);
                else if (state == DiskState.Unchanged) unchanged++;
                else notes.Add($"{Describe(item.Kind, item.GroupPath, item.ItemName)}: its files were deleted from the tree. That doesn't delete it in the project - use the delete_* tool for that.");
            }
            if (unchanged > 0) notes.Add($"{unchanged} item(s) unchanged since read_source_tree were not written (all=true writes them anyway).");
            items = changed;
        }

        var conflicts = new List<(ImportListItem Item, string Reason)>();
        if (workingCopy && !force && items.Count > 0)
        {
            conflicts = await FindConflicts(manifest!, items);
            items = items.Where(i => !conflicts.Any(c => c.Item == i)).ToList();
        }

        var startedAt = DateTime.Now;
        var summary = new StringBuilder();
        summary.AppendLine($"Source tree write - {startedAt:yyyy-MM-dd HH:mm:ss}{(dryRun ? " (dry run)" : "")}");
        summary.AppendLine($"Project: {_session.ProjectName}");
        summary.AppendLine($"Source: {sourceDirectory}");
        summary.AppendLine();

        var written = new List<ImportListItem>();
        int totalCreated = 0, totalUpdated = 0, totalSkipped = 0, totalFailed = 0;
        foreach (var deviceGroup in items.Concat(conflicts.Select(c => c.Item)).GroupBy(i => i.DeviceName, StringComparer.OrdinalIgnoreCase))
        {
            var deviceName = deviceGroup.Key;
            var deviceDir = Path.Combine(sourceDirectory, Sanitize(deviceName));
            var deviceItems = deviceGroup.Where(items.Contains).ToList();
            var isHmi = deviceGroup.All(i => i.Kind.StartsWith("HMI"));
            summary.AppendLine(isHmi ? $"=== {deviceName} (HMI) ===" : $"=== {deviceName} ===");

            var logs = new List<(List<string> Created, List<string> Updated, List<string> Skipped, List<string> Failed)>();
            var conflictLines = conflicts.Where(c => deviceGroup.Contains(c.Item)).Select(c => $"{Describe(c.Item.Kind, c.Item.GroupPath, c.Item.ItemName)}: {c.Reason}").ToList();
            if (conflictLines.Count > 0) logs.Add((new List<string>(), new List<string>(), new List<string>(), conflictLines));

            // Dependency order: tags can be UDT-typed, and blocks reference both UDTs and tags -
            // a block imported before what it references fails to import.
            var udtItems = deviceItems.Where(i => i.Kind == "udt").ToList();
            if (udtItems.Count > 0) logs.Add(await ImportTypes(deviceName, deviceDir, udtItems, createMissing, overwriteExisting, dryRun, written));

            var tagTableItems = deviceItems.Where(i => i.Kind == "tag table").ToList();
            if (tagTableItems.Count > 0) logs.Add(await ImportTagTables(deviceName, deviceDir, tagTableItems, createMissing, overwriteExisting, dryRun, written));

            var blockItems = deviceItems.Where(i => i.Kind == "block").ToList();
            if (blockItems.Count > 0) logs.Add(await ImportBlocks(deviceName, deviceDir, blockItems, createMissing, overwriteExisting, dryRun, written));

            var hmiTagTableItems = deviceItems.Where(i => i.Kind == "HMI tag table").ToList();
            if (hmiTagTableItems.Count > 0) logs.Add(await ImportHmiTagTables(deviceName, deviceDir, hmiTagTableItems, createMissing, overwriteExisting, dryRun, written));

            var hmiAlarmItems = deviceItems.Where(i => HmiAlarmKinds.Contains(i.Kind)).ToList();
            if (hmiAlarmItems.Count > 0) logs.Add(await ImportHmiAlarms(deviceName, deviceDir, hmiAlarmItems, createMissing, overwriteExisting, dryRun, written));

            foreach (var log in logs)
            {
                foreach (var line in log.Created) summary.AppendLine($"  CREATED  {line}");
                foreach (var line in log.Updated) summary.AppendLine($"  UPDATED  {line}");
                foreach (var line in log.Skipped) summary.AppendLine($"  SKIPPED  {line}");
                foreach (var line in log.Failed) summary.AppendLine($"  FAILED   {line}");
                totalCreated += log.Created.Count;
                totalUpdated += log.Updated.Count;
                totalSkipped += log.Skipped.Count;
                totalFailed += log.Failed.Count;
            }
            summary.AppendLine();
        }

        if (invalidCount > 0)
        {
            notes.Add($"{invalidCount} line(s) in the list were FAILED/STALE in the source export and were not written.");
            totalSkipped += invalidCount;
        }

        // What TIA Portal made of the written items (it normalizes e.g. quoting) becomes the tree's
        // new baseline, so the next write sees them as unchanged.
        if (workingCopy && written.Count > 0)
        {
            var (exported, _) = await ExportItems((device, kind, _, name) => written.Any(w => w.Is(device, kind, name)), null);
            WriteExport(sourceDirectory, manifest!, exported);
            manifest!.Save(sourceDirectory);
            File.WriteAllText(Path.Combine(sourceDirectory, "_export_summary.txt"), FormatSummary(sourceDirectory, manifest));
            notes.Add($"Refreshed the {exported.Count(e => e.Ok)} written item(s) in the tree from the project.");
        }

        foreach (var note in notes) summary.AppendLine(note);
        if (totalCreated + totalUpdated + totalFailed + totalSkipped == 0 && !dryRun) summary.AppendLine("Nothing to write.");
        summary.AppendLine($"Done: {totalCreated} created, {totalUpdated} updated, {totalSkipped} skipped, {totalFailed} failed. Finished {DateTime.Now:yyyy-MM-dd HH:mm:ss} (started {startedAt:HH:mm:ss}).");

        var summaryText = summary.ToString();
        File.WriteAllText(Path.Combine(sourceDirectory, "_import_report.txt"), summaryText);
        return summaryText;
    });

    private enum DiskState { Unchanged, Changed, Deleted }

    // Compares an item's files on disk with what read_source_tree last wrote for it. A writable
    // file of the item that the read didn't produce (e.g. a .s7res added by hand) counts as a change.
    private static DiskState CompareWithDisk(string root, SourceTreeItem entry)
    {
        var expected = entry.WritableFiles.ToList();
        var present = expected.Where(f => File.Exists(Path.Combine(root, f.Key))).ToList();
        if (expected.Count > 0 && present.Count == 0) return DiskState.Deleted;
        if (present.Count < expected.Count) return DiskState.Changed;
        if (present.Any(f => SourceTreeManifest.Hash(File.ReadAllText(Path.Combine(root, f.Key))) != f.Value)) return DiskState.Changed;

        var dir = ItemDir(entry.Device, entry.Kind, entry.GroupPath);
        var stem = Sanitize(ItemStem(entry.Kind, entry.Name));
        return SourceExtensions.Select(ext => $"{dir}/{stem}{ext}")
            .Any(path => File.Exists(Path.Combine(root, path)) && !entry.Files.Keys.Contains(path, StringComparer.OrdinalIgnoreCase))
            ? DiskState.Changed
            : DiskState.Unchanged;
    }

    private static readonly string[] SourceExtensions = { ".s7dcl", ".s7res", ".graph.il", ".awl", ".csv" };

    // Source files in the tree that no read put there: new items to create. Kind and group path
    // follow from the folder (see ItemDir), the name from the file name.
    private static List<ImportListItem> NewItemsInTree(string root, SourceTreeManifest manifest)
    {
        var found = new List<ImportListItem>();
        foreach (var deviceDir in Directory.GetDirectories(root))
        {
            var dirName = Path.GetFileName(deviceDir);
            var device = manifest.Items.Select(i => i.Device).FirstOrDefault(d => Sanitize(d) == dirName) ?? dirName;

            void Scan(string section, string kind, params string[] extensions)
            {
                var sectionDir = Path.Combine(deviceDir, section);
                if (!Directory.Exists(sectionDir)) return;
                foreach (var file in Directory.GetFiles(sectionDir, "*", SearchOption.AllDirectories))
                {
                    var fileName = Path.GetFileName(file);
                    var ext = extensions.FirstOrDefault(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));
                    if (ext == null || SourceTreeManifest.IsReadOnlyCompanion(fileName)) continue;

                    var name = fileName.Substring(0, fileName.Length - ext.Length);
                    var relDir = Path.GetDirectoryName(file)!.Substring(sectionDir.Length).Trim(Path.DirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/');
                    if (manifest.Find(device, kind, name) == null && !found.Any(i => i.Is(device, kind, name)))
                        found.Add(new ImportListItem(device, kind, relDir, name, true));
                }
            }

            Scan("Program blocks", "block", ".s7dcl", ".graph.il", ".awl");
            Scan("PLC data types", "udt", ".s7dcl");
            Scan("PLC tags", "tag table", ".csv");
            Scan("HMI tags", "HMI tag table", ".csv");
            foreach (var kind in HmiAlarmKinds.Where(k => File.Exists(Path.Combine(deviceDir, "HMI alarms", ItemStem(k, "") + ".csv"))))
            {
                if (manifest.Find(device, kind, "") == null) found.Add(new ImportListItem(device, kind, "", "", true));
            }
        }
        return found;
    }

    // Items to write that were changed in TIA Portal since read_source_tree - their export now
    // differs from what the manifest recorded - or that are new in the tree but already exist in
    // the project. Writing them would discard what was done in TIA Portal.
    private async Task<List<(ImportListItem Item, string Reason)>> FindConflicts(SourceTreeManifest manifest, List<ImportListItem> items)
    {
        var (current, _) = await ExportItems((device, kind, _, name) => items.Any(i => i.Is(device, kind, name)), null);
        var conflicts = new List<(ImportListItem, string)>();
        foreach (var exported in current.Where(e => e.Ok))
        {
            var item = items.First(i => i.Is(exported.Device, exported.Kind, exported.Name));
            var entry = manifest.Find(exported.Device, exported.Kind, exported.Name);
            if (entry == null)
            {
                conflicts.Add((item, "already exists in the project, but was never read into this tree. Read it (read_source_tree with items; this overwrites the file, so keep a copy), merge, and write again - or pass force=true to overwrite the project's version."));
                continue;
            }

            var now = exported.Files.Where(f => !SourceTreeManifest.IsReadOnlyCompanion(f.Path))
                .ToDictionary(f => f.Path, f => SourceTreeManifest.Hash(f.Content), StringComparer.OrdinalIgnoreCase);
            var then = entry.WritableFiles.ToList();
            if (now.Count != then.Count || then.Any(f => !now.TryGetValue(f.Key, out var hash) || hash != f.Value))
                conflicts.Add((item, "changed in TIA Portal since read_source_tree. Read it again (read_source_tree with items; this overwrites the file, so keep a copy of your edit), merge, and write again - or pass force=true to overwrite the project's version."));
        }
        return conflicts;
    }

    private async Task<(List<string> Created, List<string> Updated, List<string> Skipped, List<string> Failed)> ImportBlocks(
        string plcName, string plcDir, List<ImportListItem> items, bool createMissing, bool overwriteExisting, bool dryRun, List<ImportListItem> written)
    {
        var created = new List<string>();
        var updated = new List<string>();
        var skipped = new List<string>();
        var failed = new List<string>();

        var existingBlocks = (await _session.ListBlocksAsync(plcName)).Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ensuredGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var writes = new List<PendingWrite>();
        string? graphTemplateName = null;
        var graphTemplateSearched = false;

        foreach (var item in items)
        {
            var label = string.IsNullOrEmpty(item.GroupPath) ? item.ItemName : $"{item.GroupPath}/{item.ItemName}";
            var dir = GroupDir(plcDir, "Program blocks", item.GroupPath);
            var (docs, error) = ReadBlockDocuments(dir, item.ItemName);
            if (docs == null)
            {
                failed.Add($"block '{label}': {error}");
                continue;
            }

            var exists = existingBlocks.Contains(item.ItemName);
            if (exists)
            {
                if (!overwriteExisting) { skipped.Add($"block '{label}' - already exists, overwriteExisting=false"); continue; }
                if (dryRun) { updated.Add($"block '{label}' (dry run)"); continue; }

                var name = item.ItemName;
                writes.Add(new PendingWrite(item, $"block '{label}'", docs, false, () => _session.WriteBlockAsync(plcName, name, docs)));
                continue;
            }

            if (!createMissing) { skipped.Add($"block '{label}' - does not exist, createMissing=false"); continue; }

            var isGraph = docs.Count == 1 && docs[0].FileName.EndsWith(".graph.il", StringComparison.OrdinalIgnoreCase);
            if (isGraph)
            {
                if (!graphTemplateSearched)
                {
                    graphTemplateName = (await _session.ListBlocksAsync(plcName)).FirstOrDefault(b => b.Language == "GRAPH")?.Name;
                    graphTemplateSearched = true;
                }
                if (graphTemplateName == null)
                {
                    skipped.Add($"block '{label}' - no existing GRAPH block in PLC '{plcName}' to use as a clone template; create one manually first");
                    continue;
                }
                if (dryRun) { created.Add($"block '{label}' (dry run, GRAPH via template '{graphTemplateName}')"); continue; }

                var (graphGroup, graphName, template) = (item.GroupPath, item.ItemName, graphTemplateName);
                writes.Add(new PendingWrite(item, $"block '{label}' (GRAPH, template '{template}')", docs, true,
                    () => _session.CreateGraphBlockAsync(plcName, graphGroup, graphName, template, docs[0].Content)));
                continue;
            }

            if (dryRun) { created.Add($"block '{label}' (dry run)"); continue; }

            await EnsureBlockGroupPath(plcName, item.GroupPath, ensuredGroups);
            var (groupPath, blockName) = (item.GroupPath, item.ItemName);
            // Whole-block STL can't go through ImportFromDocuments - see create_plc_block's stlBlock.
            var isStl = docs.Count == 1 && docs[0].FileName.EndsWith(".awl", StringComparison.OrdinalIgnoreCase);
            writes.Add(new PendingWrite(item, $"block '{label}'", docs, true, isStl
                ? () => _session.CreateStlBlockAsync(plcName, groupPath, blockName, docs)
                : () => _session.CreateBlockAsync(plcName, groupPath, blockName, docs, checkExisting: false)));
        }

        await WriteInDependencyOrder(plcName, writes, created, updated, failed, written, compileBetweenLayers: false);
        return (created, updated, skipped, failed);
    }

    private async Task<(List<string> Created, List<string> Updated, List<string> Skipped, List<string> Failed)> ImportTypes(
        string plcName, string plcDir, List<ImportListItem> items, bool createMissing, bool overwriteExisting, bool dryRun, List<ImportListItem> written)
    {
        var created = new List<string>();
        var updated = new List<string>();
        var skipped = new List<string>();
        var failed = new List<string>();

        var existingTypes = (await _session.ListPlcTypesAsync(plcName)).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ensuredGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var writes = new List<PendingWrite>();

        foreach (var item in items)
        {
            var label = string.IsNullOrEmpty(item.GroupPath) ? item.ItemName : $"{item.GroupPath}/{item.ItemName}";
            var dir = GroupDir(plcDir, "PLC data types", item.GroupPath);
            var sanitizedName = Sanitize(item.ItemName);
            var file = Path.Combine(dir, sanitizedName + ".s7dcl");
            if (!File.Exists(file)) file = Path.Combine(dir, sanitizedName + ".txt");
            if (!File.Exists(file)) { failed.Add($"udt '{label}': no source file found in {dir}"); continue; }

            var resFile = Path.Combine(dir, sanitizedName + ".s7res");
            var docs = UdtDocuments(item.ItemName, File.ReadAllText(file), File.Exists(resFile) ? File.ReadAllText(resFile) : null);
            var exists = existingTypes.Contains(item.ItemName);
            if (exists)
            {
                if (!overwriteExisting) { skipped.Add($"udt '{label}' - already exists, overwriteExisting=false"); continue; }
                if (dryRun) { updated.Add($"udt '{label}' (dry run)"); continue; }

                var name = item.ItemName;
                writes.Add(new PendingWrite(item, $"udt '{label}'", docs, false, () => _session.WriteUdtAsync(plcName, name, docs)));
            }
            else
            {
                if (!createMissing) { skipped.Add($"udt '{label}' - does not exist, createMissing=false"); continue; }
                if (dryRun) { created.Add($"udt '{label}' (dry run)"); continue; }

                await EnsureTypeGroupPath(plcName, item.GroupPath, ensuredGroups);
                var (groupPath, typeName) = (item.GroupPath, item.ItemName);
                writes.Add(new PendingWrite(item, $"udt '{label}'", docs, true, () => _session.CreateUdtAsync(plcName, groupPath, typeName, docs)));
            }
        }

        await WriteInDependencyOrder(plcName, writes, created, updated, failed, written, compileBetweenLayers: true);
        return (created, updated, skipped, failed);
    }

    // One UDT or block write (update or create) decided by ImportTypes/ImportBlocks. A plain
    // class rather than a record - see ImportListItem.
    private sealed class PendingWrite
    {
        public PendingWrite(ImportListItem item, string label, List<BlockDocument> docs, bool isCreate, Func<Task<ImportResult>> write)
        {
            Item = item; Label = label; Docs = docs; IsCreate = isCreate; Write = write;
        }
        public ImportListItem Item { get; }
        public string Name => Item.ItemName;
        public string Label { get; }
        public List<BlockDocument> Docs { get; }
        public bool IsCreate { get; }
        public Func<Task<ImportResult>> Write { get; }
    }

    // Writes UDTs/blocks after what they reference: an import that references something not
    // imported yet fails ("Dependent object ... not found") or, for a UDT nesting another UDT,
    // succeeds but silently drops the nested start values unless the nested UDT is also compiled
    // - hence compileBetweenLayers for UDTs. Blocks only need the order (a full round trip of a
    // ~500-block PLC came back identical without compiles between block layers). That gives UDTs in nesting
    // order, and blocks as global DBs/FBs before the FBs that call them and the instance DBs on
    // them. References are read from the source text (see DependencyLayers), so a create that
    // still fails is retried after a compile, as long as a pass creates something.
    private async Task WriteInDependencyOrder(string plcName, List<PendingWrite> writes, List<string> created, List<string> updated, List<string> failed, List<ImportListItem> written, bool compileBetweenLayers)
    {
        var lastError = new Dictionary<PendingWrite, string>();
        var retry = new List<PendingWrite>();
        var layers = DependencyLayers(writes);
        for (var i = 0; i < layers.Count; i++)
        {
            if (i > 0 && compileBetweenLayers) await _session.CompileAsync(plcName);
            foreach (var write in layers[i])
            {
                var result = await write.Write();
                if (result.Success) { (write.IsCreate ? created : updated).Add(write.Label); written.Add(write.Item); }
                else if (write.IsCreate) { retry.Add(write); lastError[write] = string.Join(" | ", result.Messages); }
                else failed.Add($"{write.Label}: {string.Join(" | ", result.Messages)}");
            }
        }

        while (retry.Count > 0)
        {
            await _session.CompileAsync(plcName);
            var stillFailing = new List<PendingWrite>();
            foreach (var write in retry)
            {
                var result = await write.Write();
                if (result.Success) { created.Add(write.Label); written.Add(write.Item); }
                else { stillFailing.Add(write); lastError[write] = string.Join(" | ", result.Messages); }
            }
            if (stillFailing.Count == retry.Count) break;
            retry = stillFailing;
        }
        foreach (var write in retry) failed.Add($"{write.Label}: {lastError[write]}");
    }

    private static readonly Regex IdentifierToken = new("\"([^\"]+)\"|[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

    // Groups writes into layers where each item only references items of earlier layers. A
    // reference is any identifier in the item's source (not its .s7res texts) that names another
    // item in the same set - e.g. "DATA_BLOCK "x" : FB_y", "member : _.UDT_z". A cycle, or a name
    // that happens to match a variable, ends up in the last layer, where the retry in
    // WriteInDependencyOrder still covers it.
    private static List<List<PendingWrite>> DependencyLayers(List<PendingWrite> writes)
    {
        var names = new HashSet<string>(writes.Select(w => w.Name), StringComparer.OrdinalIgnoreCase);
        var dependencies = writes.ToDictionary(w => w, w => IdentifierToken
            .Matches(string.Join("\n", w.Docs.Where(d => !d.FileName.EndsWith(".s7res", StringComparison.OrdinalIgnoreCase)).Select(d => d.Content)))
            .Cast<Match>()
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Value)
            .Where(t => names.Contains(t) && !string.Equals(t, w.Name, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase));

        var layers = new List<List<PendingWrite>>();
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remaining = writes;
        while (remaining.Count > 0)
        {
            var layer = remaining.Where(w => dependencies[w].All(placed.Contains)).ToList();
            if (layer.Count == 0) layer = remaining;
            layers.Add(layer);
            foreach (var w in layer) placed.Add(w.Name);
            remaining = remaining.Except(layer).ToList();
        }
        return layers;
    }

    private async Task<(List<string> Created, List<string> Updated, List<string> Skipped, List<string> Failed)> ImportTagTables(
        string plcName, string plcDir, List<ImportListItem> items, bool createMissing, bool overwriteExisting, bool dryRun, List<ImportListItem> written)
    {
        var created = new List<string>();
        var updated = new List<string>();
        var skipped = new List<string>();
        var failed = new List<string>();

        var existingTables = (await _session.ListTagTablesAsync(plcName)).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ensuredGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            var label = string.IsNullOrEmpty(item.GroupPath) ? item.ItemName : $"{item.GroupPath}/{item.ItemName}";
            var dir = GroupDir(plcDir, "PLC tags", item.GroupPath);
            var file = Path.Combine(dir, Sanitize(item.ItemName) + ".csv");
            if (!File.Exists(file)) { failed.Add($"tag table '{label}': no .csv file found at {file}"); continue; }

            TagSpec[] tags;
            try { tags = ParseTagCsv(File.ReadAllText(file)); }
            catch (FormatException ex) { failed.Add($"tag table '{label}': {ex.Message}"); continue; }

            var exists = existingTables.Contains(item.ItemName);
            if (exists)
            {
                if (!overwriteExisting) { skipped.Add($"tag table '{label}' - already exists, overwriteExisting=false"); continue; }
                if (dryRun) { updated.Add($"tag table '{label}' (dry run, {tags.Length} tag(s))"); continue; }

                var result = await _session.WriteTagTableAsync(plcName, item.ItemName, tags);
                if (result.Success) { updated.Add($"tag table '{label}' ({tags.Length} tag(s))"); written.Add(item); }
                else failed.Add($"tag table '{label}': {string.Join(" | ", result.Messages)}");
            }
            else
            {
                if (!createMissing) { skipped.Add($"tag table '{label}' - does not exist, createMissing=false"); continue; }
                if (dryRun) { created.Add($"tag table '{label}' (dry run, {tags.Length} tag(s))"); continue; }

                await EnsureTagTableGroupPath(plcName, item.GroupPath, ensuredGroups);
                var createResult = await _session.CreateTagTableAsync(plcName, item.GroupPath, item.ItemName);
                if (!createResult.Success) { failed.Add($"tag table '{label}': {createResult.Error}"); continue; }

                var writeResult = await _session.WriteTagTableAsync(plcName, item.ItemName, tags);
                if (writeResult.Success) { created.Add($"tag table '{label}' ({tags.Length} tag(s))"); written.Add(item); }
                else failed.Add($"tag table '{label}': created empty, but writing tags failed: {string.Join(" | ", writeResult.Messages)}");
            }
        }

        return (created, updated, skipped, failed);
    }

    private async Task<(List<string> Created, List<string> Updated, List<string> Skipped, List<string> Failed)> ImportHmiTagTables(
        string hmiName, string hmiDir, List<ImportListItem> items, bool createMissing, bool overwriteExisting, bool dryRun, List<ImportListItem> written)
    {
        var created = new List<string>();
        var updated = new List<string>();
        var skipped = new List<string>();
        var failed = new List<string>();

        var existingTables = (await _session.ListHmiTagTablesAsync(hmiName)).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ensuredGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            var label = string.IsNullOrEmpty(item.GroupPath) ? item.ItemName : $"{item.GroupPath}/{item.ItemName}";
            var dir = GroupDir(hmiDir, "HMI tags", item.GroupPath);
            var file = Path.Combine(dir, Sanitize(item.ItemName) + ".csv");
            if (!File.Exists(file)) { failed.Add($"HMI tag table '{label}': no .csv file found at {file}"); continue; }

            HmiTagSpec[] tags;
            try { tags = ParseHmiTagCsv(File.ReadAllText(file)); }
            catch (FormatException ex) { failed.Add($"HMI tag table '{label}': {ex.Message}"); continue; }

            var exists = existingTables.Contains(item.ItemName);
            if (exists)
            {
                if (!overwriteExisting) { skipped.Add($"HMI tag table '{label}' - already exists, overwriteExisting=false"); continue; }
                if (dryRun) { updated.Add($"HMI tag table '{label}' (dry run, {tags.Length} tag(s))"); continue; }

                var result = await _session.WriteHmiTagTableAsync(hmiName, item.ItemName, tags);
                if (result.Success) { updated.Add($"HMI tag table '{label}' ({tags.Length} tag(s))"); written.Add(item); }
                else failed.Add($"HMI tag table '{label}': {string.Join(" | ", result.Messages)}");
            }
            else
            {
                if (!createMissing) { skipped.Add($"HMI tag table '{label}' - does not exist, createMissing=false"); continue; }
                if (dryRun) { created.Add($"HMI tag table '{label}' (dry run, {tags.Length} tag(s))"); continue; }

                await EnsureHmiTagTableGroupPath(hmiName, item.GroupPath, ensuredGroups);
                var createResult = await _session.CreateHmiTagTableAsync(hmiName, item.GroupPath, item.ItemName);
                if (!createResult.Success) { failed.Add($"HMI tag table '{label}': {createResult.Error}"); continue; }

                var writeResult = await _session.WriteHmiTagTableAsync(hmiName, item.ItemName, tags);
                if (writeResult.Success) { created.Add($"HMI tag table '{label}' ({tags.Length} tag(s))"); written.Add(item); }
                else failed.Add($"HMI tag table '{label}': created empty, but writing tags failed: {string.Join(" | ", writeResult.Messages)}");
            }
        }

        return (created, updated, skipped, failed);
    }

    // Alarms/alarm classes are always upsert-by-name (see write_hmi_alarm/write_hmi_alarm_class),
    // so createMissing/overwriteExisting are applied per-row here rather than per-CSV-file, unlike
    // every other section where one list item is one write call.
    private async Task<(List<string> Created, List<string> Updated, List<string> Skipped, List<string> Failed)> ImportHmiAlarms(
        string hmiName, string hmiDir, List<ImportListItem> items, bool createMissing, bool overwriteExisting, bool dryRun, List<ImportListItem> written)
    {
        var created = new List<string>();
        var updated = new List<string>();
        var skipped = new List<string>();
        var failed = new List<string>();
        var dir = Path.Combine(hmiDir, "HMI alarms");

        foreach (var item in items)
        {
            // A file counts as written only if every row was - see write_source_tree's refresh.
            var failedBefore = failed.Count;
            var itemName = item.Kind.Substring("HMI ".Length); // "DiscreteAlarms" / "AnalogAlarms" / "AlarmClasses"
            var file = Path.Combine(dir, itemName + ".csv");
            if (!File.Exists(file)) { failed.Add($"HMI {itemName}: no .csv file found at {file}"); continue; }
            var rows = ParseCsv(File.ReadAllText(file));

            if (itemName == "AlarmClasses")
            {
                var existing = (await _session.ListHmiAlarmClassesAsync(hmiName)).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var row in rows)
                {
                    var name = row["Name"];
                    var exists = existing.Contains(name);
                    if (exists && !overwriteExisting) { skipped.Add($"HMI alarm class '{name}' - already exists, overwriteExisting=false"); continue; }
                    if (!exists && !createMissing) { skipped.Add($"HMI alarm class '{name}' - does not exist, createMissing=false"); continue; }
                    if (dryRun) { (exists ? updated : created).Add($"HMI alarm class '{name}' (dry run)"); continue; }

                    var priority = int.TryParse(GetField(row, "Priority"), out var p) ? (int?)p : null;
                    var result = await _session.WriteHmiAlarmClassAsync(hmiName, new HmiAlarmClassSpec(name, priority, NullIfEmpty(GetField(row, "Log"))));
                    if (result.Success) (exists ? updated : created).Add($"HMI alarm class '{name}'");
                    else failed.Add($"HMI alarm class '{name}': {result.Error}");
                }
                if (!dryRun && failed.Count == failedBefore) written.Add(item);
                continue;
            }

            var type = itemName == "DiscreteAlarms" ? "Discrete" : "Analog";
            var existingAlarms = (await _session.ListHmiAlarmsAsync(hmiName, type)).Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                var name = row["Name"];
                var exists = existingAlarms.Contains(name);
                if (exists && !overwriteExisting) { skipped.Add($"HMI alarm '{name}' - already exists, overwriteExisting=false"); continue; }
                if (!exists && !createMissing) { skipped.Add($"HMI alarm '{name}' - does not exist, createMissing=false"); continue; }
                if (dryRun) { (exists ? updated : created).Add($"HMI alarm '{name}' (dry run)"); continue; }

                // TriggerAddress is a read-only computed field (see write_hmi_alarm) - it's
                // exported for readability but never written back; RaisedStateTag is what
                // actually (re)binds the trigger and recomputes it.
                var spec = new HmiAlarmSpec(
                    type, name,
                    NullIfEmpty(GetField(row, "AlarmClass")), NullIfEmpty(GetField(row, "EventText")), NullIfEmpty(GetField(row, "InfoText")),
                    null,
                    NullIfEmpty(GetField(row, "Condition")), NullIfEmpty(GetField(row, "ConditionValue")), NullIfEmpty(GetField(row, "RaisedStateTag")),
                    NullIfEmpty(GetField(row, "AuditClass")), NullIfEmpty(GetField(row, "Area")), NullIfEmpty(GetField(row, "Origin")));
                var result = await _session.WriteHmiAlarmAsync(hmiName, spec);
                if (result.Success) (exists ? updated : created).Add($"HMI alarm '{name}'");
                else failed.Add($"HMI alarm '{name}': {result.Error}");
            }
            if (!dryRun && failed.Count == failedBefore) written.Add(item);
        }

        return (created, updated, skipped, failed);
    }

    // Locates the on-disk source file(s) read_source_tree wrote for one block and packages them as the
    // BlockDocument(s) write_plc_block/create_plc_block expect - mirroring ExportBlocks' own
    // extension priority (GRAPH's '.graph.il', whole-block STL's '.awl', else the normal
    // '.s7dcl'[+'.s7res'] pair). '.interface.txt' and '.stl-networks.xml' are read-only companions
    // (see read_plc_block) and are never picked up here.
    private static (List<BlockDocument>? Docs, string? Error) ReadBlockDocuments(string dir, string blockName)
    {
        if (!Directory.Exists(dir)) return (null, $"directory not found: {dir}");
        var sanitizedName = Sanitize(blockName);

        var graphFile = Path.Combine(dir, sanitizedName + ".graph.il");
        if (File.Exists(graphFile))
            return (new List<BlockDocument> { new($"{blockName}.graph.il", File.ReadAllText(graphFile)) }, null);

        var awlFile = Path.Combine(dir, sanitizedName + ".awl");
        if (File.Exists(awlFile))
            return (new List<BlockDocument> { new($"{blockName}.awl", File.ReadAllText(awlFile)) }, null);

        var dclFile = Path.Combine(dir, sanitizedName + ".s7dcl");
        if (!File.Exists(dclFile))
        {
            var txtFile = Path.Combine(dir, sanitizedName + ".txt");
            if (File.Exists(txtFile))
                return (new List<BlockDocument> { new($"{blockName}.s7dcl", File.ReadAllText(txtFile)) }, null);
            return (null, $"no source file found in '{dir}' (expected {sanitizedName}.s7dcl, .graph.il, or .awl)");
        }

        var docs = new List<BlockDocument> { new($"{blockName}.s7dcl", File.ReadAllText(dclFile)) };
        var resFile = Path.Combine(dir, sanitizedName + ".s7res");
        if (File.Exists(resFile)) docs.Add(new BlockDocument($"{blockName}.s7res", File.ReadAllText(resFile)));
        return (docs, null);
    }

    // Best-effort recursive group creation so create_plc_block/create_plc_udt/create_plc_tag_table/
    // create_hmi_tag_table's "group must already exist" requirement doesn't block restoring a full
    // tree into an empty/new PLC or HMI. Failures (including "already exists") are ignored here - a
    // genuine problem still surfaces from the subsequent create call itself right after this runs.
    // ensured: group paths already handled in this import, so each folder is created (or found to
    // exist - a create that throws, which is slow over Openness) once rather than once per item.
    private static async Task EnsureGroupPath(string groupPath, Func<string, string, Task> createGroup, ISet<string> ensured)
    {
        if (string.IsNullOrEmpty(groupPath)) return;
        var current = "";
        foreach (var segment in groupPath.Split('/'))
        {
            var path = string.IsNullOrEmpty(current) ? segment : $"{current}/{segment}";
            if (ensured.Add(path)) await createGroup(current, segment);
            current = path;
        }
    }

    private Task EnsureBlockGroupPath(string plcName, string groupPath, ISet<string> ensured) =>
        EnsureGroupPath(groupPath, (parent, segment) => _session.CreateBlockGroupAsync(plcName, parent, segment), ensured);

    private Task EnsureTypeGroupPath(string plcName, string groupPath, ISet<string> ensured) =>
        EnsureGroupPath(groupPath, (parent, segment) => _session.CreateTypeGroupAsync(plcName, parent, segment), ensured);

    private Task EnsureTagTableGroupPath(string plcName, string groupPath, ISet<string> ensured) =>
        EnsureGroupPath(groupPath, (parent, segment) => _session.CreateTagTableGroupAsync(plcName, parent, segment), ensured);

    private Task EnsureHmiTagTableGroupPath(string hmiName, string groupPath, ISet<string> ensured) =>
        EnsureGroupPath(groupPath, (parent, segment) => _session.CreateHmiTagTableGroupAsync(hmiName, parent, segment), ensured);

    // Parses read_source_tree's _export_summary.txt format (or a hand-trimmed copy/inline excerpt of
    // it) back into importable items - see write_source_tree. Unrecognized lines (headers, blank
    // lines, the trailing 'Done: ...' line) are silently skipped rather than treated as errors, so
    // callers don't need to strip them out first.
    private static List<ImportListItem> ParseSourceTreeList(string text)
    {
        var items = new List<ImportListItem>();
        string? currentDevice = null;

        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0) continue;

            var headerMatch = Regex.Match(trimmed, @"^===\s*(.+?)\s*===$");
            if (headerMatch.Success)
            {
                var name = headerMatch.Groups[1].Value;
                currentDevice = name.EndsWith(" (HMI)") ? name[..^" (HMI)".Length] : name;
                continue;
            }

            var statusMatch = Regex.Match(trimmed, @"^(OK|FAILED|STALE)\s+(.*)$");
            if (!statusMatch.Success || currentDevice == null) continue;

            var ok = statusMatch.Groups[1].Value == "OK";
            var rest = statusMatch.Groups[2].Value;

            if (rest.StartsWith("HMI DiscreteAlarms") || rest.StartsWith("HMI AnalogAlarms") || rest.StartsWith("HMI AlarmClasses"))
            {
                var kind = rest.StartsWith("HMI DiscreteAlarms") ? "HMI DiscreteAlarms" : rest.StartsWith("HMI AnalogAlarms") ? "HMI AnalogAlarms" : "HMI AlarmClasses";
                items.Add(new ImportListItem(currentDevice, kind, "", "", ok));
                continue;
            }

            var itemMatch = Regex.Match(rest, @"^(?<kind>.+?)\s+'(?<label>[^']*)'");
            if (!itemMatch.Success) continue;

            var label = itemMatch.Groups["label"].Value;
            var slash = label.LastIndexOf('/');
            var groupPath = slash >= 0 ? label[..slash] : "";
            var itemName = slash >= 0 ? label[(slash + 1)..] : label;

            items.Add(new ImportListItem(currentDevice, itemMatch.Groups["kind"].Value, groupPath, itemName, ok));
        }

        return items;
    }

    // Reverse of FormatCsv/CsvField: RFC 4180 quoting, CRLF or LF line endings, header row used as
    // each returned row's keys.
    private static List<Dictionary<string, string>> ParseCsv(string text)
    {
        var rows = ParseCsvRows(text);
        if (rows.Count == 0) return new List<Dictionary<string, string>>();

        var headers = rows[0];
        var result = new List<Dictionary<string, string>>();
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var dict = new Dictionary<string, string>();
            for (var c = 0; c < headers.Count; c++)
                dict[headers[c]] = c < row.Count ? row[c] : "";
            result.Add(dict);
        }
        return result;
    }

    private static List<List<string>> ParseCsvRows(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                    inQuotes = false; i++; continue;
                }
                field.Append(c); i++; continue;
            }

            switch (c)
            {
                case '"': inQuotes = true; i++; continue;
                case ',': row.Add(field.ToString()); field.Clear(); i++; continue;
                case '\r': i++; continue;
                case '\n':
                    row.Add(field.ToString()); field.Clear();
                    rows.Add(row); row = new List<string>();
                    i++; continue;
                default: field.Append(c); i++; continue;
            }
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows.Where(r => r.Count > 1 || r[0].Length > 0).ToList();
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    // net48's Dictionary<TKey, TValue> has no GetValueOrDefault.
    private static string? GetField(Dictionary<string, string> row, string key) => row.TryGetValue(key, out var v) ? v : null;

    // One item as exported from TIA Portal: its files (root-relative path, content, whether it is
    // written with a UTF-8 BOM), or why it couldn't be exported. HMI alarms are three fixed items
    // per HMI with Kind 'HMI DiscreteAlarms'/'HMI AnalogAlarms'/'HMI AlarmClasses' and no name -
    // alarms and alarm classes have no folder/group concept in Openness.
    private sealed class ExportedItem
    {
        public ExportedItem(string device, string kind, string groupPath, string name, List<(string Path, string Content, bool Bom)> files, string? error)
        {
            Device = device; Kind = kind; GroupPath = groupPath; Name = name; Files = files; Error = error;
        }
        public string Device { get; }
        public string Kind { get; }
        public string GroupPath { get; }
        public string Name { get; }
        public List<(string Path, string Content, bool Bom)> Files { get; }
        public string? Error { get; }
        public bool Ok => Error == null;
    }

    private static readonly string[] HmiAlarmKinds = { "HMI DiscreteAlarms", "HMI AnalogAlarms", "HMI AlarmClasses" };

    // Exports every item include (device, kind, group path, name) selects - all of them when it's
    // null - from every PLC and HMI, or only deviceName's. Also returns the devices it went
    // through, so a caller can tell which manifest items it would have seen if they still existed.
    private async Task<(List<ExportedItem> Items, List<string> Devices)> ExportItems(Func<string, string, string, string, bool>? include, string? deviceName)
    {
        var items = new List<ExportedItem>();
        var devices = new List<string>();
        bool Includes(string device, string kind, string groupPath, string name) => include == null || include(device, kind, groupPath, name);
        bool IsSelectedDevice(string device) => deviceName == null || string.Equals(device, deviceName, StringComparison.OrdinalIgnoreCase);

        foreach (var plc in (await _session.ListDevicesAsync()).Select(d => d.PlcSoftwareName).OfType<string>().Where(IsSelectedDevice))
        {
            devices.Add(plc);
            foreach (var (b, result) in await _session.ReadAllBlocksAsync(plc, (g, n) => Includes(plc, "block", g, n)))
                items.Add(FromDocuments(plc, "block", b.GroupPath, b.Name, result));
            foreach (var (t, result) in await _session.ReadAllTagTablesAsync(plc, (g, n) => Includes(plc, "tag table", g, n)))
                items.Add(FromCsv(plc, "tag table", t.GroupPath, t.Name, result.Success ? FormatTagTable(result.Tags) : null, result.Error));
            foreach (var (t, result) in await _session.ReadAllUdtsAsync(plc, (g, n) => Includes(plc, "udt", g, n)))
                items.Add(FromDocuments(plc, "udt", t.GroupPath, t.Name, result));
        }

        foreach (var hmi in (await _session.ListHmiDevicesAsync()).Select(h => h.HmiSoftwareName).Where(IsSelectedDevice))
        {
            devices.Add(hmi);
            foreach (var table in (await _session.ListHmiTagTablesAsync(hmi)).Where(t => Includes(hmi, "HMI tag table", t.GroupPath, t.Name)))
            {
                try
                {
                    var result = await _session.ReadHmiTagTableAsync(hmi, table.Name);
                    items.Add(FromCsv(hmi, "HMI tag table", table.GroupPath, table.Name, result.Success ? FormatHmiTagTable(result.Tags) : null, result.Error));
                }
                catch (Exception ex)
                {
                    items.Add(FromCsv(hmi, "HMI tag table", table.GroupPath, table.Name, null, ex.Message));
                }
            }

            foreach (var kind in HmiAlarmKinds.Where(k => Includes(hmi, k, "", "")))
            {
                try
                {
                    var csv = kind == "HMI AlarmClasses"
                        ? FormatHmiAlarmClasses(await _session.ListHmiAlarmClassesAsync(hmi))
                        : FormatHmiAlarms(await _session.ListHmiAlarmsAsync(hmi, kind == "HMI DiscreteAlarms" ? "Discrete" : "Analog"));
                    items.Add(FromCsv(hmi, kind, "", "", csv, null));
                }
                catch (Exception ex)
                {
                    items.Add(FromCsv(hmi, kind, "", "", null, ex.Message));
                }
            }
        }

        return (items, devices);
    }

    private static ExportedItem FromDocuments(string device, string kind, string groupPath, string name, ExportResult result)
    {
        var dir = ItemDir(device, kind, groupPath);
        var files = result.Documents.Select(d => ($"{dir}/{Sanitize(EnsureExtension(d.FileName))}", d.Content, false)).ToList();
        return new ExportedItem(device, kind, groupPath, name, files, result.Success ? null : result.Error ?? "export failed");
    }

    // CSV gets a UTF-8 BOM so Excel auto-detects the encoding and opening the file directly
    // (double-click) renders non-ASCII comment/tag text correctly instead of mangling it.
    private static ExportedItem FromCsv(string device, string kind, string groupPath, string name, string? csv, string? error)
    {
        var files = csv == null
            ? new List<(string, string, bool)>()
            : new List<(string, string, bool)> { ($"{ItemDir(device, kind, groupPath)}/{Sanitize(ItemStem(kind, name))}.csv", csv, true) };
        return new ExportedItem(device, kind, groupPath, name, files, csv == null ? error ?? "export failed" : null);
    }

    private static string SectionOf(string kind) => kind switch
    {
        "block" => "Program blocks",
        "udt" => "PLC data types",
        "tag table" => "PLC tags",
        "HMI tag table" => "HMI tags",
        _ => "HMI alarms",
    };

    // The file name (without extension) an item's files start with: its name, or for an HMI
    // alarms item the fixed 'DiscreteAlarms'/'AnalogAlarms'/'AlarmClasses'.
    private static string ItemStem(string kind, string name) =>
        HmiAlarmKinds.Contains(kind) ? kind.Substring("HMI ".Length) : name;

    // Root-relative, '/'-separated folder of an item: <device>/<section>/<group path>.
    private static string ItemDir(string device, string kind, string groupPath)
    {
        var segments = new List<string> { Sanitize(device), SectionOf(kind) };
        if (!string.IsNullOrEmpty(groupPath))
            segments.AddRange(groupPath.Split('/').Select(Sanitize));
        return string.Join("/", segments);
    }

    // Writes exported items to disk and records them in the manifest. A failed item keeps no file
    // that looks current: what an earlier read left is renamed '.stale' (see MarkStale).
    private static void WriteExport(string root, SourceTreeManifest manifest, IEnumerable<ExportedItem> items)
    {
        foreach (var item in items)
        {
            var dir = Path.Combine(root, ItemDir(item.Device, item.Kind, item.GroupPath));
            var stem = ItemStem(item.Kind, item.Name);
            var previous = manifest.Find(item.Device, item.Kind, item.Name);
            var entry = new SourceTreeItem { Device = item.Device, Kind = item.Kind, GroupPath = item.GroupPath, Name = item.Name };
            if (!item.Ok)
            {
                entry.Ok = false;
                entry.Error = item.Error;
                entry.Stale = MarkStale(dir, stem);
                manifest.Set(entry);
                continue;
            }

            // Files of an earlier read this one no longer produces - e.g. a .s7res whose last
            // comment was removed, or everything at the old place of an item moved to another group.
            if (previous != null)
            {
                foreach (var old in previous.Files.Keys.Where(k => !item.Files.Any(f => string.Equals(f.Path, k, StringComparison.OrdinalIgnoreCase))))
                    DeleteFile(Path.Combine(root, old));
            }

            Directory.CreateDirectory(dir);
            ClearStale(dir, stem);
            foreach (var file in item.Files)
            {
                File.WriteAllText(Path.Combine(root, file.Path), file.Content, new UTF8Encoding(file.Bom));
                entry.Files[file.Path] = SourceTreeManifest.Hash(file.Content);
            }
            manifest.Set(entry);
        }
    }

    // An item a read didn't find in the project any more (deleted or renamed in TIA Portal):
    // its files go too, or write_source_tree would later create it again as a new item.
    private static void RemoveFromTree(string root, SourceTreeManifest manifest, SourceTreeItem item)
    {
        foreach (var file in item.Files.Keys) DeleteFile(Path.Combine(root, file));
        manifest.Items.Remove(item);
    }

    private static void DeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort - a leftover file is reported by the next write_source_tree */ }
    }

    private static string Label(string groupPath, string name) =>
        string.IsNullOrEmpty(groupPath) ? name : $"{groupPath}/{name}";

    // How an item appears in _export_summary.txt and tool results - the format ParseSourceTreeList reads back.
    private static string Describe(string kind, string groupPath, string name) =>
        HmiAlarmKinds.Contains(kind) ? kind : $"{kind} '{Label(groupPath, name)}'";

    private static string Describe(SourceTreeItem item) => Describe(item.Kind, item.GroupPath, item.Name);

    private static readonly string[] KindOrder = { "block", "tag table", "udt", "HMI tag table", "HMI DiscreteAlarms", "HMI AnalogAlarms", "HMI AlarmClasses" };

    // _export_summary.txt: every item in the tree, so it stays the full list for write_source_tree
    // after a read of only part of the project.
    private static string FormatSummary(string root, SourceTreeManifest manifest)
    {
        var summary = new StringBuilder();
        summary.AppendLine($"Source tree - last read {manifest.LastRead:yyyy-MM-dd HH:mm:ss}");
        summary.AppendLine($"Project: {manifest.Project}");
        summary.AppendLine($"Target: {root}");
        summary.AppendLine();

        foreach (var device in manifest.Items.GroupBy(i => i.Device, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            summary.AppendLine(device.All(i => i.Kind.StartsWith("HMI")) ? $"=== {device.Key} (HMI) ===" : $"=== {device.Key} ===");
            foreach (var item in device.OrderBy(i => Array.IndexOf(KindOrder, i.Kind)).ThenBy(i => Label(i.GroupPath, i.Name), StringComparer.OrdinalIgnoreCase))
            {
                if (item.Ok) { summary.AppendLine($"  OK     {Describe(item)}"); continue; }
                summary.AppendLine($"  FAILED {Describe(item)}: {item.Error}");
                if (item.Stale) summary.AppendLine($"  STALE  {Describe(item)} - previous export left on disk with a '.stale' suffix, does not reflect current state");
            }
            summary.AppendLine();
        }

        summary.AppendLine($"Done: {manifest.Items.Count(i => i.Ok)} exported, {manifest.Items.Count(i => !i.Ok)} failed, {manifest.Items.Count(i => i.Stale)} marked stale.");
        return summary.ToString();
    }

    // read_source_tree's items: 'Name', 'group/path/Name', or 'group/path/' for everything below a
    // group, case-insensitive. HMI alarms are 'DiscreteAlarms', 'AnalogAlarms', 'AlarmClasses', or
    // 'HMI alarms/' for all three.
    private static bool Selects(IReadOnlyList<string> selection, string kind, string groupPath, string name)
    {
        if (HmiAlarmKinds.Contains(kind)) (groupPath, name) = ("HMI alarms", ItemStem(kind, name));
        var label = Label(groupPath, name);
        foreach (var raw in selection)
        {
            var entry = raw.Trim().Replace('\\', '/');
            if (entry.EndsWith("/"))
            {
                var group = entry.TrimEnd('/');
                if (string.Equals(groupPath, group, StringComparison.OrdinalIgnoreCase) || groupPath.StartsWith(group + "/", StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (string.Equals(entry, entry.Contains("/") ? label : name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string FormatHmiAlarms(IReadOnlyList<HmiAlarmInfo> alarms) =>
        FormatCsv(
            new[] { "Type", "Name", "AlarmClass", "EventText", "InfoText", "TriggerAddress", "Condition", "ConditionValue", "RaisedStateTag", "AuditClass", "Area", "Origin" },
            alarms.Select(a => new[]
            {
                a.Type, a.Name, a.AlarmClass ?? "", a.EventText ?? "", a.InfoText ?? "", a.TriggerAddress ?? "",
                a.Condition ?? "", a.ConditionValue ?? "", a.RaisedStateTag ?? "", a.AuditClass ?? "", a.Area ?? "", a.Origin ?? ""
            }));

    private static string FormatHmiAlarmClasses(IReadOnlyList<HmiAlarmClassInfo> classes) =>
        FormatCsv(
            new[] { "Name", "Priority", "Log", "Id", "IsSystem" },
            classes.Select(c => new[] { c.Name, c.Priority.ToString(), c.Log ?? "", c.Id.ToString(), c.IsSystem.ToString() }));

    // RFC 4180 CSV, CRLF line endings and a header row - what Excel expects when opening a .csv
    // directly rather than going through its text-import wizard.
    private static string FormatTagTable(IReadOnlyList<TagInfo> tags) =>
        FormatCsv(
            new[] { "Name", "DataType", "LogicalAddress", "Comment", "ExternalAccessible", "ExternalVisible", "ExternalWritable" },
            tags.Select(t => new[] { t.Name, t.DataType, t.LogicalAddress, t.Comment ?? "",
                t.ExternalAccessible.ToString(), t.ExternalVisible.ToString(), t.ExternalWritable.ToString() }));

    // Inverse of FormatTagTable. Missing columns and empty cells map to null ("leave unchanged"),
    // so CSVs from before the flag columns existed still import. Throws FormatException on bad input.
    private static TagSpec[] ParseTagCsv(string csv)
    {
        var rows = ParseCsv(csv);
        var tags = new List<TagSpec>();
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var line = i + 2; // 1-based, after the header row
            var name = GetField(r, "Name");
            var dataType = GetField(r, "DataType");
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(dataType)) continue; // blank line
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(dataType))
                throw new FormatException($"CSV line {line}: Name and DataType are required.");

            tags.Add(new TagSpec(name!, dataType!,
                NullIfEmpty(GetField(r, "LogicalAddress")), NullIfEmpty(GetField(r, "Comment")),
                ParseCsvBool(r, "ExternalAccessible", line), ParseCsvBool(r, "ExternalVisible", line), ParseCsvBool(r, "ExternalWritable", line)));
        }
        return tags.ToArray();
    }

    private static bool? ParseCsvBool(Dictionary<string, string> row, string column, int line)
    {
        var value = GetField(row, column)?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (value == "1" || value!.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        throw new FormatException($"CSV line {line}: {column} must be True/False/1/0, got '{value}'.");
    }

    private static string FormatHmiTagTable(IReadOnlyList<HmiTagInfo> tags) =>
        FormatCsv(
            new[] { "Name", "DataType", "Address", "Connection", "PlcName", "PlcTag", "Comment", "AcquisitionCycle" },
            tags.Select(t => new[] { t.Name, t.DataType, t.Address ?? "", t.Connection ?? "", t.PlcName ?? "", t.PlcTag ?? "", t.Comment ?? "", t.AcquisitionCycle ?? "" }));

    // Inverse of FormatHmiTagTable, same rules as ParseTagCsv.
    private static HmiTagSpec[] ParseHmiTagCsv(string csv)
    {
        var rows = ParseCsv(csv);
        var tags = new List<HmiTagSpec>();
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var line = i + 2; // 1-based, after the header row
            var name = GetField(r, "Name");
            var dataType = GetField(r, "DataType");
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(dataType)) continue; // blank line
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(dataType))
                throw new FormatException($"CSV line {line}: Name and DataType are required.");

            tags.Add(new HmiTagSpec(name!, dataType!,
                NullIfEmpty(GetField(r, "Address")), NullIfEmpty(GetField(r, "Connection")),
                NullIfEmpty(GetField(r, "PlcName")), NullIfEmpty(GetField(r, "PlcTag")), NullIfEmpty(GetField(r, "Comment")),
                NullIfEmpty(GetField(r, "AcquisitionCycle"))));
        }
        return tags.ToArray();
    }

    private static string FormatCsv(string[] headers, IEnumerable<string[]> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", headers.Select(CsvField))).Append("\r\n");
        foreach (var row in rows)
        {
            sb.Append(string.Join(",", row.Select(CsvField))).Append("\r\n");
        }
        return sb.ToString();
    }

    private static string CsvField(string? value)
    {
        value ??= "";
        return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    private static string GroupDir(string plcDir, string sectionName, string groupPath)
    {
        var segments = new List<string> { plcDir, sectionName };
        if (!string.IsNullOrEmpty(groupPath))
            segments.AddRange(groupPath.Split('/').Select(Sanitize));
        return Path.Combine(segments.ToArray());
    }

    private static string EnsureExtension(string fileName) =>
        string.IsNullOrEmpty(Path.GetExtension(fileName)) ? fileName + ".txt" : fileName;

    // Renames leftover files from a previous successful export of this item to "<name>.stale" so
    // a failed re-export (e.g. the block went inconsistent) can't leave a same-named file on disk
    // that looks current but isn't - read_source_tree itself never deletes on failure, so without this
    // an old export is otherwise indistinguishable from a fresh one. Returns true if anything was
    // marked.
    private static bool MarkStale(string dir, string itemName)
    {
        if (!Directory.Exists(dir)) return false;
        var marked = false;
        foreach (var file in Directory.GetFiles(dir, Sanitize(itemName) + ".*"))
        {
            if (file.EndsWith(".stale", StringComparison.OrdinalIgnoreCase)) continue;
            var staleName = file + ".stale";
            if (File.Exists(staleName)) File.Delete(staleName);
            File.Move(file, staleName);
            marked = true;
        }
        return marked;
    }

    // Undoes MarkStale once this item exports successfully again - called right before writing
    // the fresh files.
    private static void ClearStale(string dir, string itemName)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir, Sanitize(itemName) + ".*.stale"))
            File.Delete(file);
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
