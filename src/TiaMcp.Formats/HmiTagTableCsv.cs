using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Formats;

// Length: the maximum length of a String/WString tag (HmiTag.MaxLength, the GUI's "Length"), null for other types.
public sealed record HmiTagInfo(string Name, string DataType, string? Address, string? Connection, string? PlcName, string? PlcTag, string? Comment, string? AcquisitionCycle,
    uint? Length = null);

// Null optional fields mean "leave unchanged" on an existing tag.
public sealed record HmiTagSpec(string Name, string DataType, string? Address, string? Connection, string? PlcName, string? PlcTag, string? Comment = null,
    string? AcquisitionCycle = null, uint? Length = null);

/// <summary>A WinCC Unified HMI tag table as CSV, one row per tag.</summary>
public static class HmiTagTableCsv
{
    private static readonly string[] Headers =
        { "Name", "DataType", "Address", "Connection", "PlcName", "PlcTag", "Comment", "AcquisitionCycle" };

    // length: add the Length column. Optional because reading it is slow; without the column
    // Parse leaves every tag's length unchanged.
    public static string Format(IReadOnlyList<HmiTagInfo> tags, bool length = false) =>
        Csv.Format(length ? Headers.Append("Length").ToArray() : Headers, tags.Select(t =>
        {
            var row = new[] { t.Name, t.DataType, t.Address ?? "", t.Connection ?? "", t.PlcName ?? "", t.PlcTag ?? "", t.Comment ?? "", t.AcquisitionCycle ?? "" };
            return length ? row.Append(t.Length?.ToString() ?? "").ToArray() : row;
        }));

    // Inverse of Format, same rules as TagTableCsv.Parse.
    public static HmiTagSpec[] Parse(string csv)
    {
        var rows = Csv.ParseRecords(csv);
        var tags = new List<HmiTagSpec>();
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var line = i + 2; // 1-based, after the header row
            var name = Csv.Get(r, "Name");
            var dataType = Csv.Get(r, "DataType");
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(dataType)) continue; // blank line
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(dataType))
                throw new FormatException($"CSV line {line}: Name and DataType are required.");

            var lengthText = Csv.GetOptional(r, "Length");
            uint? length = null;
            if (!string.IsNullOrEmpty(lengthText))
            {
                if (!uint.TryParse(lengthText, out var parsed) || parsed == 0)
                    throw new FormatException($"CSV line {line}: Length '{lengthText}' is not a positive whole number.");
                length = parsed;
            }

            tags.Add(new HmiTagSpec(name!, dataType!,
                Csv.GetOptional(r, "Address"), Csv.GetOptional(r, "Connection"),
                Csv.GetOptional(r, "PlcName"), Csv.GetOptional(r, "PlcTag"), Csv.GetOptional(r, "Comment"),
                Csv.GetOptional(r, "AcquisitionCycle"), length));
        }
        return tags.ToArray();
    }
}
