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

    public async Task<ConnectResult> ConnectAsync(string? projectPath = null, int? processId = null)
    {
        var result = await _sta.InvokeAsync(() => _connection.Connect(projectPath, processId));
        if (result.Success)
        {
            ProjectName = result.ProjectName;
            ProjectPath = result.ProjectPath;
        }
        return result;
    }

    // Plain OS process enumeration, not an Openness call - doesn't need the STA/Openness
    // dispatcher, and deliberately doesn't attach to anything (see TiaConnection.ListRunningInstances).
    public Task<IReadOnlyList<TiaInstanceInfo>> ListInstancesAsync()
    {
        return Task.FromResult(TiaConnection.ListRunningInstances());
    }

    public async Task<ConnectResult> SaveProjectAsAsync(string targetDirectory)
    {
        var result = await _sta.InvokeAsync(() => _connection.SaveProjectAs(targetDirectory));
        if (result.Success)
        {
            ProjectName = result.ProjectName;
            ProjectPath = result.ProjectPath;
        }
        return result;
    }

    public Task<SimpleResult> SaveProjectAsync()
    {
        return _sta.InvokeAsync(() => _connection.SaveProject());
    }

    public async Task<SimpleResult> CloseProjectAsync()
    {
        var result = await _sta.InvokeAsync(() => _connection.CloseProject());
        if (result.Success)
        {
            ProjectName = null;
            ProjectPath = null;
        }
        return result;
    }

    public Task<ProjectStatus> GetProjectStatusAsync()
    {
        return _sta.InvokeAsync(() => _connection.GetProjectStatus());
    }

    public Task<IReadOnlyList<DeviceSummary>> ListDevicesAsync()
    {
        return _sta.InvokeAsync(() => _connection.ListDevices());
    }

    public Task<IReadOnlyList<BlockSummary>> ListBlocksAsync(string plcName)
    {
        return _sta.InvokeAsync(() => _connection.ListBlocks(plcName));
    }

    public Task<ExportResult> ReadBlockAsync(string plcName, string blockName)
    {
        return _sta.InvokeAsync(() => _connection.ReadBlock(plcName, blockName));
    }

    public Task<IReadOnlyList<TypeSummary>> ListPlcTypesAsync(string plcName)
    {
        return _sta.InvokeAsync(() => _connection.ListPlcTypes(plcName));
    }

    public Task<ExportResult> ReadUdtAsync(string plcName, string typeName)
    {
        return _sta.InvokeAsync(() => _connection.ReadUdt(plcName, typeName));
    }

    public Task<ImportResult> WriteUdtAsync(string plcName, string typeName, IReadOnlyList<BlockDocument> documents)
    {
        return _sta.InvokeAsync(() => _connection.WriteUdt(plcName, typeName, documents));
    }

    public Task<ImportResult> CreateUdtAsync(string plcName, string groupPath, string typeName, IReadOnlyList<BlockDocument> documents)
    {
        return _sta.InvokeAsync(() => _connection.CreateUdt(plcName, groupPath, typeName, documents));
    }

    public Task<SimpleResult> DeleteUdtAsync(string plcName, string typeName)
    {
        return _sta.InvokeAsync(() => _connection.DeleteUdt(plcName, typeName));
    }

    public Task<SimpleResult> RenameUdtAsync(string plcName, string typeName, string newName)
    {
        return _sta.InvokeAsync(() => _connection.RenameUdt(plcName, typeName, newName));
    }

    public Task<IReadOnlyList<TagTableSummary>> ListTagTablesAsync(string plcName)
    {
        return _sta.InvokeAsync(() => _connection.ListTagTables(plcName));
    }

    public Task<TagTableResult> ReadTagTableAsync(string plcName, string tableName)
    {
        return _sta.InvokeAsync(() => _connection.ReadTagTable(plcName, tableName));
    }

    public Task<ImportResult> WriteBlockAsync(string plcName, string blockName, IReadOnlyList<BlockDocument> documents)
    {
        return _sta.InvokeAsync(() => _connection.WriteBlock(plcName, blockName, documents));
    }

    public Task<WriteTagsResult> WriteTagTableAsync(string plcName, string tableName, IReadOnlyList<TagSpec> tags)
    {
        return _sta.InvokeAsync(() => _connection.WriteTagTable(plcName, tableName, tags));
    }

    public Task<SimpleResult> CreateTagTableAsync(string plcName, string groupPath, string tableName)
    {
        return _sta.InvokeAsync(() => _connection.CreateTagTable(plcName, groupPath, tableName));
    }

    public Task<SimpleResult> DeleteTagTableAsync(string plcName, string tableName)
    {
        return _sta.InvokeAsync(() => _connection.DeleteTagTable(plcName, tableName));
    }

    public Task<SimpleResult> RenameTagTableAsync(string plcName, string tableName, string newName)
    {
        return _sta.InvokeAsync(() => _connection.RenameTagTable(plcName, tableName, newName));
    }

    public Task<ImportResult> CreateBlockAsync(string plcName, string groupPath, string blockName, IReadOnlyList<BlockDocument> documents)
    {
        return _sta.InvokeAsync(() => _connection.CreateBlock(plcName, groupPath, blockName, documents));
    }

    public Task<SimpleResult> CreateInstanceDbAsync(string plcName, string groupPath, string dbName, string instanceOfFbName)
    {
        return _sta.InvokeAsync(() => _connection.CreateInstanceDb(plcName, groupPath, dbName, instanceOfFbName));
    }

    public Task<ImportResult> CreateGraphBlockAsync(string plcName, string groupPath, string blockName, string templateBlockName, string ilContent)
    {
        return _sta.InvokeAsync(() => _connection.CreateGraphBlock(plcName, groupPath, blockName, templateBlockName, ilContent));
    }

    public Task<ImportResult> CreateStlBlockAsync(string plcName, string groupPath, string blockName, IReadOnlyList<BlockDocument> documents)
    {
        return _sta.InvokeAsync(() => _connection.CreateStlBlock(plcName, groupPath, blockName, documents));
    }

    public Task<SimpleResult> CreateBlockGroupAsync(string plcName, string parentGroupPath, string groupName)
    {
        return _sta.InvokeAsync(() => _connection.CreateBlockGroup(plcName, parentGroupPath, groupName));
    }

    public Task<SimpleResult> DeleteBlockGroupAsync(string plcName, string groupPath)
    {
        return _sta.InvokeAsync(() => _connection.DeleteBlockGroup(plcName, groupPath));
    }

    public Task<SimpleResult> RenamePlcGroupAsync(string plcName, string groupPath, string newName)
    {
        return _sta.InvokeAsync(() => _connection.RenamePlcGroup(plcName, groupPath, newName));
    }

    public Task<SimpleResult> DeleteBlockAsync(string plcName, string blockName)
    {
        return _sta.InvokeAsync(() => _connection.DeleteBlock(plcName, blockName));
    }

    public Task<SimpleResult> RenamePlcBlockAsync(string plcName, string blockName, string newName)
    {
        return _sta.InvokeAsync(() => _connection.RenamePlcBlock(plcName, blockName, newName));
    }

    public Task<SimpleResult> CreateTypeGroupAsync(string plcName, string parentGroupPath, string groupName)
    {
        return _sta.InvokeAsync(() => _connection.CreateTypeGroup(plcName, parentGroupPath, groupName));
    }

    public Task<SimpleResult> DeleteTypeGroupAsync(string plcName, string groupPath)
    {
        return _sta.InvokeAsync(() => _connection.DeleteTypeGroup(plcName, groupPath));
    }

    public Task<SimpleResult> RenameTypeGroupAsync(string plcName, string groupPath, string newName)
    {
        return _sta.InvokeAsync(() => _connection.RenameTypeGroup(plcName, groupPath, newName));
    }

    public Task<SimpleResult> CreateTagTableGroupAsync(string plcName, string parentGroupPath, string groupName)
    {
        return _sta.InvokeAsync(() => _connection.CreateTagTableGroup(plcName, parentGroupPath, groupName));
    }

    public Task<SimpleResult> DeleteTagTableGroupAsync(string plcName, string groupPath)
    {
        return _sta.InvokeAsync(() => _connection.DeleteTagTableGroup(plcName, groupPath));
    }

    public Task<SimpleResult> RenameTagTableGroupAsync(string plcName, string groupPath, string newName)
    {
        return _sta.InvokeAsync(() => _connection.RenameTagTableGroup(plcName, groupPath, newName));
    }

    public Task<SimpleResult> CreateHmiTagTableGroupAsync(string hmiName, string parentGroupPath, string groupName)
    {
        return _sta.InvokeAsync(() => _connection.CreateHmiTagTableGroup(hmiName, parentGroupPath, groupName));
    }

    public Task<SimpleResult> DeleteHmiTagTableGroupAsync(string hmiName, string groupPath)
    {
        return _sta.InvokeAsync(() => _connection.DeleteHmiTagTableGroup(hmiName, groupPath));
    }

    public Task<SimpleResult> RenameHmiTagTableGroupAsync(string hmiName, string groupPath, string newName)
    {
        return _sta.InvokeAsync(() => _connection.RenameHmiTagTableGroup(hmiName, groupPath, newName));
    }

    public Task<CompileResult> CompileAsync(string plcName)
    {
        return _sta.InvokeAsync(() => _connection.Compile(plcName));
    }

    public Task<IReadOnlyList<HmiDeviceSummary>> ListHmiDevicesAsync()
    {
        return _sta.InvokeAsync(() => _connection.ListHmiDevices());
    }

    public Task<IReadOnlyList<HmiTagTableSummary>> ListHmiTagTablesAsync(string hmiName)
    {
        return _sta.InvokeAsync(() => _connection.ListHmiTagTables(hmiName));
    }

    public Task<HmiTagTableResult> ReadHmiTagTableAsync(string hmiName, string tableName)
    {
        return _sta.InvokeAsync(() => _connection.ReadHmiTagTable(hmiName, tableName));
    }

    public Task<WriteTagsResult> WriteHmiTagTableAsync(string hmiName, string tableName, IReadOnlyList<HmiTagSpec> tags)
    {
        return _sta.InvokeAsync(() => _connection.WriteHmiTagTable(hmiName, tableName, tags));
    }

    public Task<SimpleResult> CreateHmiTagTableAsync(string hmiName, string groupPath, string tableName)
    {
        return _sta.InvokeAsync(() => _connection.CreateHmiTagTable(hmiName, groupPath, tableName));
    }

    public Task<SimpleResult> DeleteHmiTagTableAsync(string hmiName, string tableName)
    {
        return _sta.InvokeAsync(() => _connection.DeleteHmiTagTable(hmiName, tableName));
    }

    public Task<SimpleResult> RenameHmiTagTableAsync(string hmiName, string tableName, string newName)
    {
        return _sta.InvokeAsync(() => _connection.RenameHmiTagTable(hmiName, tableName, newName));
    }

    public Task<IReadOnlyList<HmiAlarmClassInfo>> ListHmiAlarmClassesAsync(string hmiName)
    {
        return _sta.InvokeAsync(() => _connection.ListHmiAlarmClasses(hmiName));
    }

    public Task<SimpleResult> WriteHmiAlarmClassAsync(string hmiName, HmiAlarmClassSpec spec)
    {
        return _sta.InvokeAsync(() => _connection.WriteHmiAlarmClass(hmiName, spec));
    }

    public Task<SimpleResult> DeleteHmiAlarmClassAsync(string hmiName, string name)
    {
        return _sta.InvokeAsync(() => _connection.DeleteHmiAlarmClass(hmiName, name));
    }

    public Task<IReadOnlyList<HmiAlarmInfo>> ListHmiAlarmsAsync(string hmiName, string? type = null)
    {
        return _sta.InvokeAsync(() => _connection.ListHmiAlarms(hmiName, type));
    }

    public Task<HmiAlarmInfo?> ReadHmiAlarmAsync(string hmiName, string type, string alarmName)
    {
        return _sta.InvokeAsync(() => _connection.ReadHmiAlarm(hmiName, type, alarmName));
    }

    public Task<SimpleResult> WriteHmiAlarmAsync(string hmiName, HmiAlarmSpec spec)
    {
        return _sta.InvokeAsync(() => _connection.WriteHmiAlarm(hmiName, spec));
    }

    public Task<SimpleResult> DeleteHmiAlarmAsync(string hmiName, string type, string alarmName)
    {
        return _sta.InvokeAsync(() => _connection.DeleteHmiAlarm(hmiName, type, alarmName));
    }

    public async Task<(IReadOnlyList<BlockSummary> Blocks, IReadOnlyList<TagTableSummary> TagTables, IReadOnlyList<TypeSummary> Types)> SearchAsync(string plcName, string text)
    {
        var blocks = await ListBlocksAsync(plcName);
        var tables = await ListTagTablesAsync(plcName);
        var types = await ListPlcTypesAsync(plcName);
        return (
            blocks.Where(b => b.Name.IndexOf(text, System.StringComparison.OrdinalIgnoreCase) >= 0).ToList(),
            tables.Where(t => t.Name.IndexOf(text, System.StringComparison.OrdinalIgnoreCase) >= 0).ToList(),
            types.Where(t => t.Name.IndexOf(text, System.StringComparison.OrdinalIgnoreCase) >= 0).ToList());
    }

    public string? ProjectName { get; private set; }
    public string? ProjectPath { get; private set; }
    public bool IsConnected => _connection.IsConnected;
}
