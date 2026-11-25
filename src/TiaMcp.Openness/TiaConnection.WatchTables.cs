using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.WatchAndForceTables;

namespace TiaMcp.Openness;

public sealed record WatchTableSummary(string PlcName, string GroupPath, string Name, bool IsForceTable);

// BadRows: the Names of the rows TIA Portal can't resolve (unknown symbol or address); nothing was
// written then. Created: the table didn't exist before.
public sealed record WatchTableWriteResult(bool Success, bool Created, IReadOnlyList<string> BadRows, string? Error);

/// <summary>
/// PLC watch tables and the force table (see WatchTableCsv). Openness can't change a row, so a
/// watch table is always written whole, by an XML import. TIA Portal V21 behaves badly with a bad
/// row: with a DisplayFormat the whole import fails, and an import that followed a few failed ones
/// killed TIA Portal (2026-11-23, cause not proven); without DisplayFormats it imports, but leaves a table that can't
/// be exported. So a write first imports the rows without DisplayFormats into a scratch table to
/// find the rows TIA Portal can't resolve, and imports the real table only when there are none.
/// The force table is read-only: Openness can't create one, and writing it would set up forcing.
/// </summary>
public sealed partial class TiaConnection
{
    // The scratch table of a write's symbol check; deleted again right after.
    private const string CheckTablePrefix = "TiaMcp_check_";

    public IReadOnlyList<WatchTableSummary> ListWatchTables(string plcName)
    {
        EnsureConnected();
        return AllWatchAndForceTables(plcName).Select(x => x.Summary).ToList();
    }

    // All watch table group paths, including empty groups.
    public IReadOnlyList<string> ListWatchTableGroups(string plcName)
    {
        EnsureConnected();
        var result = new List<string>();
        void Walk(PlcWatchAndForceTableUserGroupComposition groups, string path)
        {
            foreach (PlcWatchAndForceTableUserGroup g in groups)
            {
                var child = path.Length == 0 ? g.Name : $"{path}/{g.Name}";
                result.Add(child);
                Walk(g.Groups, child);
            }
        }
        Walk(GetSoftware(plcName).WatchAndForceTableGroup.Groups, "");
        return result;
    }

    // Each table as '<name>.csv' and/or '<name>.xml', from one export (see ReadInFormats).
    // forceTables: the force table(s) instead of the watch tables. open: first open a watch table that
    // is inconsistent in TIA Portal's editor, which can make it exportable (the project may then have
    // unsaved changes, and the editor tab stays open).
    public IReadOnlyList<(WatchTableSummary Table, ExportResult Result)> ReadAllWatchTables(string plcName, bool forceTables, bool open,
        Func<string, string, IReadOnlyCollection<string>?>? formats = null, ExportingItem? exporting = null)
    {
        EnsureConnected();
        var all = AllWatchAndForceTables(plcName, open).Where(x => x.Summary.IsForceTable == forceTables).ToList();
        return Selected(all.Select(x => x.Summary).ToList(), all.Select(x => x.Export).ToList(), formats, s => (s.GroupPath, s.Name), exporting)
            .Select(x => (x.Summary, ReadSafely(() => ReadInFormats(x.Summary.Name, x.Formats,
                capture => ReadWatchTableCsv(x.Summary.Name, x.Object, capture ?? new RawXmlCapture()),
                f => ExportWatchTable(x.Object, f))))).ToList();
    }

    private static ExportResult ReadWatchTableCsv(string name, Action<FileInfo> export, RawXmlCapture capture)
    {
        var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "TiaMcp", "watchtable", Guid.NewGuid().ToString("N")));
        dir.Create();
        try
        {
            var file = new FileInfo(Path.Combine(dir.FullName, "watchtable.xml"));
            ExportWatchTable(export, file);
            capture.Take(file);
            var csv = WatchTableCsv.Format(WatchTableCsv.FromXml(capture.Xml!));
            return new ExportResult(true, new[] { new BlockDocument(name + SourceFormat.Extension(SourceFormat.Csv), csv) }, null);
        }
        finally
        {
            try { dir.Delete(true); } catch { /* best effort cleanup */ }
        }
    }

    // An inconsistent table can't be exported: one with a row that doesn't resolve, but also a valid
    // one not opened in the editor since the project was (seen 2026-11-25).
    private static void ExportWatchTable(Action<FileInfo> export, FileInfo file)
    {
        try { export(file); }
        catch (Exception ex) when (!IsLostProject(ex))
        {
            throw new InvalidOperationException($"{ex.Message} Open the table in TIA Portal's editor and read again, or read with openWatchTables=true: TIA Portal can report a valid table inconsistent until it has been opened. If it still fails, check it there for rows that don't resolve.", ex);
        }
    }

    // The tables with their group path and how to export them, watch tables of a group before its subgroups.
    private IEnumerable<(WatchTableSummary Summary, Action<FileInfo> Export)> AllWatchAndForceTables(string plcName, bool open = false)
    {
        var result = new List<(WatchTableSummary, Action<FileInfo>)>();
        void Walk(PlcWatchAndForceTableGroup group, string path)
        {
            foreach (PlcWatchTable t in group.WatchTables)
                result.Add((new WatchTableSummary(plcName, path, t.Name, false), f =>
                {
                    if (open && !t.IsConsistent) t.ShowInEditor();
                    t.Export(f, ExportOptions.WithDefaults);
                }));
            foreach (PlcForceTable t in group.ForceTables)
                result.Add((new WatchTableSummary(plcName, path, t.Name, true), f => t.Export(f, ExportOptions.WithDefaults)));
            foreach (PlcWatchAndForceTableUserGroup g in group.Groups)
                Walk(g, path.Length == 0 ? g.Name : $"{path}/{g.Name}");
        }
        Walk(GetSoftware(plcName).WatchAndForceTableGroup, "");
        return result;
    }

    private static IEnumerable<(string GroupPath, PlcWatchTable Table)> AllWatchTables(PlcWatchAndForceTableGroup group, string path = "")
    {
        foreach (PlcWatchTable t in group.WatchTables) yield return (path, t);
        foreach (PlcWatchAndForceTableUserGroup g in group.Groups)
            foreach (var x in AllWatchTables(g, path.Length == 0 ? g.Name : $"{path}/{g.Name}")) yield return x;
    }

    private static PlcWatchAndForceTableGroup? ResolveWatchTableGroup(PlcSoftware software, string groupPath) =>
        string.IsNullOrEmpty(groupPath)
            ? software.WatchAndForceTableGroup
            : FindByPath<PlcWatchAndForceTableUserGroup>(groupPath, software.WatchAndForceTableGroup.Groups.Find, g => g.Groups.Find);

    // Replaces the watch table, or creates it in groupPath (which must exist) - after checking that
    // TIA Portal can resolve every row; see the class comment.
    public WatchTableWriteResult WriteWatchTable(string plcName, string groupPath, WatchTable table)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);
        var root = software.WatchAndForceTableGroup;
        var group = ResolveWatchTableGroup(software, groupPath);
        if (group == null) return new WatchTableWriteResult(false, false, Array.Empty<string>(), $"Group '{groupPath}' not found in PLC '{plcName}'.");
        if (table.IsForceTable) return new WatchTableWriteResult(false, false, Array.Empty<string>(), "The force table is read-only.");

        var existing = AllWatchTables(root).FirstOrDefault(x => string.Equals(x.Table.Name, table.Name, StringComparison.OrdinalIgnoreCase));
        if (existing.Table != null && existing.GroupPath != groupPath)
            return new WatchTableWriteResult(false, false, Array.Empty<string>(),
                $"A watch table named '{existing.Table.Name}' already exists in group '{existing.GroupPath}' (names are unique per PLC). Delete or rename it first.");
        if (root.ForceTables.Any(f => string.Equals(f.Name, table.Name, StringComparison.OrdinalIgnoreCase)))
            return new WatchTableWriteResult(false, false, Array.Empty<string>(), $"'{table.Name}' is the force table's name.");

        string? error;
        var badRows = FindUnresolvedRows(root, table, out error);
        if (error != null) return new WatchTableWriteResult(false, existing.Table == null, badRows, error);
        if (badRows.Count > 0)
            return new WatchTableWriteResult(false, existing.Table == null, badRows,
                "TIA Portal can't resolve these rows (unknown symbol or address, or a block that isn't compiled yet); nothing was written.");

        error = ImportWatchTable(group.WatchTables, table, withDisplayFormats: true, ImportOptions.Override);
        if (error != null) return new WatchTableWriteResult(false, existing.Table == null, badRows, error);
        var written = group.WatchTables.Find(table.Name);
        if (written != null && !written.IsConsistent)
            return new WatchTableWriteResult(false, existing.Table == null, badRows, "Imported, but TIA Portal reports the table inconsistent.");
        return new WatchTableWriteResult(true, existing.Table == null, badRows, null);
    }

    // The Names of the tag rows TIA Portal can't resolve: imported without DisplayFormats into a
    // scratch table (which then imports even with a bad row, and is inconsistent), halving the rows
    // of an inconsistent check until the bad ones are found. error: a check import itself failed.
    private static List<string> FindUnresolvedRows(PlcWatchAndForceTableGroup root, WatchTable table, out string? error)
    {
        var tagRows = table.Rows.Select((r, i) => (r, i)).Where(x => !x.r.IsComment).Select(x => x.i).ToList();
        var bad = new List<string>();
        error = null;
        if (tagRows.Count == 0) return bad;

        var checkName = CheckTablePrefix + Guid.NewGuid().ToString("N").Substring(0, 8);
        string? failure = null;
        bool Consistent(List<int> rows)
        {
            var check = WatchTableCsv.WithRows(table, checkName, rows);
            failure = ImportWatchTable(root.WatchTables, check, withDisplayFormats: false, ImportOptions.None);
            var imported = root.WatchTables.Find(checkName);
            try
            {
                if (failure != null) return false;
                return imported?.IsConsistent ?? throw new InvalidOperationException("The check table wasn't created.");
            }
            finally { imported?.Delete(); }
        }

        void Search(List<int> rows)
        {
            if (failure != null || Consistent(rows)) return;
            if (failure != null) return;
            if (rows.Count == 1) { bad.Add(table.Rows[rows[0]].Name); return; }
            var half = rows.Count / 2;
            Search(rows.Take(half).ToList());
            Search(rows.Skip(half).ToList());
        }

        try { Search(tagRows); }
        catch (Exception ex) when (!IsLostProject(ex)) { failure = ex.Message; }
        error = failure == null ? null : $"Nothing was written: the symbol check's import failed: {failure}";
        return bad;
    }

    // Null on success, else the import's error.
    private static string? ImportWatchTable(PlcWatchTableComposition target, WatchTable table, bool withDisplayFormats, ImportOptions options)
    {
        var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "TiaMcp", "import-watchtable", Guid.NewGuid().ToString("N")));
        dir.Create();
        try
        {
            var file = new FileInfo(Path.Combine(dir.FullName, "import.xml"));
            File.WriteAllText(file.FullName, WatchTableCsv.ToXml(table, withDisplayFormats));
            target.Import(file, options);
            return null;
        }
        catch (Exception ex) when (!IsLostProject(ex))
        {
            return ex.Message;
        }
        finally
        {
            try { dir.Delete(true); } catch { /* best effort cleanup */ }
        }
    }

    // The raw XML of a table (the "xml" format) as a WatchTable, after the same checks as the other
    // raw XML imports: one watch table, named like its file.
    public WatchTableWriteResult WriteWatchTableXml(string plcName, string groupPath, string tableName, string xml)
    {
        WatchTable table;
        try { table = WatchTableCsv.FromXml(xml); }
        catch (Exception ex) { return new WatchTableWriteResult(false, false, Array.Empty<string>(), $"Nothing was written: the XML is not a watch table export: {ex.Message}"); }
        if (!string.Equals(table.Name, tableName, StringComparison.Ordinal))
            return new WatchTableWriteResult(false, false, Array.Empty<string>(), $"Nothing was written: the XML's <Name> is '{table.Name}', but the file is named '{tableName}'. Rename both together.");
        return WriteWatchTable(plcName, groupPath, table);
    }

    private PlcWatchTable? FindWatchTable(PlcSoftware software, string name) =>
        AllWatchTables(software.WatchAndForceTableGroup).Select(x => x.Table).FirstOrDefault(t => t.Name == name);

    public SimpleResult DeleteWatchTable(string plcName, string tableName)
    {
        EnsureConnected();
        var table = FindWatchTable(GetSoftware(plcName), tableName);
        if (table == null) return new SimpleResult(false, $"Watch table '{tableName}' not found in PLC '{plcName}'.");
        return TryChange(() => table.Delete());
    }

    public SimpleResult RenameWatchTable(string plcName, string tableName, string newName)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);
        var table = FindWatchTable(software, tableName);
        if (table == null) return new SimpleResult(false, $"Watch table '{tableName}' not found in PLC '{plcName}'.");
        if (FindWatchTable(software, newName) != null) return new SimpleResult(false, $"A watch table named '{newName}' already exists in PLC '{plcName}' (names are unique per PLC).");
        return TryChange(() => table.Name = newName);
    }

    public bool WatchTableGroupExists(string plcName, string groupPath)
    {
        EnsureConnected();
        return ResolveWatchTableGroup(GetSoftware(plcName), groupPath) != null;
    }

    public SimpleResult CreateWatchTableGroup(string plcName, string parentGroupPath, string groupName)
    {
        EnsureConnected();
        var parent = ResolveWatchTableGroup(GetSoftware(plcName), parentGroupPath);
        if (parent == null) return new SimpleResult(false, $"Parent group '{parentGroupPath}' not found in PLC '{plcName}'.");
        return TryChange(() => parent.Groups.Create(groupName));
    }

    public SimpleResult DeleteWatchTableGroup(string plcName, string groupPath)
    {
        EnsureConnected();
        var group = string.IsNullOrEmpty(groupPath) ? null : ResolveWatchTableGroup(GetSoftware(plcName), groupPath) as PlcWatchAndForceTableUserGroup;
        if (group == null) return new SimpleResult(false, $"Group '{groupPath}' not found in PLC '{plcName}'.");
        return TryChange(() => group.Delete());
    }

    public SimpleResult RenameWatchTableGroup(string plcName, string groupPath, string newName)
    {
        EnsureConnected();
        var group = string.IsNullOrEmpty(groupPath) ? null : ResolveWatchTableGroup(GetSoftware(plcName), groupPath) as PlcWatchAndForceTableUserGroup;
        if (group == null) return new SimpleResult(false, $"Group '{groupPath}' not found in PLC '{plcName}'.");
        return TryChange(() => group.Name = newName);
    }

    private static SimpleResult TryChange(Action action)
    {
        try { action(); return new SimpleResult(true, null); }
        catch (Exception ex) when (!IsLostProject(ex)) { return new SimpleResult(false, ex.Message); }
    }

    // For the Multiuser mark state (see MarkObjects).
    private static IEnumerable<PlcWatchTable> AllWatchTableObjects(PlcSoftware software) =>
        AllWatchTables(software.WatchAndForceTableGroup).Select(x => x.Table);
}
