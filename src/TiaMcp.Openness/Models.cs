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

// ProjectPath: the project or local session (.alsXX) the process has open, null for none.
// AttachedClients: Openness clients attached to it, this server included (ThisServer).
public sealed record TiaInstanceInfo(int ProcessId, string? ProjectPath, bool WithUserInterface, int AttachedClients, bool ThisServer);

public sealed record TagTableSummary(string PlcName, string GroupPath, string Name);

public sealed record TypeSummary(string PlcName, string GroupPath, string Name);

public sealed record WriteTagsResult(bool Success, IReadOnlyList<string> Messages);

public sealed record SimpleResult(bool Success, string? Error);

// Multiuser: null for a plain project.
public sealed record ProjectStatus(string Name, string? Path, bool IsModified, string Author, DateTime LastModified, string LastModifiedBy, string Version, MultiuserStatus? Multiuser = null);

// A Multiuser local session against its server. Null fields: Openness couldn't tell (e.g. the server isn't reachable).
// Marked: the names of the marked objects that have one (MarkedCount counts them all).
public sealed record MultiuserStatus(bool? UpToDate, int? MarkedCount, IReadOnlyList<string>? Marked, int? Conflicted, string? Error);

// What TiaConnection.GetMarkStates checks the mark state of: one item kind of the source tree.
public enum MarkTarget { Block, Udt, TagTable, HmiTagTable, HmiScreen, HmiAlarmClasses, HmiDiscreteAlarms, HmiAnalogAlarms }

// The Multiuser mark state of one item, over the markable objects it is made of (one for a block,
// every tag of a tag table): how many are marked by this session, marked by another user, and not
// up to date with the server - with the names of the latter two.
public sealed record MarkSummary(int Objects, int ByMe, int ByOthers, int NotUpToDate, IReadOnlyList<string> ByOthersNames, IReadOnlyList<string> NotUpToDateNames);

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
