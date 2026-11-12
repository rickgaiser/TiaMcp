using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Siemens.Engineering.HmiUnified;

namespace TiaMcp.Openness;

/// <summary>
/// WinCC Unified text lists. Openness V21 gives a list only its Name and Delete; the entries are
/// reached through the Export/Import of the whole HmiTextLists composition, as two YAML files (see
/// HmiTextListYaml). So a write starts from a fresh export, changes only the lists being written,
/// imports that, and exports again to check that TIA Portal made of each list what was written.
/// System text lists (HmiSystemTextLists) are TIA Portal's own and not handled.
/// </summary>
public sealed partial class TiaConnection
{
    // The file name passed to Export/Import; TIA Portal adds ".hmi.yml" and ".TextLibrary.hmi.yml".
    private const string HmiTextListFile = "textlists";
    private const string LibrarySuffix = ".TextLibrary.hmi.yml";

    private sealed class HmiTextListExport
    {
        public HmiTextListExport(string listsName, string lists, string libraryName, string library, bool bom)
        {
            ListsName = listsName; Lists = lists; LibraryName = libraryName; Library = library; Bom = bom;
        }
        public string ListsName { get; }
        public string Lists { get; }
        public string LibraryName { get; }
        public string Library { get; }
        public bool Bom { get; }
    }

    public IReadOnlyList<string> ListHmiTextListNames(string hmiName)
    {
        EnsureConnected();
        return GetHmiSoftware(hmiName).HmiTextLists.Select(l => l.Name).ToList();
    }

    public PlcAlarmTextListsResult ReadHmiTextLists(string hmiName)
    {
        EnsureConnected();
        var software = GetHmiSoftware(hmiName);
        if (software.HmiTextLists.Count == 0) return new PlcAlarmTextListsResult(true, Array.Empty<PlcAlarmTextList>(), null);

        var tempDir = NewTempDir();
        try
        {
            var (export, error) = ExportHmiTextLists(software, tempDir);
            if (export == null) return new PlcAlarmTextListsResult(false, Array.Empty<PlcAlarmTextList>(), error);
            return new PlcAlarmTextListsResult(true, HmiTextListYaml.Read(export.Lists, export.Library), null);
        }
        catch (FormatException ex)
        {
            return new PlcAlarmTextListsResult(false, Array.Empty<PlcAlarmTextList>(), $"TIA Portal's text list export has a form this server doesn't read: {ex.Message}");
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    /// <summary>
    /// Writes the given lists: each existing list's entries become the given ones when
    /// deleteMissing is set, otherwise given entries are added/updated and the others kept; a list
    /// that doesn't exist is created. Language columns not given keep their texts. With dryRun only
    /// the comparison is made.
    /// </summary>
    public PlcAlarmTextWriteResult WriteHmiTextLists(string hmiName, IReadOnlyList<PlcAlarmTextList> lists, bool deleteMissing, bool dryRun)
    {
        EnsureConnected();
        var software = GetHmiSoftware(hmiName);
        if (software.HmiTextLists.Count == 0)
            return new PlcAlarmTextWriteResult(false, Array.Empty<PlcAlarmTextListChange>(),
                $"Nothing was written: HMI '{hmiName}' has no text list yet. Openness can only add a list next to an existing one - create one in TIA Portal first.");

        var tempDir = NewTempDir();
        try
        {
            var (export, error) = ExportHmiTextLists(software, Path.Combine(tempDir, "before"));
            if (export == null) return new PlcAlarmTextWriteResult(false, Array.Empty<PlcAlarmTextListChange>(), error);

            HmiTextListPatch patch;
            try { patch = HmiTextListYaml.Patch(export.Lists, export.Library, lists, deleteMissing); }
            catch (FormatException ex) { return new PlcAlarmTextWriteResult(false, Array.Empty<PlcAlarmTextListChange>(), $"Nothing was written: {ex.Message}"); }

            var changes = patch.Changes.Select(c =>
            {
                var change = new PlcAlarmTextListChange(c.List);
                change.Added.AddRange(c.Added);
                change.Changed.AddRange(c.Changed);
                change.Deleted.AddRange(c.Deleted);
                return change;
            }).ToList();
            if (dryRun || !patch.Changes.Any(c => c.Any)) return new PlcAlarmTextWriteResult(true, changes, null);

            var importDir = Path.Combine(tempDir, "import");
            Directory.CreateDirectory(importDir);
            var encoding = new UTF8Encoding(export.Bom);
            File.WriteAllText(Path.Combine(importDir, export.ListsName), patch.ListsYaml, encoding);
            File.WriteAllText(Path.Combine(importDir, export.LibraryName), patch.LibraryYaml, encoding);
            try
            {
                if (!software.HmiTextLists.Import(new DirectoryInfo(importDir), HmiTextListFile))
                    return new PlcAlarmTextWriteResult(false, changes, "TIA Portal's text list import reported failure (it returned false), without a reason.");
            }
            catch (Exception ex)
            {
                return new PlcAlarmTextWriteResult(false, changes, $"Text list import failed: {DescribeException(ex)}");
            }

            // Import's semantics (merge or replace, per list and per entry) aren't documented, so
            // what TIA Portal made of each written list is checked against what was meant.
            var (after, afterError) = ExportHmiTextLists(software, Path.Combine(tempDir, "after"));
            if (after == null) return new PlcAlarmTextWriteResult(false, changes, $"Imported, but the check export failed: {afterError}");
            var meant = HmiTextListYaml.Read(patch.ListsYaml, patch.LibraryYaml);
            var got = HmiTextListYaml.Read(after.Lists, after.Library);
            var mismatches = new List<string>();
            foreach (var list in lists)
            {
                var difference = Difference(meant.FirstOrDefault(l => l.Name == list.Name), got.FirstOrDefault(l => l.Name == list.Name));
                if (difference != null) mismatches.Add($"'{list.Name}': {difference}");
            }
            return mismatches.Count == 0
                ? new PlcAlarmTextWriteResult(true, changes, null)
                : new PlcAlarmTextWriteResult(false, changes, $"Imported, but TIA Portal made something else of {string.Join("; ", mismatches)}");
        }
        catch (FormatException ex)
        {
            return new PlcAlarmTextWriteResult(false, Array.Empty<PlcAlarmTextListChange>(), $"TIA Portal's text list export has a form this server doesn't read: {ex.Message}");
        }
        finally
        {
            DeleteTempDir(tempDir);
        }
    }

    public SimpleResult DeleteHmiTextList(string hmiName, string listName)
    {
        EnsureConnected();
        var list = GetHmiSoftware(hmiName).HmiTextLists.Find(listName);
        if (list == null) return new SimpleResult(false, $"Text list '{listName}' not found in HMI '{hmiName}'.");
        try { list.Delete(); return new SimpleResult(true, null); }
        catch (Exception ex) { return new SimpleResult(false, DescribeException(ex)); }
    }

    // A fresh export of all text lists of the HMI into dir.
    private static (HmiTextListExport? Export, string? Error) ExportHmiTextLists(HmiSoftware software, string dir)
    {
        Directory.CreateDirectory(dir);
        List<FileInfo> files;
        try { files = software.HmiTextLists.Export(new DirectoryInfo(dir), HmiTextListFile).ToList(); }
        catch (Exception ex) { return (null, $"Text list export failed: {DescribeException(ex)}"); }

        var library = files.FirstOrDefault(f => f.Name.EndsWith(LibrarySuffix, StringComparison.OrdinalIgnoreCase));
        var lists = files.FirstOrDefault(f => f != library && f.Name.EndsWith(".hmi.yml", StringComparison.OrdinalIgnoreCase));
        if (lists == null || library == null)
            return (null, $"Text list export gave unexpected files: {string.Join(", ", files.Select(f => f.Name))}");
        var bytes = File.ReadAllBytes(lists.FullName);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return (new HmiTextListExport(lists.Name, File.ReadAllText(lists.FullName), library.Name, File.ReadAllText(library.FullName), bom), null);
    }

    // The first way a list as read back differs from the list as written, or null.
    private static string? Difference(PlcAlarmTextList? meant, PlcAlarmTextList? got)
    {
        if (meant == null) return null;
        if (got == null) return "the list is not there after the import";
        string Key(PlcAlarmTextEntry e) => e.From == e.To ? e.From : $"{e.From}..{e.To}";
        var gotEntries = got.Entries.GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
        foreach (var entry in meant.Entries)
        {
            if (!gotEntries.TryGetValue(Key(entry), out var actual)) return $"entry {Key(entry)} is missing";
            for (var i = 0; i < meant.Languages.Count; i++)
            {
                var j = got.Languages.ToList().IndexOf(meant.Languages[i]);
                var actualText = j >= 0 && j < actual.Texts.Count ? actual.Texts[j] : "";
                if (actualText != entry.Texts[i]) return $"entry {Key(entry)} [{meant.Languages[i]}] is '{actualText}', not '{entry.Texts[i]}'";
            }
        }
        var extra = got.Entries.Select(Key).Except(meant.Entries.Select(Key)).ToList();
        return extra.Count > 0 ? $"entries kept that were removed: {string.Join(", ", extra)}" : null;
    }
}
