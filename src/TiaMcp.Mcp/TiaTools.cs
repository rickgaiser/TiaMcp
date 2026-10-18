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
using static TiaMcp.Mcp.SourceTree;

namespace TiaMcp.Mcp;

[McpServerToolType]
public sealed partial class TiaTools
{
    private readonly TiaSession _session;

    // Notes of the last ExportItems for the read result (e.g. graphic lists that are skipped).
    private readonly List<string> _exportNotes = new();

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
    [Description("Attach to a running TIA Portal instance. With no arguments, attaches to the single open project or Multiuser local session. If more than one TIA Portal instance is running, this returns an error listing their process ids instead of guessing or attaching to any of them (attaching is what triggers Openness's 'allow connection' dialog, so probing every instance would prompt all of them) - call list_tia_instances first, then call this again with processId set to the one you want. Pass projectPath to open a specific .apXX project or .alsXX local session, or to match that session if already open, in the targeted instance. Call this again if a PLC/device was renamed in the GUI or a tool starts reporting an unknown plcName - the PLC name index is only built at connect time.")]
    public Task<string> Connect(
        [Description("Optional full path to a .apXX project file or .alsXX Multiuser local session to open. Omit to use whatever project or local session is already open in TIA Portal.")] string? projectPath = null,
        [Description("Optional process id (PID) of the specific running TIA Portal instance to attach to, from list_tia_instances. Required when more than one TIA Portal instance is running; omit when only one is running.")] int? processId = null) => Safe(async () =>
    {
        var result = await _session.ConnectAsync(projectPath, processId);
        if (!result.Success)
        {
            return $"Connect failed: {result.Error}";
        }
        return result.ProjectPath == null
            ? $"Connected to project '{result.ProjectName}'."
            : $"Connected to project '{result.ProjectName}' at {result.ProjectPath}.";
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
    [Description("Save the currently connected project to a new folder, like TIA Portal's own 'Save project as' - creates a copy at the target location and continues working on that copy. Use this before risky changes if you want an on-disk checkpoint distinct from the original project. Not supported for Multiuser local sessions.")]
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
    [Description("Save the currently connected project in place (equivalent to Ctrl+S in the TIA Portal GUI). Block/tag/group writes made through this server remain in the open session until this is called - call it after a batch of changes you want persisted to disk. For a Multiuser local session, this saves locally only and does not commit changes to the project server.")]
    public Task<string> SaveProject() => Safe(async () =>
    {
        var result = await _session.SaveProjectAsync();
        return result.Success ? "Saved." : $"Save failed: {result.Error}";
    });

    [McpServerTool(Name = "close_project")]
    [Description("Close the currently connected project (equivalent to closing it in the TIA Portal GUI). Save first to preserve unsaved edits; this does not save automatically. Use before tia_connect with a different projectPath if TIA refuses to open a second project. For a Multiuser local session, this closes the session without committing to the project server; it is not just an MCP disconnect.")]
    public Task<string> CloseProject() => Safe(async () =>
    {
        var result = await _session.CloseProjectAsync();
        if (result.Success) return "Closed.";
        return _session.IsConnected ? $"Close failed: {result.Error}" : result.Error!;
    });

    [McpServerTool(Name = "project_status")]
    [Description("Report the connected project's save state: whether it has unsaved changes, and who/when it was last saved. For a Multiuser local session, the path shown is the inner .amcXX project file, not the outer .alsXX session file.")]
    public Task<string> ProjectStatus() => Safe(async () =>
    {
        var s = await _session.GetProjectStatusAsync();
        return $"Project '{s.Name}' at {s.Path}\n" +
               $"Unsaved changes: {(s.IsModified ? "YES" : "no")}\n" +
               $"Author: {s.Author}\n" +
               $"Last modified: {s.LastModified:u} by {s.LastModifiedBy}\n" +
               $"Version: {s.Version}";
    });

    [McpServerTool(Name = "delete_plc_udt")]
    [Description("Delete a UDT (PLC data type) by name. Fails with TIA Portal's own error if the UDT is still used as a member type by another UDT or block interface. This is destructive - confirm with the user before calling.")]
    public Task<string> DeleteUdt(
        [Description("PLC software name, from list_project")] string plcName,
        [Description("UDT name to delete, from list_project")] string typeName) => Safe(async () =>
    {
        var result = await _session.DeleteUdtAsync(plcName, typeName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "rename_plc_udt")]
    [Description("Rename an existing UDT (PLC data type) in place. Fails if a UDT with the new name already exists (names are unique per-PLC). Does not auto-compile - call compile_plc afterward, since a UDT rename can affect every block that references it.")]
    public Task<string> RenameUdt(
        [Description("PLC software name, from list_project")] string plcName,
        [Description("UDT name to rename, from list_project")] string typeName,
        [Description("New UDT name")] string newName) => Safe(async () =>
    {
        var result = await _session.RenameUdtAsync(plcName, typeName, newName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "create_plc_udt_group")]
    [Description("Create a new UDT (PLC data type) group (folder) under a given parent group path.")]
    public Task<string> CreateUdtGroup(
        [Description("PLC software name, from list_project")] string plcName,
        [Description("Parent group path, e.g. 'Types'; empty string for the root")] string parentGroupPath,
        [Description("Name of the new group")] string groupName) => Safe(async () =>
    {
        var result = await _session.CreateTypeGroupAsync(plcName, parentGroupPath, groupName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "delete_plc_udt_group")]
    [Description("Delete a UDT (PLC data type) group (folder) and everything in it, including nested UDTs and subgroups. This is destructive - confirm with the user before calling.")]
    public Task<string> DeleteUdtGroup(
        [Description("PLC software name, from list_project")] string plcName,
        [Description("Full group path to delete, e.g. 'Types/Subgroup'")] string groupPath) => Safe(async () =>
    {
        var result = await _session.DeleteTypeGroupAsync(plcName, groupPath);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "rename_plc_udt_group")]
    [Description("Rename an existing UDT (PLC data type) group (folder) in place, without moving it to a different parent.")]
    public Task<string> RenameUdtGroup(
        [Description("PLC software name, from list_project")] string plcName,
        [Description("Full group path to rename, e.g. 'Types/Subgroup'")] string groupPath,
        [Description("New group name")] string newName) => Safe(async () =>
    {
        var result = await _session.RenameTypeGroupAsync(plcName, groupPath, newName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "create_plc_instance_db")]
    [Description("Create an instance-DB bound to a specific FB type (e.g. instantiate a Typical FB from a library for one piece of equipment). Unlike adding its .s7dcl to the source tree, this needs no source text. Does not auto-compile - call compile_plc afterward.")]
    public Task<string> CreateInstanceDb(
        [Description("PLC software name, from list_project")] string plcName,
        [Description("Group path to create the DB in, e.g. 'Instances'; empty string for the root")] string groupPath,
        [Description("New instance-DB name")] string dbName,
        [Description("Name of the FB type to instantiate (must already exist in this PLC, e.g. a library FB from list_project)")] string instanceOfFbName) => Safe(async () =>
    {
        var result = await _session.CreateInstanceDbAsync(plcName, groupPath, dbName, instanceOfFbName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "create_plc_group")]
    [Description("Create a new block group (folder) under a given parent group path.")]
    public Task<string> CreateGroup(
        [Description("PLC software name, from list_project")] string plcName,
        [Description("Parent group path, e.g. 'Group1'; empty string for the root")] string parentGroupPath,
        [Description("Name of the new group")] string groupName) => Safe(async () =>
    {
        var result = await _session.CreateBlockGroupAsync(plcName, parentGroupPath, groupName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "delete_plc_group")]
    [Description("Delete a block group (folder) and everything in it, including nested blocks and subgroups. This is destructive - confirm with the user before calling.")]
    public Task<string> DeleteGroup(
        [Description("PLC software name, from list_project")] string plcName,
        [Description("Full group path to delete, e.g. 'Group1/Subgroup'")] string groupPath) => Safe(async () =>
    {
        var result = await _session.DeleteBlockGroupAsync(plcName, groupPath);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "rename_plc_group")]
    [Description("Rename an existing block group (folder) in place, without moving it to a different parent.")]
    public Task<string> RenamePlcGroup(
        [Description("PLC software name, from list_project")] string plcName,
        [Description("Full group path to rename, e.g. 'Group1/Subgroup'")] string groupPath,
        [Description("New group name")] string newName) => Safe(async () =>
    {
        var result = await _session.RenamePlcGroupAsync(plcName, groupPath, newName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "delete_plc_block")]
    [Description("Delete a single block (any language, including a GRAPH block or an instance-DB) by name, without touching its containing group or sibling blocks. Use this to recover a block that write_source_tree can't write (e.g. a GRAPH block stuck INCONSISTENT) by deleting it and writing it again from the source tree. To rename a block instead, use rename_plc_block. An instance-DB still bound to an FB/FC must be deleted before the FB/FC itself - deleting the type block first fails with TIA's own error naming the dependency. This is destructive - confirm with the user before calling.")]
    public Task<string> DeleteBlock(
        [Description("PLC software name, from list_project")] string plcName,
        [Description("Block name to delete, from list_project")] string blockName) => Safe(async () =>
    {
        var result = await _session.DeleteBlockAsync(plcName, blockName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "rename_plc_block")]
    [Description("Rename an existing block (any language, including a GRAPH block or an instance-DB) in place. Fails if a block with the new name already exists (names are unique per-PLC). Safe even on a block with real dependents (callers, a bound instance-DB): TIA resolves block/DB references by internal object identity, not by name text, so callers automatically follow the rename. The block and its dependents go temporarily INCONSISTENT, same as any block edit - call compile_plc afterward to resolve that.")]
    public Task<string> RenamePlcBlock(
        [Description("PLC software name, from list_project")] string plcName,
        [Description("Block name to rename, from list_project")] string blockName,
        [Description("New block name")] string newName) => Safe(async () =>
    {
        var result = await _session.RenamePlcBlockAsync(plcName, blockName, newName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "rename_plc_tag")]
    [Description("Rename a single PLC tag in place, in whichever tag table it lives. Unlike changing the name in a tag table CSV in the source tree (which makes a new tag), this keeps references to the tag in blocks intact; blocks show the new name only after compile_plc. Fails if a tag with the new name already exists (names are unique per-PLC).")]
    public Task<string> RenameTag(
        [Description("PLC software name, from list_project")] string plcName,
        [Description("Current tag name")] string tagName,
        [Description("New tag name")] string newName) => Safe(async () =>
    {
        var result = await _session.RenameTagAsync(plcName, tagName, newName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "create_plc_tag_table")]
    [Description("Create a new, empty PLC tag table. Fails if a tag table with this name already exists anywhere in the PLC (names are unique per-PLC, not per-group). Add tags by writing its CSV through the source tree.")]
    public Task<string> CreateTagTable(
        [Description("PLC software name, from list_project")] string plcName,
        [Description("Group path to create the table under, e.g. 'IO/Group1'; empty string for the root")] string groupPath,
        [Description("Name of the new tag table")] string tableName) => Safe(async () =>
    {
        var result = await _session.CreateTagTableAsync(plcName, groupPath, tableName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "delete_plc_tag_table")]
    [Description("Delete a PLC tag table (and all its tags) by name. This is destructive - confirm with the user before calling.")]
    public Task<string> DeleteTagTable(
        [Description("PLC software name, from list_project")] string plcName,
        [Description("Tag table name to delete, from list_project")] string tableName) => Safe(async () =>
    {
        var result = await _session.DeleteTagTableAsync(plcName, tableName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "rename_plc_tag_table")]
    [Description("Rename an existing PLC tag table in place. Fails if a tag table with the new name already exists (names are unique per-PLC).")]
    public Task<string> RenameTagTable(
        [Description("PLC software name, from list_project")] string plcName,
        [Description("Tag table name to rename, from list_project")] string tableName,
        [Description("New tag table name")] string newName) => Safe(async () =>
    {
        var result = await _session.RenameTagTableAsync(plcName, tableName, newName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "create_plc_tag_table_group")]
    [Description("Create a new PLC tag table group (folder) under a given parent group path.")]
    public Task<string> CreateTagTableGroup(
        [Description("PLC software name, from list_project")] string plcName,
        [Description("Parent group path, e.g. 'IO'; empty string for the root")] string parentGroupPath,
        [Description("Name of the new group")] string groupName) => Safe(async () =>
    {
        var result = await _session.CreateTagTableGroupAsync(plcName, parentGroupPath, groupName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "delete_plc_tag_table_group")]
    [Description("Delete a PLC tag table group (folder) and everything in it, including nested tag tables and subgroups. This is destructive - confirm with the user before calling.")]
    public Task<string> DeleteTagTableGroup(
        [Description("PLC software name, from list_project")] string plcName,
        [Description("Full group path to delete, e.g. 'IO/Group1'")] string groupPath) => Safe(async () =>
    {
        var result = await _session.DeleteTagTableGroupAsync(plcName, groupPath);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "rename_plc_tag_table_group")]
    [Description("Rename an existing PLC tag table group (folder) in place, without moving it to a different parent.")]
    public Task<string> RenameTagTableGroup(
        [Description("PLC software name, from list_project")] string plcName,
        [Description("Full group path to rename, e.g. 'IO/Group1'")] string groupPath,
        [Description("New group name")] string newName) => Safe(async () =>
    {
        var result = await _session.RenameTagTableGroupAsync(plcName, groupPath, newName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "rename_hmi_tag")]
    [Description("Rename a single WinCC Unified HMI tag in place, in whichever tag table it lives. Unlike changing the name in an HMI tag table CSV in the source tree (which makes a new tag), this renames the existing tag object. Fails if a tag with the new name already exists (names are unique per-HMI).")]
    public Task<string> RenameHmiTag(
        [Description("HMI software name, from list_project")] string hmiName,
        [Description("Current tag name")] string tagName,
        [Description("New tag name")] string newName) => Safe(async () =>
    {
        var result = await _session.RenameHmiTagAsync(hmiName, tagName, newName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "create_hmi_tag_table")]
    [Description("Create a new, empty WinCC Unified HMI tag table. Fails if a tag table with this name already exists anywhere in the HMI (names are unique per-HMI, not per-group). Add tags by writing its CSV through the source tree.")]
    public Task<string> CreateHmiTagTable(
        [Description("HMI software name, from list_project")] string hmiName,
        [Description("Group path to create the table under; empty string for the root")] string groupPath,
        [Description("Name of the new HMI tag table")] string tableName) => Safe(async () =>
    {
        var result = await _session.CreateHmiTagTableAsync(hmiName, groupPath, tableName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "delete_hmi_tag_table")]
    [Description("Delete a WinCC Unified HMI tag table (and all its tags) by name. This is destructive - confirm with the user before calling.")]
    public Task<string> DeleteHmiTagTable(
        [Description("HMI software name, from list_project")] string hmiName,
        [Description("HMI tag table name to delete, from list_project")] string tableName) => Safe(async () =>
    {
        var result = await _session.DeleteHmiTagTableAsync(hmiName, tableName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "rename_hmi_tag_table")]
    [Description("Rename an existing WinCC Unified HMI tag table in place. Fails if a tag table with the new name already exists (names are unique per-HMI).")]
    public Task<string> RenameHmiTagTable(
        [Description("HMI software name, from list_project")] string hmiName,
        [Description("HMI tag table name to rename, from list_project")] string tableName,
        [Description("New HMI tag table name")] string newName) => Safe(async () =>
    {
        var result = await _session.RenameHmiTagTableAsync(hmiName, tableName, newName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "create_hmi_tag_table_group")]
    [Description("Create a new WinCC Unified HMI tag table group (folder) under a given parent group path.")]
    public Task<string> CreateHmiTagTableGroup(
        [Description("HMI software name, from list_project")] string hmiName,
        [Description("Parent group path; empty string for the root")] string parentGroupPath,
        [Description("Name of the new group")] string groupName) => Safe(async () =>
    {
        var result = await _session.CreateHmiTagTableGroupAsync(hmiName, parentGroupPath, groupName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "delete_hmi_tag_table_group")]
    [Description("Delete a WinCC Unified HMI tag table group (folder) and everything in it, including nested tag tables and subgroups. This is destructive - confirm with the user before calling.")]
    public Task<string> DeleteHmiTagTableGroup(
        [Description("HMI software name, from list_project")] string hmiName,
        [Description("Full group path to delete")] string groupPath) => Safe(async () =>
    {
        var result = await _session.DeleteHmiTagTableGroupAsync(hmiName, groupPath);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "rename_hmi_tag_table_group")]
    [Description("Rename an existing WinCC Unified HMI tag table group (folder) in place, without moving it to a different parent.")]
    public Task<string> RenameHmiTagTableGroup(
        [Description("HMI software name, from list_project")] string hmiName,
        [Description("Full group path to rename")] string groupPath,
        [Description("New group name")] string newName) => Safe(async () =>
    {
        var result = await _session.RenameHmiTagTableGroupAsync(hmiName, groupPath, newName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "delete_hmi_alarm_class")]
    [Description("Delete a WinCC Unified HMI alarm class by name. Fails with TIA Portal's own error if any alarm still references this class. This is destructive - confirm with the user before calling.")]
    public Task<string> DeleteHmiAlarmClass(
        [Description("HMI software name, from list_project")] string hmiName,
        [Description("Alarm class name to delete, from the source tree's AlarmClasses.csv")] string name) => Safe(async () =>
    {
        var result = await _session.DeleteHmiAlarmClassAsync(hmiName, name);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "delete_hmi_alarm")]
    [Description("Delete a WinCC Unified HMI alarm (discrete or analog) by name. This is destructive - confirm with the user before calling.")]
    public Task<string> DeleteHmiAlarm(
        [Description("HMI software name, from list_project")] string hmiName,
        [Description("'Discrete' or 'Analog'")] string type,
        [Description("Alarm name to delete, from the source tree's alarm CSVs")] string alarmName) => Safe(async () =>
    {
        var result = await _session.DeleteHmiAlarmAsync(hmiName, type, alarmName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "delete_hmi_screen")]
    [Description("Delete a WinCC Unified screen by name. This is destructive - confirm with the user before calling.")]
    public Task<string> DeleteHmiScreen(
        [Description("HMI software name, from list_project")] string hmiName,
        [Description("Screen name to delete, from list_project")] string screenName) => Safe(async () =>
    {
        var result = await _session.DeleteHmiScreenAsync(hmiName, screenName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "rename_hmi_screen")]
    [Description("Rename a WinCC Unified screen in place. Fails if a screen with the new name already exists (names are unique per HMI). Screen windows and scripts refer to screens by name and are not updated.")]
    public Task<string> RenameHmiScreen(
        [Description("HMI software name, from list_project")] string hmiName,
        [Description("Screen name to rename, from list_project")] string screenName,
        [Description("New screen name")] string newName) => Safe(async () =>
    {
        var result = await _session.RenameHmiScreenAsync(hmiName, screenName, newName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "create_hmi_screen_group")]
    [Description("Create a new WinCC Unified screen group (folder) under a given parent group path.")]
    public Task<string> CreateHmiScreenGroup(
        [Description("HMI software name, from list_project")] string hmiName,
        [Description("Parent group path; empty string for the root")] string parentGroupPath,
        [Description("Name of the new group")] string groupName) => Safe(async () =>
    {
        var result = await _session.CreateHmiScreenGroupAsync(hmiName, parentGroupPath, groupName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "delete_hmi_screen_group")]
    [Description("Delete a WinCC Unified screen group (folder) and everything in it, including its screens and subgroups. This is destructive - confirm with the user before calling.")]
    public Task<string> DeleteHmiScreenGroup(
        [Description("HMI software name, from list_project")] string hmiName,
        [Description("Full group path to delete")] string groupPath) => Safe(async () =>
    {
        var result = await _session.DeleteHmiScreenGroupAsync(hmiName, groupPath);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "rename_hmi_screen_group")]
    [Description("Rename a WinCC Unified screen group (folder) in place, without moving it to a different parent.")]
    public Task<string> RenameHmiScreenGroup(
        [Description("HMI software name, from list_project")] string hmiName,
        [Description("Full group path to rename")] string groupPath,
        [Description("New group name")] string newName) => Safe(async () =>
    {
        var result = await _session.RenameHmiScreenGroupAsync(hmiName, groupPath, newName);
        return result.Success ? "Success" : $"FAILED: {result.Error}";
    });

    [McpServerTool(Name = "describe_hmi_item_type")]
    [Description("Describe a WinCC Unified screen item type for editing a .json: its settable properties (JSON key, value type, enum members), nested parts, events and dynamization options. Call without a type to list all item types; pass 'Screen' for the screen's own properties. Needs no connection.")]
    public Task<string> DescribeHmiItemType(
        [Description("Item type, e.g. 'Button', 'IOField', 'FaceplateContainer'; omit to list all types")] string? type = null) =>
        Safe(() => Task.FromResult(TiaConnection.DescribeHmiItemType(type)));

    [McpServerTool(Name = "list_library_types")]
    [Description("List the types in the project library (folder path, name, kind, and per version: version number, default flag, state and the ContainedType string a FaceplateContainer in a .json uses). Faceplates must be in the project library to be placed on a screen; global libraries are not supported.")]
    public Task<string> ListLibraryTypes(
        [Description("Optional substring filter on the type name")] string? nameFilter = null) => Safe(async () =>
    {
        var types = await _session.ListProjectLibraryTypesAsync(nameFilter);
        return Csv.Format(
            new[] { "FolderPath", "Name", "Kind", "Version", "IsDefault", "State", "ContainedType" },
            types.SelectMany(t => t.Versions.Count == 0
                ? new[] { new[] { t.FolderPath, t.Name, t.Kind, "", "", "", "" } }
                : t.Versions.Select(v => new[] { t.FolderPath, t.Name, t.Kind, v.Version, v.IsDefault.ToString(), v.State, v.ContainedType })));
    });

    [McpServerTool(Name = "read_faceplate_type")]
    [Description("Show a faceplate type's interface (property names, value types, default values) and events, as needed for the \"Interface\" of a FaceplateContainer in a .json. Openness only exposes the interface on a placed instance, so this places one on a scratch screen inside a rolled-back transaction - the project is not changed.")]
    public Task<string> ReadFaceplateType(
        [Description("HMI software name, from list_project (the faceplate must be usable on this device)")] string hmiName,
        [Description("Faceplate type + version as list_library_types shows it in ContainedType, e.g. 'V1.0.0\\MyFaceplate'")] string containedType) => Safe(async () =>
    {
        var result = await _session.ReadFaceplateTypeAsync(hmiName, containedType);
        if (!result.Success) return $"FAILED: {result.Error}";
        var csv = Csv.Format(
            new[] { "PropertyName", "ValueType", "DefaultValue" },
            result.Interface.Select(p => new[] { p.PropertyName, p.ValueType ?? "", p.DefaultValue ?? "" }));
        return $"ContainedType: {result.ContainedType}\nEvents: {string.Join(", ", result.Events)}\nInterface:\n{csv}";
    });

    [McpServerTool(Name = "compile_plc")]
    [Description("Compile a PLC's software and report errors/warnings. On a Multiuser local session this compiles locally, can mark the session modified, and does not commit results to the server.")]
    public Task<string> Compile([Description("PLC software name, from list_project")] string plcName) => Safe(async () =>
    {
        var result = await _session.CompileAsync(plcName);
        var messages = string.Join("\n", result.Messages);
        return $"State: {result.State}  Errors: {result.ErrorCount}  Warnings: {result.WarningCount}\n{messages}";
    });

    [McpServerTool(Name = "list_project")]
    [Description(@"List what the connected project has, per PLC and HMI software ('=== name ===' - the plcName/hmiName other tools take): blocks with their language (INCONSISTENT: needs compile_plc, e.g. before a GRAPH or STL block can be read), UDTs, tag tables, empty tag table groups and alarm text lists; for WinCC Unified the HMI tag tables, screens and alarm files, for classic WinCC the tag tables, text lists, screen templates, pop-up screens and screens. Fast: names only, no source - read_source_tree reads the source. Each item line ends with the formats it can be read in, its own first, e.g. ""tag table 'Tags' [csv, xml]"" (""xml"" is the raw Openness export). Item lines are in _export_summary.txt's format and can be passed to read_source_tree's items as they are.

Without filter or deviceName, a project of more than 200 items only gets counts per software back; pass targetDirectory to write the full list to '<targetDirectory>/_project_list.txt'.")]
    public Task<string> ListProject(
        [Description("Optional: only items whose group path or name contains this text (case-insensitive)")] string? filter = null,
        [Description("Optional: only this PLC or HMI software name")] string? deviceName = null,
        [Description("Optional: directory (e.g. the source tree root) to write the full list to as _project_list.txt")] string? targetDirectory = null) => Safe(async () =>
    {
        if (!_session.IsConnected) return "Not connected. Call tia_connect first.";

        bool Matches(string groupPath, string name) => filter == null || Label(groupPath, name).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        var list = new StringBuilder();
        var counts = new StringBuilder();
        var total = 0;
        var devices = await Devices(deviceName);
        foreach (var (family, device) in devices)
        {
            var kinds = new List<(ISourceKind Kind, List<ListedEntry> Entries)>();
            foreach (var kind in SourceKinds.Of(family).OrderBy(k => SourceKinds.ListIndex(k.Name)))
                kinds.Add((kind, (await kind.List(_session, device)).Where(e => Matches(e.GroupPath, e.Name)).ToList()));

            var found = kinds.Sum(k => k.Entries.Count);
            total += found;
            counts.AppendLine($"{device}{(family == DeviceFamily.Plc ? "" : family == DeviceFamily.HmiClassic ? " (HMI, classic WinCC)" : " (HMI)")}: " +
                              string.Join(", ", kinds.Where(k => k.Kind.FixedName == null).Select(k => CountLine(k.Kind, k.Entries))));
            if (found == 0 && filter != null) continue;

            list.AppendLine(Header(family, device));
            foreach (var entry in kinds.SelectMany(k => k.Entries)) list.AppendLine($"  {entry.Text}{FormatList(entry.Formats)}");
            list.AppendLine();
        }

        if (devices.Count == 0) return deviceName == null ? "No PLC or HMI software found." : $"No PLC or HMI software named '{deviceName}'.";

        string? listPath = null;
        if (!string.IsNullOrEmpty(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
            listPath = Path.Combine(targetDirectory, "_project_list.txt");
            File.WriteAllText(listPath, list.ToString());
        }

        if (filter != null || deviceName != null || total <= 200) return total == 0 ? "No matching items." : list.ToString();
        return counts + (listPath != null
            ? $"Full list: {listPath}"
            : "Pass filter or deviceName to list items, or targetDirectory to write the full list to a file.");
    });

    private static string CountLine(ISourceKind kind, List<ListedEntry> entries)
    {
        var inconsistent = entries.Count(e => e.Inconsistent);
        return $"{entries.Count(e => e.IsItem)} {kind.Name}(s){(inconsistent > 0 ? $" ({inconsistent} inconsistent)" : "")}";
    }

    private static string Header(DeviceFamily family, string device) =>
        family == DeviceFamily.Plc ? $"=== {device} ===" : $"=== {device} (HMI) ===";

    // Every PLC and HMI software of the project, or only deviceName's, with its family.
    private async Task<List<(DeviceFamily Family, string Name)>> Devices(string? deviceName)
    {
        bool IsSelected(string device) => deviceName == null || string.Equals(device, deviceName, StringComparison.OrdinalIgnoreCase);
        var devices = new List<(DeviceFamily, string)>();
        devices.AddRange((await _session.ListDevicesAsync()).Select(d => d.PlcSoftwareName).OfType<string>().Where(IsSelected).Select(n => (DeviceFamily.Plc, n)));
        devices.AddRange((await _session.ListHmiDevicesAsync()).Select(h => h.HmiSoftwareName).Where(IsSelected).Select(n => (DeviceFamily.HmiUnified, n)));
        devices.AddRange((await _session.ListHmiClassicNamesAsync()).Where(IsSelected).Select(n => (DeviceFamily.HmiClassic, n)));
        return devices;
    }

    [McpServerTool(Name = "read_source_tree")]
    [Description(@"Export project source to disk, into folders that mirror the TIA Portal project tree: <targetDirectory>/<PlcName>/Program blocks/<group path>/..., .../PLC tags/..., .../PLC data types/..., and .../PLC alarm text lists/<list>.csv; <targetDirectory>/<HmiSoftwareName>/HMI tags/..., .../Screens/<group path>/<screen>.json (WinCC Unified) and .../HMI alarms/ (DiscreteAlarms.csv, AnalogAlarms.csv, AlarmClasses.csv - fixed files, alarms have no groups in Openness); for a classic WinCC HMI .../HMI tags/, Screens/, Screen templates/, Pop-up screens/ (each with its folders) and Text lists/, as native Openness XML. Classic graphic lists are skipped (exporting one crashes TIA Portal V21). Read the files with your file tools; edit them and call write_source_tree to write the changes back.

Pass items to read only part of the project (much faster than a full read): e.g. one block to look at or refresh, or a group. A read overwrites the selected items' files, including unsaved edits to them. An item that no longer exists in the project (deleted or renamed in TIA Portal) has its files deleted.

Files per item: blocks and UDTs keep TIA Portal's own document format (.s7dcl, plus .s7res holding the comment/title texts its S7_MLC attributes refer to); a GRAPH block is '<name>.graph.il', a whole-block-STL block '<name>.awl' (TIA Portal's own 'Generate source' text). Read-only companions, never written back: '<name>.interface.txt' (an instance DB's resolved interface) and '<name>.stl-networks.awl'/'.xml' (the STL networks inside an otherwise FBD/LAD/SCL block; the .s7dcl keeps an empty placeholder NETWORK with S7_Language := ""STL"" at each one's position). Tag tables, alarms and alarm text lists (From, To, one Text [language] column per project language) are Excel-compatible CSV with a UTF-8 BOM.

Formats: blocks, UDTs and PLC tag tables can also be read as their raw Openness XML ('<name>.xml', SimaticML): complete where a converted format leaves something out, and writable too. An item is read in the formats it already has in the tree, else its own format; pass its list line with formats (""tag table 'Tags' [csv, xml]"", ""block 'Main' [xml]"") to choose. The XML costs no extra export where the own format is made from it (tag tables, GRAPH and code blocks); for data blocks, UDTs and whole-STL blocks it does. _manifest.json records each item's formats and each file's format.

An item that fails to export (e.g. an inconsistent block - GRAPH blocks must be compiled first) is reported, and files from an earlier read of it are renamed with a '.stale' suffix rather than left looking current. '_export_summary.txt' lists every item in the tree, with the formats it has there (list_project shows the formats it can be read in); '_manifest.json' and the '.base' folder are write_source_tree's bookkeeping - don't edit them. Returns counts, failures, and for a partial read each item's files.")]
    public Task<string> ReadSourceTree(
        [Description("Root directory of the source tree. Created if it doesn't exist; keep using the same one for the project.")] string targetDirectory,
        [Description("Optional: read only these items - 'Name', 'group/path/Name', 'group/path/' for everything below a group, or item lines as list_project shows them, optionally ending with the formats to read, e.g. \"tag table 'Tags' [csv, xml]\" (case-insensitive; for HMI alarms 'DiscreteAlarms', 'AnalogAlarms', 'AlarmClasses' or 'HMI alarms/'). Omit to read everything.")] string[]? items = null,
        [Description("Optional: read only this PLC or HMI software name")] string? deviceName = null,
        [Description("Read into a tree that has an _export_summary.txt but no _manifest.json (read by an older version, or generated), making it a working copy of the connected project. Default false.")] bool adoptExisting = false) => Safe(async () =>
    {
        if (!_session.IsConnected) return "Not connected. Call tia_connect first.";

        var selection = items is { Length: > 0 } ? items : null;
        var partial = selection != null || deviceName != null;
        Directory.CreateDirectory(targetDirectory);
        var previous = SourceTreeManifest.Load(targetDirectory);
        // A tree without a manifest is written as-is by write_source_tree (e.g. one a generator
        // script writes). A manifest would turn it into a working copy, whose written items are read
        // back into it - replacing generated files with TIA Portal's rendering of them.
        if (previous == null && !adoptExisting && File.Exists(Path.Combine(targetDirectory, "_export_summary.txt")))
        {
            return $"'{targetDirectory}' holds a source tree without {SourceTreeManifest.FileName} - a generated tree, or one read by an older version. " +
                   "Reading into it would make it a working copy of the project, and write_source_tree would then read written items back into it, replacing its files with TIA Portal's rendering. " +
                   "Read into another directory - or, for a tree read by an older version, pass adoptExisting=true.";
        }
        if (partial && previous?.Project != null && previous.Project != _session.ProjectName)
        {
            return $"'{targetDirectory}' holds the source tree of project '{previous.Project}', not of the connected project '{_session.ProjectName}'. " +
                   "Read the whole project (no items/deviceName) to replace it, or use another directory.";
        }

        var startedAt = DateTime.Now;
        var (exported, devices) = await ExportItems(selection == null ? null : (_, kind, groupPath, name) => RequestedFormats(selection, kind, groupPath, name), deviceName, previous);
        if (devices.Count == 0) return deviceName == null ? "No PLC or HMI devices found." : $"No PLC or HMI software named '{deviceName}'.";

        var manifest = previous ?? new SourceTreeManifest();
        WriteExport(targetDirectory, manifest, exported);

        // Items in the part that was read which the project doesn't have (any more).
        var gone = manifest.Items
            .Where(i => (deviceName == null || string.Equals(i.Device, deviceName, StringComparison.OrdinalIgnoreCase))
                        && (selection == null || RequestedFormats(selection, i.Kind, i.GroupPath, i.Name) != null)
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
        foreach (var item in gone) result.AppendLine($"  REMOVED {Describe(item.Kind, item.GroupPath, item.Name)} - no longer in the project, its files were deleted");
        foreach (var note in _exportNotes) result.AppendLine($"  {note}");
        result.AppendLine($"Full item list: {summaryPath}");
        return result.ToString();
    });

    [McpServerTool(Name = "write_source_tree")]
    [Description(@"Write a source tree from read_source_tree back into the connected project: every item whose files were edited since the last read, and every new file - a new block, UDT, tag table or WinCC Unified screen is created by adding its file in the right folder (the folder gives the group, the file name the item's name; for a new block or UDT start from an existing file of the same kind). Written items are read back into the tree afterward, so their files show what TIA Portal made of them. Not compiled - call compile_plc afterward, and after an FB interface change read the instance DBs again - except that a PLC whose written items have an .xml in the tree is compiled before they are read back (an XML export needs a consistent object). Deleting a file does not delete the item in the project (use the delete_* tools).

Refuses (FAILED, nothing written for that item) to overwrite an item that was changed in TIA Portal since it was read, or that exists in the project but was never read into the tree; force=true overwrites anyway. A tree read from another project (e.g. to restore it into an emptied copy) is written in full, without these checks. So is a tree without _manifest.json, such as one a generator script writes: everything its _export_summary.txt (or itemList) lists is written, and nothing is read back into it.

An item in more than one format is written from the one format whose files were edited; an item edited in two formats is refused (FAILED) - only one can be written. All formats are read back afterward.

Writing rules per file type:
- SCL block: the '{ S7_EditorMode := ""SCL"" }' attribute block before FUNCTION/FUNCTION_BLOCK/DATA_BLOCK is required. FBD/LAD: never hand-write NETWORK/RUNG notation from scratch - copy a similar existing block's .s7dcl/.s7res and adapt it.
- Comments and titles: an { S7_MLC := ""MLC_x"" } attribute (S7_NetworkTitle/S7_NetworkComment for networks) referring to an entry in the .s7res; '//' comments in the source are dropped by TIA Portal. Keep text ids unique within a .s7res.
- GRAPH ('.graph.il'): step/transition attributes, actions and conditions are writable, steps/transitions can be added or removed, and BRANCHES/CONNECTIONS of a SEQUENCE are rewritten when present; INTERFACE and PREOPERATIONS are read-only. A condition still containing a construct shown as generic 'PartName(args)' is refused. A new GRAPH block is made by cloning an existing GRAPH block of the PLC (picked automatically) and reshaping it. The write recompiles the block and reports errors.
- Whole-block STL ('.awl'): AWL text as TIA Portal's 'Generate source' produces it, block name in the header unchanged. A block with embedded STL networks can't be written.
- Instance DB: its .s7dcl ('DATA_BLOCK ""x"" ... : FB_y'); the FB must exist.
- Raw XML ('<name>.xml' of a block, UDT or PLC tag table): SimaticML as read; replaces the whole object (a table's removed tags go too), or creates it - also GRAPH blocks and blocks with embedded STL networks. Its <Name> must match the file name.
- PLC tag table CSV: Name, DataType, LogicalAddress, Comment, ExternalAccessible, ExternalVisible, ExternalWritable (True/False). Rows update or add tags; an empty cell leaves that value unchanged; removing a row deletes that tag (not when writing a tree read from another project). Renaming a tag in the CSV deletes it and makes a new one, breaking references - use rename_plc_tag.
- HMI tag table CSV: Name, DataType, Address, Connection, PlcName, PlcTag, Comment, AcquisitionCycle. Bind to a PLC tag with Connection + PlcTag (dot-qualified for a DB member); DataType then follows from the PLC tag. PlcName is ignored on write. AcquisitionCycle (e.g. T1s) only on PLC-bound tags. Rows and removed rows as for PLC tag tables; rename with rename_hmi_tag.
- PLC alarm text list CSV ('PLC alarm text lists/<list>.csv'): From, To, Text [language]... Only existing user lists (create a new list in TIA Portal). The write patches a fresh export of all lists and imports it, so other lists are untouched; a removed row deletes that entry (not when writing a tree read from another project); a language column left out stays unchanged. Every list's language must be a project language.
- Classic WinCC XML (screens, templates, pop-ups, tag tables, text lists): native Openness XML as read; <Name> must match the file name. A new screen needs a <Number> not used by another screen - checked before the import (a duplicate number makes TIA Portal V21 terminate itself).
- WinCC Unified screen ('.json', built from the Openness object model - there is no export format): the screen's properties plus ""items"", one object per screen item with ""name"", ""type"" (see describe_hmi_item_type) and its properties under their Openness names; parts are nested objects, part lists arrays. Colors '#AARRGGBB', enums by member name, texts plain text in the editing language. Reserved keys: ""dynamizations"" ({""<Property>"": ""<HMI tag>""} or {""type"": ""Script"", ...}), ""events"" ({""<EventType>"": ""<JS code>""}), ""propertyEvents"" ({""<Property>:Change"": ...}). A FaceplateContainer has ""ContainedType"" (see list_library_types) and ""Interface"" (a UDT-typed property takes the HMI tag name; see read_faceplate_type). Only values that differ from a new item are shown; leaving a property, dynamization or event out of the file doesn't reset it - set the default value, or null to remove a dynamization/script. Items are matched by name: a new name creates the item (appended - Openness has no z-order control), a changed type recreates it, a removed item is deleted (not when writing a tree read from another project). A new screen is created from a new file. Each property is set on its own; failures are reported per property and the rest still applies. SLOW: a screen is written item by item through Openness - a new screen (also a copy of an existing one: Openness can't copy Unified screens) or a forced write takes about 0.2 s per item, close to a minute for a screen of a few hundred items. An edit to a screen read into this tree writes only the changed properties and is fast. The read before and after each write adds a few seconds per screen.
- HMI alarms CSV: rows update or add alarms by name; a removed row is not deleted (use delete_hmi_alarm). RaisedStateTag binds the trigger (a tag or member path whose base HMI tag exists); TriggerAddress is computed and read-only. EventText/InfoText are plain text. AlarmClasses.csv: Name, Priority, Log; system classes accept no Priority change.

Writes in dependency order: UDTs, then tag tables, then blocks, with UDTs and blocks each split into layers by what their source references (nested UDTs first; global DBs and called FBs before their callers and instance DBs), compiling the PLC between UDT layers - imports can fail, or a UDT can silently lose nested start values, when what they reference isn't there yet.
Then the PLC alarm text lists. An HMI is written tag tables before screens (a classic one text lists first, then tag tables, templates and pop-ups, then screens).

itemList limits what is considered, as lines from list_project or _export_summary.txt (a file path, or the lines inline; the OK status column is optional); all=true writes those items even if unchanged. Set dryRun to preview what would be created/updated/skipped/refused.

Writes '<sourceDirectory>/_import_report.txt' with one CREATED/UPDATED/SKIPPED/FAILED line per item (per row for alarms). Returns that report for a small write; for a larger one, the counts per software plus every SKIPPED/FAILED line.")]
    public Task<string> WriteSourceTree(
        [Description("Root directory of the source tree, as passed to read_source_tree.")] string sourceDirectory,
        [Description("Optional: consider only these items - lines from list_project or _export_summary.txt, as a file path or inline. Omit to consider the whole tree, including new files.")] string? itemList = null,
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
            if (allItems.Count == 0) return $"No items recognized in the given list. {ItemListExample}";
            // A label is split at its last '/', but an item name can hold a '/' itself
            // ("Group/Pump 1/2"): the manifest knows where the group path ends.
            if (manifest != null)
                allItems = allItems.Select(i => manifest.Items.FirstOrDefault(m => m.Device == i.DeviceName && m.Kind == i.Kind
                        && string.Equals(Label(m.GroupPath, m.Name), Label(i.GroupPath, i.ItemName), StringComparison.OrdinalIgnoreCase)) is { } m
                    ? new ImportListItem(i.DeviceName, i.Kind, m.GroupPath, m.Name, i.Ok)
                    : i).ToList();
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
            notes.Add("No _manifest.json in the tree (generated, or read by an older version): every listed item is written, without change or conflict checks, and nothing is read back into the tree.");
        else if (!workingCopy)
            notes.Add($"The tree was read from project '{manifest.Project}', not the connected '{_session.ProjectName}': every listed item is written, and nothing is compared with or refreshed from this project.");

        // The format each item is written from; unchanged items are left out unless all is set.
        var toWrite = new List<ImportListItem>();
        var refused = new List<(ImportListItem Item, string Reason)>();
        var unchanged = 0;
        foreach (var item in items)
        {
            var entry = workingCopy ? manifest!.Find(item.DeviceName, item.Kind, item.ItemName) : null;
            var (format, state, error) = WriteFormat(sourceDirectory, item, entry, workingCopy);
            if (error != null) { refused.Add((item, error)); continue; }
            if (state == DiskState.Deleted)
            {
                notes.Add($"{Describe(item.Kind, item.GroupPath, item.ItemName)}: its files were deleted from the tree. That doesn't delete it in the project - use the delete_* tool for that.");
                continue;
            }
            if (state == DiskState.Unchanged && !all) { unchanged++; continue; }
            item.Format = format;
            toWrite.Add(item);
        }
        if (unchanged > 0) notes.Add($"{unchanged} item(s) unchanged since read_source_tree were not written (all=true writes them anyway).");
        items = toWrite;

        var projectDevices = await Devices(null);
        await _session.ClearHmiClassicScreenNumbersAsync();

        var conflicts = new List<(ImportListItem Item, string Reason)>();
        if (workingCopy && !force && items.Count > 0)
        {
            conflicts = await FindConflicts(manifest!, items);
            items = items.Where(i => !conflicts.Any(c => c.Item == i)).ToList();
        }
        conflicts.AddRange(refused);

        var startedAt = DateTime.Now;
        var summary = new StringBuilder();
        summary.AppendLine($"Source tree write - {startedAt:yyyy-MM-dd HH:mm:ss}{(dryRun ? " (dry run)" : "")}");
        summary.AppendLine($"Project: {_session.ProjectName}");
        summary.AppendLine($"Source: {sourceDirectory}");
        summary.AppendLine();

        // The tool result: per software its counts plus every line that isn't a success. The full
        // report, one line per item (per alarm row), goes to _import_report.txt only - it can be
        // longer than a tool result may be.
        var compact = new StringBuilder();
        var written = new List<ImportListItem>();
        int totalCreated = 0, totalUpdated = 0, totalSkipped = 0, totalFailed = 0;
        foreach (var deviceGroup in items.Concat(conflicts.Select(c => c.Item)).GroupBy(i => i.DeviceName, StringComparer.OrdinalIgnoreCase))
        {
            var deviceName = deviceGroup.Key;
            var deviceDir = Path.Combine(sourceDirectory, DirName(deviceName));
            var deviceItems = deviceGroup.Where(items.Contains).ToList();
            var family = projectDevices.Where(d => string.Equals(d.Name, deviceName, StringComparison.OrdinalIgnoreCase)).Select(d => (DeviceFamily?)d.Family).FirstOrDefault();
            var isHmi = family is DeviceFamily.HmiUnified or DeviceFamily.HmiClassic || (family == null && deviceGroup.All(i => i.Kind.StartsWith("HMI")));
            summary.AppendLine(isHmi ? $"=== {deviceName} (HMI) ===" : $"=== {deviceName} ===");

            var logs = new List<WriteLog>();
            var conflictLog = new WriteLog();
            conflictLog.Failed.AddRange(conflicts.Where(c => deviceGroup.Contains(c.Item)).Select(c => $"{Describe(c.Item.Kind, c.Item.GroupPath, c.Item.ItemName)}: {c.Reason}"));
            logs.Add(conflictLog);

            // Each kind in the registry's write order, which puts what an item references first.
            var kinds = family == null ? new List<ISourceKind>() : SourceKinds.Of(family.Value).ToList();
            var context = new WriteContext(_session, deviceName, sourceDirectory, deviceDir, createMissing, overwriteExisting, dryRun, workingCopy,
                workingCopy && !force ? manifest : null, written);
            foreach (var kind in kinds)
            {
                var kindItems = deviceItems.Where(i => i.Kind == kind.Name).ToList();
                if (kindItems.Count > 0) logs.Add(await kind.Write(context, kindItems));
            }

            var unknown = new WriteLog();
            unknown.Failed.AddRange(deviceItems.Where(i => !kinds.Any(k => k.Name == i.Kind)).Select(i => family == null
                ? $"{Describe(i.Kind, i.GroupPath, i.ItemName)}: the project has no PLC or HMI software named '{deviceName}'"
                : $"{Describe(i.Kind, i.GroupPath, i.ItemName)}: a {i.Kind} can't be written to {(family == DeviceFamily.Plc ? "a PLC" : family == DeviceFamily.HmiClassic ? "a classic WinCC HMI" : "a WinCC Unified HMI")}"));
            logs.Add(unknown);

            // See ISourceKind.XmlNeedsCompile.
            if (workingCopy && written.Any(w => string.Equals(w.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase)
                    && kinds.Any(k => k.Name == w.Kind && k.XmlNeedsCompile)
                    && (w.Format == SourceFormat.Xml || manifest!.Find(w.DeviceName, w.Kind, w.ItemName)?.WritableFiles.Any(f => f.Value.Format == SourceFormat.Xml) == true)))
            {
                var compile = await _session.CompileAsync(deviceName);
                notes.Add($"Compiled '{deviceName}' so the written items can be read back as XML: {compile.State}, {compile.ErrorCount} error(s), {compile.WarningCount} warning(s).");
            }

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

            compact.AppendLine($"{(isHmi ? $"=== {deviceName} (HMI) ===" : $"=== {deviceName} ===")} {logs.Sum(l => l.Created.Count)} created, " +
                               $"{logs.Sum(l => l.Updated.Count)} updated, {logs.Sum(l => l.Skipped.Count)} skipped, {logs.Sum(l => l.Failed.Count)} failed");
            foreach (var line in logs.SelectMany(l => l.Skipped)) compact.AppendLine($"  SKIPPED  {line}");
            foreach (var line in logs.SelectMany(l => l.Failed)) compact.AppendLine($"  FAILED   {line}");
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
            var (exported, _) = await ExportItems((device, kind, _, name) => written.Any(w => w.Is(device, kind, name)) ? Array.Empty<string>() : null, null, manifest);
            WriteExport(sourceDirectory, manifest!, exported);
            manifest!.Save(sourceDirectory);
            File.WriteAllText(Path.Combine(sourceDirectory, "_export_summary.txt"), FormatSummary(sourceDirectory, manifest));
            notes.Add($"Refreshed the {exported.Count(e => e.Ok)} written item(s) in the tree from the project.");
        }

        var footer = new StringBuilder();
        foreach (var note in notes) footer.AppendLine(note);
        if (totalCreated + totalUpdated + totalFailed + totalSkipped == 0 && !dryRun) footer.AppendLine("Nothing to write.");
        footer.AppendLine($"Done: {totalCreated} created, {totalUpdated} updated, {totalSkipped} skipped, {totalFailed} failed. Finished {DateTime.Now:yyyy-MM-dd HH:mm:ss} (started {startedAt:HH:mm:ss}).");

        var summaryText = summary.Append(footer).ToString();
        var reportPath = Path.Combine(sourceDirectory, "_import_report.txt");
        File.WriteAllText(reportPath, summaryText);
        if (totalCreated + totalUpdated + totalSkipped + totalFailed <= 30) return summaryText;
        return compact.Append(footer).AppendLine($"Full report, one line per item: {reportPath}").ToString();
    });

    private enum DiskState { Unchanged, Changed, Deleted }

    // The format an item is written from, and whether it was edited since the last read. For an
    // item of the working copy that the last read wrote (entry): the one format whose files were
    // edited - an edit in two formats is refused, only one can be written - or, unedited, the first
    // format it has in the tree. For anything else (a new file, a tree from another project or
    // without manifest): the first format of the kind whose file is there - so its own format
    // before the raw XML - except that a new item of the working copy in two formats is refused.
    private static (string? Format, DiskState State, string? Error) WriteFormat(string root, ImportListItem item, SourceTreeItem? entry, bool workingCopy)
    {
        var kind = SourceKinds.Find(item.Kind)!;
        var dir = ItemDir(item.DeviceName, item.Kind, item.GroupPath);
        var stem = Sanitize(ItemStem(item.Kind, item.ItemName));
        var present = kind.Formats.Where(f => File.Exists(Path.Combine(root, $"{dir}/{stem}{SourceFormat.Extension(f)}"))).ToList();
        if (entry == null)
        {
            if (workingCopy && present.Count > 1)
                return (null, DiskState.Changed, $"is new in the tree in more than one format ({string.Join(", ", present)}). Keep the file of one format only, and write again.");
            return (present.FirstOrDefault(), DiskState.Changed, null);
        }

        var edited = EditedFormats(root, entry, kind, dir, stem);
        if (edited.Count > 1)
            return (null, DiskState.Changed, $"was edited in more than one format ({string.Join(", ", edited)}), and only one can be written. Undo the edit in all but one of them, or read the item again to bring them in line.");
        if (edited.Count == 1) return (edited[0], DiskState.Changed, null);
        if (present.Count == 0 && entry.WritableFiles.Any()) return (null, DiskState.Deleted, null);
        return (present.FirstOrDefault(), DiskState.Unchanged, null);
    }

    // The formats of an item whose files on disk differ from what read_source_tree last wrote: a
    // changed or deleted file of a format that still has files, or a file the read didn't produce
    // (e.g. a .s7res added next to a .s7dcl, or an .xml next to a .csv). A format whose files were
    // all deleted is not an edit.
    private static List<string> EditedFormats(string root, SourceTreeItem entry, ISourceKind kind, string dir, string stem)
    {
        var edited = new List<string>();
        foreach (var format in entry.WritableFiles.GroupBy(f => f.Value.Format))
        {
            var present = format.Where(f => File.Exists(Path.Combine(root, f.Key))).ToList();
            if (present.Count == 0) continue;
            if (present.Count < format.Count() || present.Any(f => SourceTreeManifest.Hash(File.ReadAllText(Path.Combine(root, f.Key))) != f.Value.Hash))
                edited.Add(format.Key);
        }

        foreach (var format in kind.Formats.Where(f => !edited.Contains(f)))
        {
            var files = new List<string> { $"{dir}/{stem}{SourceFormat.Extension(format)}" };
            if (format == SourceFormat.S7dcl) files.Add($"{dir}/{stem}.s7res");
            if (files.Any(path => File.Exists(Path.Combine(root, path)) && !entry.Files.Keys.Contains(path, StringComparer.OrdinalIgnoreCase)))
                edited.Add(format);
        }
        return edited;
    }

    // Source files in the tree that no read put there: new items to create. Kind and group path
    // follow from the folder (see ItemDir), the name from the file name.
    private static List<ImportListItem> NewItemsInTree(string root, SourceTreeManifest manifest)
    {
        var found = new List<ImportListItem>();
        foreach (var deviceDir in Directory.GetDirectories(root).Where(d => Path.GetFileName(d) != BaseDirName))
        {
            var dirName = Path.GetFileName(deviceDir);
            var device = manifest.Items.Select(i => i.Device).FirstOrDefault(d => DirName(d) == dirName) ?? dirName;

            // The group each folder holds per the last read: a folder doesn't always give the group
            // path back ('/' in a group name, trailing spaces and dots, replaced characters).
            var groupOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in manifest.Items.Where(i => i.Device == device && !string.IsNullOrEmpty(i.GroupPath)))
                groupOf[ItemDir(item.Device, item.Kind, item.GroupPath)] = item.GroupPath;

            // Two kinds can share a section (classic and Unified HMI tag tables): a file is new for
            // the first kind it fits.
            foreach (var kind in SourceKinds.All)
            {
                var sectionDir = Path.Combine(deviceDir, kind.Section);
                if (!Directory.Exists(sectionDir)) continue;
                foreach (var file in Directory.GetFiles(sectionDir, "*", SearchOption.AllDirectories))
                {
                    var fileName = Path.GetFileName(file);
                    var ext = kind.Formats.Select(SourceFormat.Extension).FirstOrDefault(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));
                    if (ext == null || SourceTreeManifest.IsReadOnlyCompanion(fileName)) continue;

                    var stem = fileName.Substring(0, fileName.Length - ext.Length);
                    var relDir = Path.GetDirectoryName(file)!.Substring(sectionDir.Length).Trim(Path.DirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/');
                    if (kind.FixedName != null && (relDir != "" || !string.Equals(stem, kind.FixedName, StringComparison.OrdinalIgnoreCase))) continue;

                    var name = kind.FixedName == null ? stem : "";
                    if (manifest.Find(device, kind.Name, name) == null && !found.Any(i => i.Is(device, kind.Name, name)))
                        found.Add(new ImportListItem(device, kind.Name, groupOf.TryGetValue(ItemDir(device, kind.Name, relDir), out var groupPath) ? groupPath : relDir, name, true));
                }
            }
        }
        return found;
    }

    // Items to write that were changed in TIA Portal since read_source_tree - their export now
    // differs from what the manifest recorded - or that are new in the tree but already exist in
    // the project. Writing them would discard what was done in TIA Portal.
    private async Task<List<(ImportListItem Item, string Reason)>> FindConflicts(SourceTreeManifest manifest, List<ImportListItem> items)
    {
        var (current, _) = await ExportItems((device, kind, _, name) => items.Any(i => i.Is(device, kind, name)) ? Array.Empty<string>() : null, null, manifest);
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
            if (now.Count != then.Count || then.Any(f => !now.TryGetValue(f.Key, out var hash) || hash != f.Value.Hash))
                conflicts.Add((item, "changed in TIA Portal since read_source_tree. Read it again (read_source_tree with items; this overwrites the file, so keep a copy of your edit), merge, and write again - or pass force=true to overwrite the project's version."));
        }
        return conflicts;
    }

    // Parses an item list - _export_summary.txt or list_project output, or a trimmed copy/excerpt of
    // either - into importable items; see write_source_tree. The status column is optional (a line
    // without one counts as OK). Unrecognized lines (headers, blank lines, the trailing 'Done: ...'
    // line, empty groups) are silently skipped, so callers don't need to strip them out first.
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

            var itemMatch = SourceKinds.ListedItem.Match(trimmed);
            if (!itemMatch.Success || currentDevice == null) continue;

            var ok = itemMatch.Groups["status"].Value is "" or "OK";
            var label = itemMatch.Groups["label"].Value;
            var slash = label.LastIndexOf('/');
            var groupPath = slash >= 0 ? label[..slash] : "";
            var itemName = slash >= 0 ? label[(slash + 1)..] : label;

            items.Add(new ImportListItem(currentDevice, itemMatch.Groups["kind"].Value, groupPath, itemName, ok));
        }

        return items;
    }

    private const string ItemListExample =
        "Expected lines as list_project or _export_summary.txt show them, under their software's header, e.g.:\n" +
        "=== PLC_1 ===\n  block 'Group/Main'\n  OK     udt 'Types/UDT_Motor'\n=== HMI_1 (HMI) ===\n  HMI tag table 'Tags'\n  HMI DiscreteAlarms";

    // Exports every item include (device, kind, group path, name) selects - all of them when it's
    // null - from every PLC and HMI, or only deviceName's. Also returns the devices it went
    // through, so a caller can tell which manifest items it would have seen if they still existed.
    // formats (device, kind, group path, name) returns null to skip an item, or the formats to
    // read it in; an empty list (and no formats function) means the formats the tree already has
    // it in (manifest), else its own format. Formats a kind doesn't have are left out.
    private async Task<(List<ExportedItem> Items, List<string> Devices)> ExportItems(Func<string, string, string, string, IReadOnlyCollection<string>?>? formats, string? deviceName, SourceTreeManifest? manifest)
    {
        var items = new List<ExportedItem>();
        var devices = await Devices(deviceName);
        _exportNotes.Clear();
        foreach (var (family, device) in devices)
        {
            foreach (var kind in SourceKinds.Of(family))
            {
                IReadOnlyCollection<string>? Formats(string groupPath, string name)
                {
                    var requested = formats == null ? Array.Empty<string>() : formats(device, kind.Name, groupPath, name);
                    if (requested == null) return null;
                    if (requested.Count == 0)
                        requested = manifest?.Find(device, kind.Name, name)?.WritableFiles.Select(f => f.Value.Format).Distinct().ToList() ?? new List<string>();
                    return requested.Where(kind.Formats.Contains).ToList();
                }
                await kind.Export(_session, device, Formats, items);
            }

            if (family == DeviceFamily.HmiClassic && formats == null)
            {
                var graphicLists = await _session.CountHmiClassicGraphicListsAsync(device);
                if (graphicLists > 0)
                    _exportNotes.Add($"SKIPPED {graphicLists} graphic list(s) of '{device}' - exporting a classic graphic list crashes TIA Portal V21 (InvalidCastException in UpdateOpenLinks)");
            }
        }

        return (items, devices.Select(d => d.Name).ToList());
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
            var entry = new SourceTreeItem { Device = item.Device, Kind = item.Kind, GroupPath = item.GroupPath, Name = item.Name, Formats = item.Formats.ToList() };
            if (!item.Ok)
            {
                entry.Ok = false;
                entry.Error = item.Error;
                entry.Formats = previous?.Formats ?? new List<string>();
                entry.Stale = MarkStale(dir, stem);
                manifest.Set(entry);
                continue;
            }

            // Files of an earlier read this one no longer produces - e.g. a .s7res whose last
            // comment was removed, or everything at the old place of an item moved to another group.
            if (previous != null)
            {
                foreach (var old in previous.Files.Keys.Where(k => !item.Files.Any(f => string.Equals(f.Path, k, StringComparison.OrdinalIgnoreCase))))
                {
                    DeleteFile(Path.Combine(root, old));
                    DeleteFile(BasePath(root, old));
                }
            }

            Directory.CreateDirectory(dir);
            ClearStale(dir, stem);
            foreach (var file in item.Files)
            {
                File.WriteAllText(Path.Combine(root, file.Path), file.Content, new UTF8Encoding(file.Bom));
                entry.Files[file.Path] = new SourceTreeFile { Hash = SourceTreeManifest.Hash(file.Content), Format = SourceTreeManifest.FormatOf(file.Path) };
                if (KeepsBase(file.Path)) WriteBase(root, file.Path, file.Content);
            }
            manifest.Set(entry);
        }
    }

    // An item a read didn't find in the project any more (deleted or renamed in TIA Portal):
    // its files go too, or write_source_tree would later create it again as a new item.
    private static void RemoveFromTree(string root, SourceTreeManifest manifest, SourceTreeItem item)
    {
        foreach (var file in item.Files.Keys)
        {
            DeleteFile(Path.Combine(root, file));
            DeleteFile(BasePath(root, file));
        }
        manifest.Items.Remove(item);
    }

    private static void DeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort - a leftover file is reported by the next write_source_tree */ }
    }

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
            foreach (var item in device.OrderBy(i => SourceKinds.ListIndex(i.Kind)).ThenBy(i => Label(i.GroupPath, i.Name), StringComparer.OrdinalIgnoreCase))
            {
                if (item.Ok) { summary.AppendLine($"  OK     {Describe(item.Kind, item.GroupPath, item.Name)}{FormatList(item.Files.Values.Select(f => f.Format).Distinct().ToList())}"); continue; }
                summary.AppendLine($"  FAILED {Describe(item.Kind, item.GroupPath, item.Name)}: {item.Error}");
                if (item.Stale) summary.AppendLine($"  STALE  {Describe(item.Kind, item.GroupPath, item.Name)} - previous export left on disk with a '.stale' suffix, does not reflect current state");
            }
            summary.AppendLine();
        }

        summary.AppendLine($"Done: {manifest.Items.Count(i => i.Ok)} exported, {manifest.Items.Count(i => !i.Ok)} failed, {manifest.Items.Count(i => i.Stale)} marked stale.");
        return summary.ToString();
    }

    // read_source_tree's items: 'Name', 'group/path/Name', or 'group/path/' for everything below a
    // group, case-insensitive. A fixed-file kind (the HMI alarm files) is selected by its file name
    // ('DiscreteAlarms') or its section ('HMI alarms/'). A line as list_project or
    // _export_summary.txt shows an item ("block 'group/Name' [SCL] [s7dcl, xml]", "OK     udt
    // 'Name'", "HMI DiscreteAlarms") selects that item, in the formats at its end if any. Returns
    // null when the item isn't selected, else the requested formats - empty for the default.
    private static IReadOnlyCollection<string>? RequestedFormats(IReadOnlyList<string> selection, string kind, string groupPath, string name)
    {
        foreach (var raw in selection)
        {
            var line = SourceKinds.ListedItem.Match(raw.Trim());
            if (line.Success && line.Groups["kind"].Value == kind
                && (SourceKinds.IsFixed(kind) || string.Equals(line.Groups["label"].Value, Label(groupPath, name), StringComparison.OrdinalIgnoreCase)))
                return FormatsInLine(raw);
        }

        if (SourceKinds.IsFixed(kind)) (groupPath, name) = (SectionOf(kind), ItemStem(kind, name));
        var label = Label(groupPath, name);
        foreach (var raw in selection)
        {
            var entry = raw.Trim().Replace('\\', '/');
            if (SourceKinds.ListedItem.IsMatch(entry)) continue;
            if (entry.EndsWith("/"))
            {
                var group = entry.TrimEnd('/');
                if (string.Equals(groupPath, group, StringComparison.OrdinalIgnoreCase) || groupPath.StartsWith(group + "/", StringComparison.OrdinalIgnoreCase)) return Array.Empty<string>();
            }
            else if (string.Equals(entry, entry.Contains("/") ? label : name, StringComparison.OrdinalIgnoreCase))
            {
                return Array.Empty<string>();
            }
        }
        return null;
    }

    // The formats of an item line: its last bracket that holds only format names, e.g. the
    // '[csv, xml]' in "tag table 'Tags' [csv, xml]" - not the '[SCL]' of a block.
    private static IReadOnlyCollection<string> FormatsInLine(string line)
    {
        foreach (Match bracket in Regex.Matches(line, @"\[([^\]]*)\]").Cast<Match>().Reverse())
        {
            var names = bracket.Groups[1].Value.Split(',').Select(f => f.Trim().ToLowerInvariant()).ToList();
            if (names.All(SourceFormat.All.Contains)) return names;
        }
        return Array.Empty<string>();
    }

    // ' [csv, xml]' after an item line, or nothing for a line without formats.
    private static string FormatList(IReadOnlyCollection<string> formats) =>
        formats.Count == 0 ? "" : $" [{string.Join(", ", formats)}]";

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

}
