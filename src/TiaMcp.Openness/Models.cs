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

public sealed record WriteTagsResult(bool Success, IReadOnlyList<string> Messages);

public sealed record SimpleResult(bool Success, string? Error);

public sealed record ProjectStatus(string Name, string? Path, bool IsModified, string Author, DateTime LastModified, string LastModifiedBy, string Version);

public sealed record HmiDeviceSummary(string DeviceName, string ItemName, string HmiSoftwareName);

public sealed record HmiTagTableSummary(string HmiName, string GroupPath, string Name);

public sealed record HmiTagTableResult(bool Success, IReadOnlyList<HmiTagInfo> Tags, string? Error);

public sealed record HmiAlarmResult(bool Success, IReadOnlyList<HmiAlarmInfo> Alarms, string? Error);

// Classic WinCC (Comfort/Advanced/RT Advanced) - Siemens.Engineering.Hmi.HmiTarget.
// Kind is one of HmiClassicKinds.All; FolderPath is "" for the root of that kind's system folder.
public sealed record HmiClassicObjectSummary(string HmiName, string Kind, string FolderPath, string Name);

// One classic object as Openness XML (without DocumentInfo), or why it couldn't be exported.
public sealed record HmiClassicObjectXml(string Kind, string FolderPath, string Name, string? Xml, string? Error);

public sealed record PlcAlarmTextListsResult(bool Success, IReadOnlyList<PlcAlarmTextList> Lists, string? Error);

public sealed class PlcAlarmTextListChange
{
    public PlcAlarmTextListChange(string list) => List = list;

    public string List { get; }
    public List<string> Added { get; } = new();
    public List<string> Changed { get; } = new();
    public List<string> Deleted { get; } = new();
}

// Message: the error when not successful, otherwise an optional note (e.g. TIA reported warnings).
public sealed record PlcAlarmTextWriteResult(bool Success, IReadOnlyList<PlcAlarmTextListChange> Changes, string? Message);

public sealed record HmiScreenSummary(string HmiName, string GroupPath, string Name, int ScreenNumber, long Width, long Height, int ItemCount);

// Screens travel as JSON text (see HmiScreenConverter).
public sealed record HmiScreenResult(bool Success, string? Json, string? Error);

public sealed record WriteHmiScreenResult(bool Success, int Created, int Updated, int Deleted, IReadOnlyList<string> Messages);

public sealed record LibraryTypeVersionInfo(string Version, bool IsDefault, string State, string ContainedType);

public sealed record LibraryTypeSummary(string FolderPath, string Name, string Kind, IReadOnlyList<LibraryTypeVersionInfo> Versions);

public sealed record FaceplateInterfaceInfo(string PropertyName, string? ValueType, string? DefaultValue);

public sealed record FaceplateTypeResult(bool Success, string ContainedType, IReadOnlyList<FaceplateInterfaceInfo> Interface, IReadOnlyList<string> Events, string? Error);
