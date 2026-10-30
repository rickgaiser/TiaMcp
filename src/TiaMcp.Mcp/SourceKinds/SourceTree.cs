using System.Text;
using TiaMcp.Openness;

namespace TiaMcp.Mcp;

/// <summary>Paths and small helpers of the source tree, shared by the tools and the kind handlers.</summary>
internal static class SourceTree
{
    public static string SectionOf(string kind) =>
        SourceKinds.Find(kind)?.Section ?? throw new ArgumentException($"Unknown kind '{kind}'.", nameof(kind));

    // The file name (without extension) an item's files start with: its name, or the fixed file
    // name of a kind such as the HMI alarm files.
    public static string ItemStem(string kind, string name) => SourceKinds.Find(kind)?.FixedName ?? name;

    // How an item appears in _export_summary.txt, list_project and tool results - the format
    // SourceKinds.ListedItem reads back.
    public static string Describe(string kind, string groupPath, string name) =>
        SourceKinds.IsFixed(kind) ? kind : $"{kind} '{Label(groupPath, name)}'";

    // Root-relative, '/'-separated folder of an item: <device>/<section>/<group path>.
    public static string ItemDir(string device, string kind, string groupPath)
    {
        var segments = new List<string> { DirName(device), SectionOf(kind) };
        if (!string.IsNullOrEmpty(groupPath))
            segments.AddRange(groupPath.Split('/').Select(DirName));
        return string.Join("/", segments);
    }

    public static string GroupDir(string deviceDir, string sectionName, string groupPath)
    {
        var segments = new List<string> { deviceDir, sectionName };
        if (!string.IsNullOrEmpty(groupPath))
            segments.AddRange(groupPath.Split('/').Select(DirName));
        return Path.Combine(segments.ToArray());
    }

    public static string Label(string groupPath, string name) =>
        string.IsNullOrEmpty(groupPath) ? name : $"{groupPath}/{name}";

    public static string EnsureExtension(string fileName) =>
        string.IsNullOrEmpty(Path.GetExtension(fileName)) ? fileName + ".txt" : fileName;

    public static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    // A folder name: Windows strips trailing spaces and dots from a folder it creates, so a group
    // 'Valve fwd / rev' (segments 'Valve fwd ' and ' rev') would be looked up under a name it
    // doesn't have. File names keep them - the extension follows.
    public static string DirName(string name)
    {
        name = Sanitize(name).TrimEnd(' ', '.');
        return name.Length == 0 ? "_" : name;
    }

    // Groups with no table anywhere below them - otherwise empty groups would be invisible.
    public static IEnumerable<string> EmptyGroups(IEnumerable<string> tableGroupPaths, IReadOnlyList<string> groups)
    {
        var used = tableGroupPaths.ToList();
        return groups.Where(g => !used.Any(p => p == g || p.StartsWith(g + "/")));
    }

    // Best-effort recursive group creation so the create calls' "group must already
    // exist" requirement doesn't block restoring a full
    // tree into an empty/new PLC or HMI. Failures (including "already exists") are ignored here - a
    // genuine problem still surfaces from the subsequent create call itself right after this runs.
    // Only what's below the longest part of the path that exists is created: a group name may
    // contain '/' itself ('Valve fwd / rev'), so creating every '/'-segment would add wrong groups.
    // ensured: group paths already handled in this import, so each folder is created (or found to
    // exist - a create that throws, which is slow over Openness) once rather than once per item.
    public static async Task EnsureGroupPath(string groupPath, Func<string, Task<bool>> exists, Func<string, string, Task> createGroup, ISet<string> ensured)
    {
        if (string.IsNullOrEmpty(groupPath)) return;
        var end = groupPath.Length;
        while (end > 0 && !ensured.Contains(groupPath.Substring(0, end)) && !await exists(groupPath.Substring(0, end)))
            end = groupPath.LastIndexOf('/', end - 1);
        if (end <= 0) end = 0;
        var current = groupPath.Substring(0, end);
        if (end > 0) ensured.Add(current);
        if (end == groupPath.Length) return;

        foreach (var segment in groupPath.Substring(end == 0 ? 0 : end + 1).Split('/'))
        {
            var path = string.IsNullOrEmpty(current) ? segment : $"{current}/{segment}";
            if (ensured.Add(path)) await createGroup(current, segment);
            current = path;
        }
    }

    // '<root>/.base/<file path>': each file of an object-model format (json) as last read. Such a
    // file is written property by property through Openness, which takes minutes for a large
    // screen, so a write diffs the file against this copy and writes only what changed.
    public const string BaseDirName = ".base";

    public static bool KeepsBase(string path) => SourceTreeManifest.FormatOf(path) == SourceFormat.Json;

    public static string BasePath(string root, string path) => Path.Combine(root, BaseDirName, path);

    public static void WriteBase(string root, string path, string content)
    {
        var file = BasePath(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content, new UTF8Encoding(false));
    }

    // The file as last read, if the project is known to still match it: the write checked the item
    // for changes in TIA Portal (context.Baseline set) and the copy's hash matches the manifest.
    public static string? ReadBase(WriteContext context, string path)
    {
        if (context.Baseline is not { } manifest) return null;
        var file = BasePath(context.Root, path);
        var entry = manifest.Items.FirstOrDefault(i => i.Files.ContainsKey(path));
        if (entry == null || !File.Exists(file)) return null;
        var content = File.ReadAllText(file);
        return SourceTreeManifest.Hash(content) == entry.Files[path].Hash ? content : null;
    }

    // Marks a report line of an item written from its raw XML.
    public static string FromXml(ImportListItem item) => item.Format == SourceFormat.Xml ? " (from XML)" : "";

    // The item's '<name>.xml', or null after logging that it's missing.
    public static string? ReadXml(WriteContext context, ISourceKind kind, ImportListItem item, WriteLog log)
    {
        var file = Path.Combine(GroupDir(context.DeviceDir, kind.Section, item.GroupPath), Sanitize(item.ItemName) + SourceFormat.Extension(SourceFormat.Xml));
        if (File.Exists(file)) return File.ReadAllText(file);
        log.Failed.Add($"{Describe(kind.Name, item.GroupPath, item.ItemName)}: no .xml file found at {file}");
        return null;
    }

    // Writes one item from its raw XML right away (kinds without dependency layers): replaces it
    // when it exists, creates it (and its group) when it doesn't.
    public static async Task WriteXml(WriteContext context, ISourceKind kind, ImportListItem item, WriteLog log, bool exists, ISet<string> ensuredGroups,
        Func<string, Task<bool>> groupExists, Func<string, string, Task> createGroup, Func<string, Task<ImportResult>> import)
    {
        var label = Describe(kind.Name, item.GroupPath, item.ItemName) + FromXml(item);
        var xml = ReadXml(context, kind, item, log);
        if (xml == null) return;
        if (exists && !context.OverwriteExisting) { log.Skipped.Add($"{label} - already exists, overwriteExisting=false"); return; }
        if (!exists && !context.CreateMissing) { log.Skipped.Add($"{label} - does not exist, createMissing=false"); return; }
        if (context.DryRun) { (exists ? log.Updated : log.Created).Add($"{label} (dry run)"); return; }

        if (!exists) await EnsureGroupPath(item.GroupPath, groupExists, createGroup, ensuredGroups);
        var result = await import(xml);
        if (!result.Success) { log.Failed.Add($"{label}: {string.Join(" | ", result.Messages)}"); return; }
        (exists ? log.Updated : log.Created).Add(label);
        context.Written.Add(item);
    }

    public static string DeletedTags(WriteTagsResult result)
    {
        var deleted = result.Messages.Where(m => m.StartsWith("Deleted '")).Select(m => m.Substring("Deleted ".Length).TrimEnd('.')).ToList();
        return deleted.Count == 0 ? "" : $", deleted {string.Join(", ", deleted)}";
    }
}
