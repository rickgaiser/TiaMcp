using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Formats;

public sealed record TagInfo(string Name, string DataType, string LogicalAddress, string? Comment,
    bool ExternalAccessible, bool ExternalVisible, bool ExternalWritable);

// Null optional fields mean "leave unchanged" on an existing tag.
public sealed record TagSpec(string Name, string DataType, string? LogicalAddress, string? Comment = null,
    bool? ExternalAccessible = null, bool? ExternalVisible = null, bool? ExternalWritable = null);

/// <summary>A PLC tag table as CSV, one row per tag.</summary>
public static class TagTableCsv
{
    private static readonly string[] Headers =
        { "Name", "DataType", "LogicalAddress", "Comment", "ExternalAccessible", "ExternalVisible", "ExternalWritable" };

    public static string Format(IReadOnlyList<TagInfo> tags) =>
        Csv.Format(Headers, tags.Select(t => new[] { t.Name, t.DataType, t.LogicalAddress, t.Comment ?? "",
            t.ExternalAccessible.ToString(), t.ExternalVisible.ToString(), t.ExternalWritable.ToString() }));

    // Inverse of Format. Missing columns and empty cells map to null ("leave unchanged"), so CSVs
    // from before the flag columns existed still import. Throws FormatException on bad input.
    public static TagSpec[] Parse(string csv)
    {
        var rows = Csv.ParseRecords(csv);
        var tags = new List<TagSpec>();
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var line = i + 2; // 1-based, after the header row
            var name = Csv.Get(r, "Name");
            var dataType = Csv.Get(r, "DataType");
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(dataType)) continue; // blank line
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(dataType))
                throw new FormatException($"CSV line {line}: Name and DataType are required.");

            tags.Add(new TagSpec(name!, dataType!,
                Csv.GetOptional(r, "LogicalAddress"), Csv.GetOptional(r, "Comment"),
                Csv.GetBool(r, "ExternalAccessible", line), Csv.GetBool(r, "ExternalVisible", line), Csv.GetBool(r, "ExternalWritable", line)));
        }
        return tags.ToArray();
    }
}
