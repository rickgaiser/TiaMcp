using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Formats;

// PLC alarm text list (user list): entries From/To (as text, like the export) with one text per language.
public sealed record PlcAlarmTextEntry(string From, string To, IReadOnlyList<string> Texts);

public sealed record PlcAlarmTextList(string Name, IReadOnlyList<string> Languages, IReadOnlyList<PlcAlarmTextEntry> Entries);

/// <summary>A PLC alarm text list as CSV: From, To and one 'Text [language]' column per project language.</summary>
public static class AlarmTextListCsv
{
    public static string Format(PlcAlarmTextList list) =>
        Csv.Format(new[] { "From", "To" }.Concat(list.Languages.Select(l => $"Text [{l}]")).ToArray(),
            list.Entries.Select(e => new[] { e.From, e.To }.Concat(e.Texts).ToArray()));

    // The From of a list's default entry (the text for every value no other entry has); its To is
    // empty. Only WinCC Unified text lists have one.
    public const string DefaultEntry = "Default";

    // Inverse of Format. An empty To means a single value (To = From). With allowDefault, a From of
    // "Default" (any case) is the default entry. Throws FormatException on bad input.
    public static PlcAlarmTextList Parse(string name, string csv, bool allowDefault = false)
    {
        var rows = Csv.ParseRows(csv);
        if (rows.Count == 0) throw new FormatException("empty file");
        var header = rows[0];
        int cFrom = header.IndexOf("From"), cTo = header.IndexOf("To");
        if (cFrom < 0) throw new FormatException("column 'From' missing");
        var languageColumns = header.Select((h, i) => (h, i)).Where(x => x.h.StartsWith("Text [") && x.h.EndsWith("]")).ToList();
        if (languageColumns.Count == 0) throw new FormatException("no 'Text [xx-XX]' column");

        var entries = new List<PlcAlarmTextEntry>();
        for (var r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            string Cell(int i) => i >= 0 && i < row.Count ? row[i] : "";
            var from = Cell(cFrom).Trim();
            if (from.Length == 0) continue;
            var to = Cell(cTo).Trim();
            if (allowDefault && string.Equals(from, DefaultEntry, StringComparison.OrdinalIgnoreCase))
            {
                if (to.Length > 0) throw new FormatException($"line {r + 1}: the default entry has no To (leave it empty)");
                entries.Add(new PlcAlarmTextEntry(DefaultEntry, "", languageColumns.Select(c => Cell(c.i)).ToList()));
                continue;
            }
            if (!long.TryParse(from, out _))
                throw new FormatException($"line {r + 1}: From '{from}' is not a number{(allowDefault ? $" or '{DefaultEntry}'" : "")}");
            if (to.Length == 0) to = from;
            if (!long.TryParse(to, out _)) throw new FormatException($"line {r + 1}: To '{to}' is not a number");
            entries.Add(new PlcAlarmTextEntry(from, to, languageColumns.Select(c => Cell(c.i)).ToList()));
        }
        var duplicate = entries.GroupBy(e => $"{e.From}|{e.To}").FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            throw new FormatException(duplicate.Key == DefaultEntry + "|"
                ? $"the default entry appears {duplicate.Count()} times; a list has at most one"
                : $"entry {duplicate.First().From}{(duplicate.First().From == duplicate.First().To ? "" : ".." + duplicate.First().To)} appears {duplicate.Count()} times");
        return new PlcAlarmTextList(name, languageColumns.Select(c => c.h.Substring(6, c.h.Length - 7)).ToList(), entries);
    }
}
