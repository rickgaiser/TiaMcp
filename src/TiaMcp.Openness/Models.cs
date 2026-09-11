using System;
using System.Collections.Generic;

namespace TiaMcp.Openness;

public sealed record DeviceSummary(string DeviceName, string ItemName, string? PlcSoftwareName);

public sealed record BlockSummary(string PlcName, string GroupPath, string Name, string Language, bool Consistent);

public sealed record BlockDocument(string FileName, string Content);

public sealed record ExportResult(bool Success, IReadOnlyList<BlockDocument> Documents, string? Error);

public sealed record ImportResult(bool Success, IReadOnlyList<string> Messages);

public sealed record CompileResult(string State, int ErrorCount, int WarningCount, IReadOnlyList<string> Messages);

public sealed record ConnectResult(bool Success, string? ProjectName, string? ProjectPath, string? Error);

public sealed record TiaInstanceInfo(int ProcessId, string? WindowTitle);

public sealed record TagTableSummary(string PlcName, string GroupPath, string Name);

public sealed record TypeSummary(string PlcName, string GroupPath, string Name);

public sealed record TagInfo(string Name, string DataType, string LogicalAddress, string? Comment);

public sealed record TagTableResult(bool Success, IReadOnlyList<TagInfo> Tags, string? Error);

public sealed record TagSpec(string Name, string DataType, string? LogicalAddress);

public sealed record WriteTagsResult(bool Success, IReadOnlyList<string> Messages);

public sealed record SimpleResult(bool Success, string? Error);

public sealed record ProjectStatus(string Name, string? Path, bool IsModified, string Author, DateTime LastModified, string LastModifiedBy, string Version);

public sealed record HmiDeviceSummary(string DeviceName, string ItemName, string HmiSoftwareName);

public sealed record HmiTagTableSummary(string HmiName, string GroupPath, string Name);

public sealed record HmiTagInfo(string Name, string DataType, string? Address, string? Connection, string? PlcName, string? PlcTag, string? Comment);

public sealed record HmiTagTableResult(bool Success, IReadOnlyList<HmiTagInfo> Tags, string? Error);

public sealed record HmiTagSpec(string Name, string DataType, string? Address, string? Connection, string? PlcName, string? PlcTag);

public sealed record HmiAlarmClassInfo(string Name, int Priority, string? Log, int Id, bool IsSystem);

public sealed record HmiAlarmClassSpec(string Name, int? Priority, string? Log);

public sealed record HmiAlarmInfo(
    string Type,
    string Name,
    string? AlarmClass,
    string? EventText,
    string? InfoText,
    string? TriggerAddress,
    string? Condition,
    string? ConditionValue,
    string? RaisedStateTag,
    string? AuditClass,
    string? Area,
    string? Origin);

public sealed record HmiAlarmSpec(
    string Type,
    string Name,
    string? AlarmClass,
    string? EventText,
    string? InfoText,
    string? TriggerAddress,
    string? Condition,
    string? ConditionValue,
    string? RaisedStateTag,
    string? AuditClass,
    string? Area,
    string? Origin);

public sealed record HmiAlarmResult(bool Success, IReadOnlyList<HmiAlarmInfo> Alarms, string? Error);
