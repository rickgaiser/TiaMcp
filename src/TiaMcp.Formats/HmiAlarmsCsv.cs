using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TiaMcp.Formats;

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

/// <summary>
/// WinCC Unified HMI alarms as CSV: one file per alarm type (discrete, analog) and one for the
/// alarm classes, one row per alarm or class.
/// </summary>
public static class HmiAlarmsCsv
{
    private static readonly string[] AlarmHeaders =
        { "Type", "Name", "AlarmClass", "EventText", "InfoText", "TriggerAddress", "Condition", "ConditionValue", "RaisedStateTag", "AuditClass", "Area", "Origin" };

    private static readonly string[] ClassHeaders = { "Name", "Priority", "Log", "Id", "IsSystem" };

    public static string FormatAlarms(IReadOnlyList<HmiAlarmInfo> alarms) =>
        Csv.Format(AlarmHeaders, alarms.Select(a => new[]
        {
            a.Type, a.Name, a.AlarmClass ?? "", a.EventText ?? "", a.InfoText ?? "", a.TriggerAddress ?? "",
            a.Condition ?? "", a.ConditionValue ?? "", a.RaisedStateTag ?? "", a.AuditClass ?? "", a.Area ?? "", a.Origin ?? ""
        }));

    // Inverse of FormatAlarms, for alarms of the given type ("Discrete"/"Analog"); the file's own
    // Type column is informational. Empty cells map to null ("leave unchanged"). TriggerAddress is
    // computed by TIA Portal: it's exported for readability but never written back -
    // RaisedStateTag is what (re)binds the trigger. Throws FormatException on bad input.
    public static HmiAlarmSpec[] ParseAlarms(string csv, string type)
    {
        var alarms = new List<HmiAlarmSpec>();
        foreach (var (r, line) in NamedRows(csv))
        {
            alarms.Add(new HmiAlarmSpec(
                type, Csv.Get(r, "Name")!,
                Csv.GetOptional(r, "AlarmClass"), Csv.GetOptional(r, "EventText"), Csv.GetOptional(r, "InfoText"),
                null,
                Csv.GetOptional(r, "Condition"), Csv.GetOptional(r, "ConditionValue"), Csv.GetOptional(r, "RaisedStateTag"),
                Csv.GetOptional(r, "AuditClass"), Csv.GetOptional(r, "Area"), Csv.GetOptional(r, "Origin")));
        }
        return alarms.ToArray();
    }

    // Whether writing spec would change nothing on the alarm as it is: every cell it sets (not
    // null) already holds that value. TriggerAddress is never written, so it doesn't count.
    public static bool Unchanged(HmiAlarmSpec spec, HmiAlarmInfo current) =>
        Same(spec.AlarmClass, current.AlarmClass) && Same(spec.EventText, current.EventText) && Same(spec.InfoText, current.InfoText)
        && Same(spec.Condition, current.Condition) && SameNumber(spec.ConditionValue, current.ConditionValue)
        && Same(spec.RaisedStateTag, current.RaisedStateTag) && Same(spec.AuditClass, current.AuditClass)
        && Same(spec.Area, current.Area) && Same(spec.Origin, current.Origin);

    private static bool Same(string? spec, string? current) => spec == null || spec == (current ?? "");

    private static bool SameNumber(string? spec, string? current) =>
        Same(spec, current)
        || (double.TryParse(spec, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
            && double.TryParse(current, NumberStyles.Float, CultureInfo.CurrentCulture, out var b) && a == b);

    public static string FormatClasses(IReadOnlyList<HmiAlarmClassInfo> classes) =>
        Csv.Format(ClassHeaders, classes.Select(c => new[] { c.Name, c.Priority.ToString(), c.Log ?? "", c.Id.ToString(), c.IsSystem.ToString() }));

    // Inverse of FormatClasses. Id and IsSystem are informational and not written back.
    public static HmiAlarmClassSpec[] ParseClasses(string csv)
    {
        var classes = new List<HmiAlarmClassSpec>();
        foreach (var (r, line) in NamedRows(csv))
        {
            var priorityText = Csv.Get(r, "Priority")?.Trim();
            int? priority = null;
            if (!string.IsNullOrEmpty(priorityText))
            {
                if (!int.TryParse(priorityText, out var p)) throw new FormatException($"CSV line {line}: Priority must be a number, got '{priorityText}'.");
                priority = p;
            }
            classes.Add(new HmiAlarmClassSpec(Csv.Get(r, "Name")!, priority, Csv.GetOptional(r, "Log")));
        }
        return classes.ToArray();
    }

    // Rows with their 1-based line number; Name is required.
    private static IEnumerable<(Dictionary<string, string> Row, int Line)> NamedRows(string csv)
    {
        var rows = Csv.ParseRecords(csv);
        for (var i = 0; i < rows.Count; i++)
        {
            var line = i + 2; // 1-based, after the header row
            if (string.IsNullOrEmpty(Csv.Get(rows[i], "Name"))) throw new FormatException($"CSV line {line}: Name is required.");
            yield return (rows[i], line);
        }
    }
}
