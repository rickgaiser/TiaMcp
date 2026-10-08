using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Siemens.Engineering.SW.Alarm;
using Siemens.Engineering.SW.Alarm.TextLists;

namespace TiaMcp.Openness;

/// <summary>
/// Read-only access to the PLC alarm text lists (PLC-Meldetextlisten). The list entries themselves
/// are only reachable through the Excel export of PlcAlarmTextListProvider.
/// </summary>
public sealed partial class TiaConnection
{
    public IReadOnlyList<PlcAlarmTextlistSummary> ListPlcAlarmTextlists(string plcName)
    {
        EnsureConnected();
        var group = GetSoftware(plcName).PlcAlarmTextlistGroup;
        var result = new List<PlcAlarmTextlistSummary>();
        foreach (PlcAlarmUserTextlist list in group.PlcAlarmUserTextlists)
        {
            result.Add(new PlcAlarmTextlistSummary("User", list.ID, list.Name, list.ListRange.ToString()));
        }
        foreach (PlcAlarmSystemTextlist list in group.PlcAlarmSystemTextlists)
        {
            result.Add(new PlcAlarmTextlistSummary("System", list.ID, list.Name, list.ListRange.ToString()));
        }
        return result;
    }

    // Exports all PLC alarm text lists (with their entries, all project languages) to one .xlsx file.
    public SimpleResult ExportPlcAlarmTextlists(string plcName, string targetFile)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);
        var provider = software.GetService<PlcAlarmTextListProvider>();
        if (provider == null)
        {
            return new SimpleResult(false, $"PLC '{plcName}' offers no PlcAlarmTextListProvider service.");
        }

        var file = new FileInfo(targetFile);
        if (file.Exists)
        {
            return new SimpleResult(false, $"Target file already exists: {file.FullName}");
        }
        file.Directory?.Create();

        try
        {
            var result = provider.ExportToXlsx(file);
            var log = result.LogFilePath?.FullName;
            return result.State.ToString() is "Success" or "OK"
                ? new SimpleResult(true, log)
                : new SimpleResult(false, $"Export state {result.State}. Log: {log}");
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    // Imports PLC alarm text lists from an .xlsx in the format written by ExportPlcAlarmTextlists.
    // Existing entries with the same list/range are overwritten (ImportOptions.Override).
    // dryRun: exports the current lists to a temp file, compares entry by entry and writes nothing.
    public SimpleResult ImportPlcAlarmTextlists(string plcName, string sourceFile, bool dryRun = true)
    {
        if (dryRun)
        {
            return DryRunPlcAlarmTextlistImport(plcName, sourceFile);
        }

        EnsureConnected();
        var provider = GetSoftware(plcName).GetService<PlcAlarmTextListProvider>();
        if (provider == null)
        {
            return new SimpleResult(false, $"PLC '{plcName}' offers no PlcAlarmTextListProvider service.");
        }
        var file = new FileInfo(sourceFile);
        if (!file.Exists)
        {
            return new SimpleResult(false, $"Source file not found: {file.FullName}");
        }
        try
        {
            var result = provider.ImportFromXlsx(file, Siemens.Engineering.ImportOptions.Override);
            var log = result.LogFilePath?.FullName;
            return result.State.ToString() is "Success" or "OK"
                ? new SimpleResult(true, log)
                : new SimpleResult(false, $"Import state {result.State}. Log: {log}");
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    private SimpleResult DryRunPlcAlarmTextlistImport(string plcName, string sourceFile)
    {
        EnsureConnected();
        var source = new FileInfo(sourceFile);
        if (!source.Exists)
        {
            return new SimpleResult(false, $"Source file not found: {source.FullName}");
        }
        var provider = GetSoftware(plcName).GetService<PlcAlarmTextListProvider>();
        if (provider == null)
        {
            return new SimpleResult(false, $"PLC '{plcName}' offers no PlcAlarmTextListProvider service.");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "TiaMcpTextlists", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var current = new FileInfo(Path.Combine(tempDir, "current.xlsx"));
            var export = provider.ExportToXlsx(current);
            if (!(export.State.ToString() is "Success" or "OK") || !current.Exists)
            {
                return new SimpleResult(false, $"Export of the current lists for comparison failed ({export.State}).");
            }

            var now = ReadTextListEntries(current.FullName);
            var incoming = ReadTextListEntries(source.FullName);
            var lines = new List<string>();
            int created = 0, changed = 0, unchanged = 0;
            var perList = new SortedDictionary<string, int[]>(StringComparer.Ordinal);
            foreach (var entry in incoming)
            {
                if (!perList.TryGetValue(entry.Key.List, out var c)) perList[entry.Key.List] = c = new int[4];
                if (!now.TryGetValue(entry.Key, out var old))
                {
                    created++; c[0]++;
                    if (lines.Count < 30) lines.Add($"WOULD CREATE    {entry.Key.List} {entry.Key.Range}: {entry.Value.FirstOrDefault()}");
                }
                else if (!old.SequenceEqual(entry.Value))
                {
                    changed++; c[1]++;
                    if (lines.Count < 30) lines.Add($"WOULD OVERWRITE {entry.Key.List} {entry.Key.Range}: '{old.FirstOrDefault()}' -> '{entry.Value.FirstOrDefault()}'");
                }
                else
                {
                    unchanged++; c[2]++;
                }
            }
            // TIA replaces the COMPLETE content of every list that occurs in the file:
            // entries of those lists that are missing in the file get deleted.
            var listsInFile = new HashSet<string>(incoming.Keys.Select(k => k.List), StringComparer.Ordinal);
            var deleted = now.Keys.Where(k => listsInFile.Contains(k.List) && !incoming.ContainsKey(k)).ToList();
            foreach (var k in deleted)
            {
                if (!perList.TryGetValue(k.List, out var c)) perList[k.List] = c = new int[4];
                if (lines.Count < 60) lines.Add($"WOULD DELETE    {k.List} {k.Range}: {now[k].FirstOrDefault()}");
            }
            var deletedPerList = deleted.GroupBy(k => k.List).ToDictionary(g => g.Key, g => g.Count());
            var summary = $"DRY RUN - nothing imported. {incoming.Count} entries in file: {created} new, {changed} overwritten, {unchanged} unchanged, {deleted.Count} WOULD BE DELETED (missing in file - TIA replaces whole lists).\n"
                + string.Join("\n", perList.Select(p => $"  {p.Key}: {p.Value[0]} new, {p.Value[1]} overwritten, {p.Value[2]} unchanged, {(deletedPerList.TryGetValue(p.Key, out var d) ? d : 0)} deleted"))
                + (lines.Count > 0 ? "\nFirst differences (first language column):\n" + string.Join("\n", lines) : "");
            return new SimpleResult(true, summary);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private readonly struct TextListKey : IEquatable<TextListKey>
    {
        public TextListKey(string list, string range) { List = list; Range = range; }
        public string List { get; }
        public string Range { get; }
        public bool Equals(TextListKey other) => List == other.List && Range == other.Range;
        public override bool Equals(object? obj) => obj is TextListKey k && Equals(k);
        public override int GetHashCode() => (List ?? "").GetHashCode() * 31 + (Range ?? "").GetHashCode();
    }

    // Reads sheet 'TextListEntry' (Parent, From, To, Text [lang]...) of a TIA text list export.
    private static Dictionary<TextListKey, string[]> ReadTextListEntries(string xlsxPath)
    {
        System.Xml.Linq.XNamespace m = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        System.Xml.Linq.XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        using var zip = System.IO.Compression.ZipFile.OpenRead(xlsxPath);
        System.Xml.Linq.XDocument Load(string name)
        {
            var e = zip.GetEntry(name) ?? throw new InvalidDataException($"{name} missing in {xlsxPath}");
            using var st = e.Open();
            return System.Xml.Linq.XDocument.Load(st);
        }
        var shared = zip.GetEntry("xl/sharedStrings.xml") == null ? new List<string>()
            : Load("xl/sharedStrings.xml").Root!.Elements(m + "si").Select(si => string.Concat(si.Descendants(m + "t").Select(t => t.Value))).ToList();
        var workbook = Load("xl/workbook.xml");
        var rels = Load("xl/_rels/workbook.xml.rels");
        var sheet = workbook.Descendants(m + "sheet").FirstOrDefault(s => (string?)s.Attribute("name") == "TextListEntry")
            ?? throw new InvalidDataException($"Sheet 'TextListEntry' missing in {xlsxPath}");
        var relId = (string?)sheet.Attribute(r + "id");
        var target = rels.Root!.Elements().First(e => (string?)e.Attribute("Id") == relId).Attribute("Target")!.Value.TrimStart('/');
        var sheetPath = target.StartsWith("xl/") ? target : "xl/" + target;

        var result = new Dictionary<TextListKey, string[]>();
        var rows = Load(sheetPath).Descendants(m + "row").ToList();
        foreach (var row in rows.Skip(1))
        {
            var cells = new SortedDictionary<int, string>();
            foreach (var c in row.Elements(m + "c"))
            {
                var col = new string(((string)c.Attribute("r")!).TakeWhile(char.IsLetter).ToArray());
                int idx = 0; foreach (var ch in col) idx = idx * 26 + (ch - 'A' + 1);
                string val;
                if ((string?)c.Attribute("t") == "s") val = shared[int.Parse(c.Element(m + "v")!.Value)];
                else if ((string?)c.Attribute("t") == "inlineStr") val = string.Concat(c.Descendants(m + "t").Select(t => t.Value));
                else val = c.Element(m + "v")?.Value ?? "";
                cells[idx] = val;
            }
            if (!cells.TryGetValue(1, out var list) || string.IsNullOrEmpty(list)) continue;
            cells.TryGetValue(2, out var from); cells.TryGetValue(3, out var to);
            var texts = cells.Where(kv => kv.Key >= 4).Select(kv => kv.Value ?? "").ToArray();
            result[new TextListKey(list, $"{from}..{to}")] = texts;
        }
        return result;
    }
}
