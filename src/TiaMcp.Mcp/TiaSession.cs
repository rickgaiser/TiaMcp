using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TiaMcp.Openness;

namespace TiaMcp.Mcp;

/// <summary>
/// Session-scoped singleton tying the STA dispatcher and the live Openness connection
/// together. MCP tool classes call into this rather than touching
/// TiaConnection/StaDispatcher directly.
/// </summary>
public sealed class TiaSession
{
    private readonly StaDispatcher _sta = new();
    private readonly TiaConnection _connection = new();

    // The project tia_connect last attached to, to attach to again when TIA Portal closed and
    // reopened it (a Multiuser check-in or refresh, another Openness client) or this server was
    // restarted. Kept on disk per working directory, as each client session starts its own server.
    private (int? ProcessId, string Path)? _target = LoadTarget();

    public async Task<ConnectResult> ConnectAsync(string? projectPath = null, int? processId = null, bool startPortal = false)
    {
        var result = await _sta.InvokeAsync(() => _connection.Connect(projectPath, processId, startPortal));
        if (result.Success) Connected(result);
        return result;
    }

    private void Connected(ConnectResult result)
    {
        ProjectName = result.ProjectName;
        ProjectPath = result.ProjectPath;
        _target = result.ProjectPath == null ? null : (_connection.ProcessId, result.ProjectPath);
        SaveTarget(_target);
    }

    // Before a tool runs: attaches again when the project handle died or this server was
    // restarted since tia_connect. Returns a note for the tool's result when it did or couldn't;
    // null when nothing was needed.
    public async Task<string?> RestoreAsync()
    {
        if (_target is not { } target) return null;
        var wasConnected = _connection.IsConnected;
        if (wasConnected && await _sta.InvokeAsync(() => _connection.ProjectAlive())) return null;

        var result = await _sta.InvokeAsync(() => _connection.Reconnect(target.ProcessId, target.Path));
        if (result.Success)
        {
            Connected(result);
            return wasConnected
                ? $"(Reconnected to '{result.ProjectName}': TIA Portal had closed and reopened it - a Multiuser check-in or refresh, or another Openness client.)"
                : $"(Connected to '{result.ProjectName}' again, as before this server restarted.)";
        }
        ProjectName = null;
        return wasConnected
            ? $"The connection to '{target.Path}' was lost (TIA Portal closed it) and could not be restored: {result.Error} Call tia_connect."
            : null;
    }

    // A call that failed because TIA Portal closed the project while it ran: a read is attached
    // again and repeated; a change is not repeated, as part of it may have been done.
    private async Task<T> Run<T>(Func<T> call, bool repeat)
    {
        try
        {
            return await _sta.InvokeAsync(call);
        }
        catch (Exception ex) when (_target is { } target && IsLostProject(ex))
        {
            var result = await _sta.InvokeAsync(() => _connection.Reconnect(target.ProcessId, target.Path));
            if (!result.Success)
                throw new InvalidOperationException($"TIA Portal closed the project while this call ran, and it could not be attached again: {result.Error} Call tia_connect.", ex);
            Connected(result);
            if (!repeat)
                throw new InvalidOperationException($"TIA Portal closed and reopened the project while this call ran (a Multiuser check-in or refresh, or another Openness client); reconnected to '{result.ProjectName}'. Part of the change may have been made: check, then call it again.", ex);
            return await _sta.InvokeAsync(call);
        }
    }

    private Task<T> Read<T>(Func<T> call) => Run(call, repeat: true);
    private Task<T> Change<T>(Func<T> call) => Run(call, repeat: false);

    private static bool IsLostProject(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e.GetType().Name == "EngineeringObjectDisposedException" || e.Message.Contains("Access to a disposed object")) return true;
        return false;
    }

    private static string TargetFile()
    {
        var dir = Environment.CurrentDirectory.ToLowerInvariant();
        using var sha = System.Security.Cryptography.SHA1.Create();
        var hash = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(dir))).Replace("-", "").Substring(0, 12);
        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TiaMcp", $"connection-{hash}.txt");
    }

    // Line 1: process id (may be empty), line 2: project path.
    private static (int? ProcessId, string Path)? LoadTarget()
    {
        try
        {
            var lines = System.IO.File.ReadAllLines(TargetFile());
            if (lines.Length < 2 || lines[1].Length == 0) return null;
            return (int.TryParse(lines[0], out var pid) ? pid : null, lines[1]);
        }
        catch
        {
            return null;
        }
    }

    private static void SaveTarget((int? ProcessId, string Path)? target)
    {
        try
        {
            var file = TargetFile();
            if (target is not { } t) { System.IO.File.Delete(file); return; }
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            System.IO.File.WriteAllLines(file, new[] { t.ProcessId?.ToString() ?? "", t.Path });
        }
        catch
        {
            // Only a convenience: without it a restarted server waits for tia_connect.
        }
    }

    // Doesn't attach to anything (see TiaConnection.ListRunningInstances).
    public Task<IReadOnlyList<TiaInstanceInfo>> ListInstancesAsync()
    {
        return _sta.InvokeAsync(() => _connection.ListRunningInstances());
    }

    public async Task<ConnectResult> SaveProjectAsAsync(string targetDirectory)
    {
        var result = await _sta.InvokeAsync(() => _connection.SaveProjectAs(targetDirectory));
        if (result.Success) Connected(result);
        return result;
    }

    public Task<SimpleResult> SaveProjectAsync()
    {
        return Change(() => _connection.SaveProject());
    }

    public async Task<SimpleResult> CloseProjectAsync()
    {
        try
        {
            return await _sta.InvokeAsync(() => _connection.CloseProject());
        }
        finally
        {
            if (!_connection.IsConnected)
            {
                ProjectName = null;
                ProjectPath = null;
                // Closed on purpose: not to be attached again.
                _target = null;
                SaveTarget(null);
            }
        }
    }

    public Task<ProjectStatus> GetProjectStatusAsync()
    {
        return Read(() => _connection.GetProjectStatus());
    }

    public bool IsMultiuser => _connection.IsMultiuser;

    public Task<IReadOnlyDictionary<string, MarkSummary>?> GetMarkStatesAsync(string device, MarkTarget target, IReadOnlyCollection<string> names)
    {
        return Read(() => _connection.GetMarkStates(device, target, names));
    }

    public Task<IReadOnlyList<ProjectSoftware>> ListSoftwareAsync()
    {
        return Read(() => _connection.ListSoftware());
    }

    public Task<IReadOnlyList<BlockSummary>> ListBlocksAsync(string plcName)
    {
        return Read(() => _connection.ListBlocks(plcName));
    }

    public Task<IReadOnlyList<(BlockSummary Block, ExportResult Result)>> ReadAllBlocksAsync(string plcName, Func<string, string, IReadOnlyCollection<string>?>? formats = null, ExportingItem? exporting = null)
    {
        return Read(() => _connection.ReadAllBlocks(plcName, formats, exporting));
    }

    public Task<IReadOnlyList<(TypeSummary Type, ExportResult Result)>> ReadAllUdtsAsync(string plcName, Func<string, string, IReadOnlyCollection<string>?>? formats = null, ExportingItem? exporting = null)
    {
        return Read(() => _connection.ReadAllUdts(plcName, formats, exporting));
    }

    public Task<IReadOnlyList<(TagTableSummary Table, ExportResult Result)>> ReadAllTagTablesAsync(string plcName, Func<string, string, IReadOnlyCollection<string>?>? formats = null, ExportingItem? exporting = null)
    {
        return Read(() => _connection.ReadAllTagTables(plcName, formats, exporting));
    }

    public Task<ImportResult> ImportBlockXmlAsync(string plcName, string groupPath, string blockName, string xml)
    {
        return Change(() => _connection.ImportBlockXml(plcName, groupPath, blockName, xml));
    }

    public Task<ImportResult> ImportUdtXmlAsync(string plcName, string groupPath, string typeName, string xml)
    {
        return Change(() => _connection.ImportUdtXml(plcName, groupPath, typeName, xml));
    }

    public Task<ImportResult> ImportTagTableXmlAsync(string plcName, string groupPath, string tableName, string xml)
    {
        return Change(() => _connection.ImportTagTableXml(plcName, groupPath, tableName, xml));
    }

    public Task<IReadOnlyList<TypeSummary>> ListPlcTypesAsync(string plcName)
    {
        return Read(() => _connection.ListPlcTypes(plcName));
    }

    public Task<ImportResult> WriteUdtAsync(string plcName, string typeName, IReadOnlyList<BlockDocument> documents)
    {
        return Change(() => _connection.WriteUdt(plcName, typeName, documents));
    }

    public Task<ImportResult> CreateUdtAsync(string plcName, string groupPath, string typeName, IReadOnlyList<BlockDocument> documents)
    {
        return Change(() => _connection.CreateUdt(plcName, groupPath, typeName, documents));
    }

    public Task<SimpleResult> DeleteUdtAsync(string plcName, string typeName)
    {
        return Change(() => _connection.DeleteUdt(plcName, typeName));
    }

    public Task<SimpleResult> RenameUdtAsync(string plcName, string typeName, string newName)
    {
        return Change(() => _connection.RenameUdt(plcName, typeName, newName));
    }

    public Task<IReadOnlyList<TagTableSummary>> ListTagTablesAsync(string plcName)
    {
        return Read(() => _connection.ListTagTables(plcName));
    }

    public Task<IReadOnlyList<string>> ListTagTableGroupsAsync(string plcName)
    {
        return Read(() => _connection.ListTagTableGroups(plcName));
    }

    public Task<int?> ExportsSinceImportAsync(string plcName, string blockName)
    {
        return Read(() => _connection.ExportsSinceImport(plcName, blockName));
    }

    public Task<ImportResult> WriteBlockAsync(string plcName, string blockName, IReadOnlyList<BlockDocument> documents, bool stlAtRead = false)
    {
        return Change(() => _connection.WriteBlock(plcName, blockName, documents, stlAtRead));
    }

    public Task<WriteTagsResult> WriteTagTableAsync(string plcName, string tableName, IReadOnlyList<TagSpec> tags, bool deleteMissing = false, bool dryRun = false)
    {
        return Change(() => _connection.WriteTagTable(plcName, tableName, tags, deleteMissing, dryRun));
    }

    public Task<SimpleResult> RenameTagAsync(string plcName, string tagName, string newName)
    {
        return Change(() => _connection.RenameTag(plcName, tagName, newName));
    }

    public Task<SimpleResult> CreateTagTableAsync(string plcName, string groupPath, string tableName)
    {
        return Change(() => _connection.CreateTagTable(plcName, groupPath, tableName));
    }

    public Task<SimpleResult> DeleteTagTableAsync(string plcName, string tableName)
    {
        return Change(() => _connection.DeleteTagTable(plcName, tableName));
    }

    public Task<SimpleResult> RenameTagTableAsync(string plcName, string tableName, string newName)
    {
        return Change(() => _connection.RenameTagTable(plcName, tableName, newName));
    }

    public Task<ImportResult> CreateBlockAsync(string plcName, string groupPath, string blockName, IReadOnlyList<BlockDocument> documents, bool checkExisting = true)
    {
        return Change(() => _connection.CreateBlock(plcName, groupPath, blockName, documents, checkExisting));
    }

    public Task<SimpleResult> CreateInstanceDbAsync(string plcName, string groupPath, string dbName, string instanceOfFbName)
    {
        return Change(() => _connection.CreateInstanceDb(plcName, groupPath, dbName, instanceOfFbName));
    }

    public Task<ImportResult> CreateGraphBlockAsync(string plcName, string groupPath, string blockName, string templateBlockName, string ilContent)
    {
        return Change(() => _connection.CreateGraphBlock(plcName, groupPath, blockName, templateBlockName, ilContent));
    }

    public Task<ImportResult> CreateStlBlockAsync(string plcName, string groupPath, string blockName, IReadOnlyList<BlockDocument> documents)
    {
        return Change(() => _connection.CreateStlBlock(plcName, groupPath, blockName, documents));
    }

    public Task<bool> BlockGroupExistsAsync(string plcName, string groupPath)
    {
        return Read(() => _connection.BlockGroupExists(plcName, groupPath));
    }

    public Task<SimpleResult> CreateBlockGroupAsync(string plcName, string parentGroupPath, string groupName)
    {
        return Change(() => _connection.CreateBlockGroup(plcName, parentGroupPath, groupName));
    }

    public Task<SimpleResult> DeleteBlockGroupAsync(string plcName, string groupPath)
    {
        return Change(() => _connection.DeleteBlockGroup(plcName, groupPath));
    }

    public Task<SimpleResult> RenamePlcGroupAsync(string plcName, string groupPath, string newName)
    {
        return Change(() => _connection.RenamePlcGroup(plcName, groupPath, newName));
    }

    public Task<SimpleResult> DeleteBlockAsync(string plcName, string blockName)
    {
        return Change(() => _connection.DeleteBlock(plcName, blockName));
    }

    public Task<SimpleResult> RenamePlcBlockAsync(string plcName, string blockName, string newName)
    {
        return Change(() => _connection.RenamePlcBlock(plcName, blockName, newName));
    }

    public Task<bool> TypeGroupExistsAsync(string plcName, string groupPath)
    {
        return Read(() => _connection.TypeGroupExists(plcName, groupPath));
    }

    public Task<SimpleResult> CreateTypeGroupAsync(string plcName, string parentGroupPath, string groupName)
    {
        return Change(() => _connection.CreateTypeGroup(plcName, parentGroupPath, groupName));
    }

    public Task<SimpleResult> DeleteTypeGroupAsync(string plcName, string groupPath)
    {
        return Change(() => _connection.DeleteTypeGroup(plcName, groupPath));
    }

    public Task<SimpleResult> RenameTypeGroupAsync(string plcName, string groupPath, string newName)
    {
        return Change(() => _connection.RenameTypeGroup(plcName, groupPath, newName));
    }

    public Task<bool> TagTableGroupExistsAsync(string plcName, string groupPath)
    {
        return Read(() => _connection.TagTableGroupExists(plcName, groupPath));
    }

    public Task<SimpleResult> CreateTagTableGroupAsync(string plcName, string parentGroupPath, string groupName)
    {
        return Change(() => _connection.CreateTagTableGroup(plcName, parentGroupPath, groupName));
    }

    public Task<SimpleResult> DeleteTagTableGroupAsync(string plcName, string groupPath)
    {
        return Change(() => _connection.DeleteTagTableGroup(plcName, groupPath));
    }

    public Task<SimpleResult> RenameTagTableGroupAsync(string plcName, string groupPath, string newName)
    {
        return Change(() => _connection.RenameTagTableGroup(plcName, groupPath, newName));
    }

    public Task<bool> HmiTagTableGroupExistsAsync(string hmiName, string groupPath)
    {
        return Read(() => _connection.HmiTagTableGroupExists(hmiName, groupPath));
    }

    public Task<SimpleResult> CreateHmiTagTableGroupAsync(string hmiName, string parentGroupPath, string groupName)
    {
        return Change(() => _connection.CreateHmiTagTableGroup(hmiName, parentGroupPath, groupName));
    }

    public Task<SimpleResult> DeleteHmiTagTableGroupAsync(string hmiName, string groupPath)
    {
        return Change(() => _connection.DeleteHmiTagTableGroup(hmiName, groupPath));
    }

    public Task<SimpleResult> RenameHmiTagTableGroupAsync(string hmiName, string groupPath, string newName)
    {
        return Change(() => _connection.RenameHmiTagTableGroup(hmiName, groupPath, newName));
    }

    public Task<CompileResult> CompileAsync(string softwareName)
    {
        return Change(() => _connection.Compile(softwareName));
    }

    public Task<IReadOnlyList<HmiClassicObjectSummary>> ListHmiClassicObjectsAsync(string hmiName)
    {
        return Read(() => _connection.ListHmiClassicObjects(hmiName));
    }

    public Task<IReadOnlyList<HmiClassicObjectXml>> ReadHmiClassicObjectsAsync(string hmiName, Func<string, string, string, bool>? include, ExportingItem? exporting = null)
    {
        return Read(() => _connection.ReadHmiClassicObjects(hmiName, include, exporting));
    }

    public Task<int> CountHmiClassicGraphicListsAsync(string hmiName)
    {
        return Read(() => _connection.CountHmiClassicGraphicLists(hmiName));
    }

    public Task<SimpleResult> WriteHmiClassicObjectAsync(string hmiName, string kind, string folderPath, string name, string xml, bool create)
    {
        return Change(() => _connection.WriteHmiClassicObject(hmiName, kind, folderPath, name, xml, create));
    }

    public Task<bool> ClearHmiClassicScreenNumbersAsync()
    {
        return Change(() => { _connection.ClearHmiClassicScreenNumbers(); return true; });
    }

    public Task<IReadOnlyList<string>> ListPlcAlarmTextListNamesAsync(string plcName)
    {
        return Read(() => _connection.ListPlcAlarmTextListNames(plcName));
    }

    public Task<PlcAlarmTextListsResult> ReadPlcAlarmTextListsAsync(string plcName)
    {
        return Read(() => _connection.ReadPlcAlarmTextLists(plcName));
    }

    public Task<PlcAlarmTextWriteResult> WritePlcAlarmTextListsAsync(string plcName, IReadOnlyList<PlcAlarmTextList> lists, bool deleteMissing, bool dryRun)
    {
        return Change(() => _connection.WritePlcAlarmTextLists(plcName, lists, deleteMissing, dryRun));
    }

    public Task<IReadOnlyList<HmiTagTableSummary>> ListHmiTagTablesAsync(string hmiName)
    {
        return Read(() => _connection.ListHmiTagTables(hmiName));
    }

    public Task<IReadOnlyList<string>> ListHmiTagTableGroupsAsync(string hmiName)
    {
        return Read(() => _connection.ListHmiTagTableGroups(hmiName));
    }

    public Task<HmiTagTableResult> ReadHmiTagTableAsync(string hmiName, string tableName, bool readLength = false)
    {
        return Read(() => _connection.ReadHmiTagTable(hmiName, tableName, readLength));
    }

    public Task<WriteTagsResult> WriteHmiTagTableAsync(string hmiName, string tableName, IReadOnlyList<HmiTagSpec> tags, bool deleteMissing = false, bool dryRun = false)
    {
        return Change(() => _connection.WriteHmiTagTable(hmiName, tableName, tags, deleteMissing, dryRun));
    }

    public Task<SimpleResult> RenameHmiTagAsync(string hmiName, string tagName, string newName)
    {
        return Change(() => _connection.RenameHmiTag(hmiName, tagName, newName));
    }

    public Task<SimpleResult> CreateHmiTagTableAsync(string hmiName, string groupPath, string tableName)
    {
        return Change(() => _connection.CreateHmiTagTable(hmiName, groupPath, tableName));
    }

    public Task<SimpleResult> DeleteHmiTagTableAsync(string hmiName, string tableName)
    {
        return Change(() => _connection.DeleteHmiTagTable(hmiName, tableName));
    }

    public Task<SimpleResult> RenameHmiTagTableAsync(string hmiName, string tableName, string newName)
    {
        return Change(() => _connection.RenameHmiTagTable(hmiName, tableName, newName));
    }

    public Task<IReadOnlyList<HmiAlarmClassInfo>> ListHmiAlarmClassesAsync(string hmiName)
    {
        return Read(() => _connection.ListHmiAlarmClasses(hmiName));
    }

    public Task<SimpleResult> WriteHmiAlarmClassAsync(string hmiName, HmiAlarmClassSpec spec)
    {
        return Change(() => _connection.WriteHmiAlarmClass(hmiName, spec));
    }

    public Task<SimpleResult> DeleteHmiAlarmClassAsync(string hmiName, string name)
    {
        return Change(() => _connection.DeleteHmiAlarmClass(hmiName, name));
    }

    public Task<IReadOnlyList<HmiAlarmInfo>> ListHmiAlarmsAsync(string hmiName, string? type = null, bool repair = true)
    {
        return Read(() => _connection.ListHmiAlarms(hmiName, type, repair));
    }

    public Task<SimpleResult> WriteHmiAlarmAsync(string hmiName, HmiAlarmSpec spec)
    {
        return Change(() => _connection.WriteHmiAlarm(hmiName, spec));
    }

    public Task<SimpleResult> DeleteHmiAlarmAsync(string hmiName, string type, string alarmName)
    {
        return Change(() => _connection.DeleteHmiAlarm(hmiName, type, alarmName));
    }

    public Task<IReadOnlyList<HmiScreenSummary>> ListHmiScreensAsync(string hmiName)
    {
        return Read(() => _connection.ListHmiScreens(hmiName));
    }

    public Task<IReadOnlyList<string>> ListHmiScreenGroupsAsync(string hmiName)
    {
        return Read(() => _connection.ListHmiScreenGroups(hmiName));
    }

    public Task<HmiScreenResult> ReadHmiScreenAsync(string hmiName, string screenName, bool includeDefaults = false)
    {
        return Read(() => _connection.ReadHmiScreen(hmiName, screenName, includeDefaults));
    }

    public Task<WriteHmiScreenResult> WriteHmiScreenAsync(string hmiName, string screenName, string json, bool deleteMissing = false, string? baseJson = null)
    {
        return Change(() => _connection.WriteHmiScreen(hmiName, screenName, json, deleteMissing, baseJson));
    }

    public Task<SimpleResult> CreateHmiScreenAsync(string hmiName, string groupPath, string screenName)
    {
        return Change(() => _connection.CreateHmiScreen(hmiName, groupPath, screenName));
    }

    public Task<SimpleResult> DeleteHmiScreenAsync(string hmiName, string screenName)
    {
        return Change(() => _connection.DeleteHmiScreen(hmiName, screenName));
    }

    public Task<SimpleResult> RenameHmiScreenAsync(string hmiName, string screenName, string newName)
    {
        return Change(() => _connection.RenameHmiScreen(hmiName, screenName, newName));
    }

    public Task<bool> HmiScreenGroupExistsAsync(string hmiName, string groupPath)
    {
        return Read(() => _connection.HmiScreenGroupExists(hmiName, groupPath));
    }

    public Task<SimpleResult> CreateHmiScreenGroupAsync(string hmiName, string parentGroupPath, string groupName)
    {
        return Change(() => _connection.CreateHmiScreenGroup(hmiName, parentGroupPath, groupName));
    }

    public Task<SimpleResult> DeleteHmiScreenGroupAsync(string hmiName, string groupPath)
    {
        return Change(() => _connection.DeleteHmiScreenGroup(hmiName, groupPath));
    }

    public Task<SimpleResult> RenameHmiScreenGroupAsync(string hmiName, string groupPath, string newName)
    {
        return Change(() => _connection.RenameHmiScreenGroup(hmiName, groupPath, newName));
    }

    public Task<IReadOnlyList<LibraryTypeSummary>> ListProjectLibraryTypesAsync(string? nameFilter)
    {
        return Read(() => _connection.ListProjectLibraryTypes(nameFilter));
    }

    public Task<FaceplateTypeResult> ReadFaceplateTypeAsync(string hmiName, string containedType)
    {
        return Read(() => _connection.ReadFaceplateType(hmiName, containedType));
    }

    public string? ProjectName { get; private set; }
    public string? ProjectPath { get; private set; }
    public bool IsConnected => _connection.IsConnected;
}
