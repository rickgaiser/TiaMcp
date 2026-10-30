using TiaMcp.Openness;
using static TiaMcp.Mcp.SourceTree;

namespace TiaMcp.Mcp;

// WinCC Unified screens. Openness has no export for them, so a screen is JSON built from the object
// model (see HmiScreenConverter and ScreenJson), in its screen groups. Classic WinCC screens share
// the kind name and folder, but are an HmiClassicKind.
internal sealed class HmiScreenKind : ISourceKind
{
    public string Name => "HMI screen";
    public string Section => "Screens";
    public DeviceFamily Family => DeviceFamily.HmiUnified;
    public string? FixedName => null;
    public bool XmlNeedsCompile => false;
    public IReadOnlyList<string> Formats { get; } = new[] { SourceFormat.Json };

    public async Task<IReadOnlyList<ListedEntry>> List(TiaSession session, string hmi)
    {
        var screens = await session.ListHmiScreensAsync(hmi);
        var entries = screens.Select(s => new ListedEntry(s.GroupPath, s.Name, Describe(Name, s.GroupPath, s.Name), Formats)).ToList();
        entries.AddRange(EmptyGroups(screens.Select(s => s.GroupPath), await session.ListHmiScreenGroupsAsync(hmi))
            .Select(g => new ListedEntry(g, "", $"HMI screen group '{g}' (empty)", Array.Empty<string>(), isItem: false)));
        return entries;
    }

    public async Task Export(TiaSession session, string hmi, Func<string, string, IReadOnlyCollection<string>?> formats, ExportOptions options, List<ExportedItem> items)
    {
        foreach (var screen in (await session.ListHmiScreensAsync(hmi)).Where(s => formats(s.GroupPath, s.Name) != null))
        {
            var result = await session.ReadHmiScreenAsync(hmi, screen.Name);
            var files = result.Success
                ? new List<(string, string, bool)> { (FilePath(hmi, screen.GroupPath, screen.Name), result.Json!, false) }
                : new List<(string, string, bool)>();
            items.Add(new ExportedItem(hmi, Name, screen.GroupPath, screen.Name, files, result.Success ? null : result.Error ?? "export failed"));
        }
    }

    // Items are matched by name; in a working copy, items that the file no longer has are deleted.
    // With a baseline (see WriteContext.Baseline) only what changed since the read is written: a
    // whole screen goes property by property through Openness, which takes minutes for a large one.
    public async Task<WriteLog> Write(WriteContext context, IReadOnlyList<ImportListItem> items)
    {
        var log = new WriteLog();
        var session = context.Session;
        var hmi = context.Device;
        var existingScreens = (await session.ListHmiScreensAsync(hmi)).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ensuredGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            var label = Describe(Name, item.GroupPath, item.ItemName);
            var path = FilePath(hmi, item.GroupPath, item.ItemName);
            var file = Path.Combine(context.Root, path);
            if (!File.Exists(file)) { log.Failed.Add($"{label}: no .json file found at {file}"); continue; }
            var json = File.ReadAllText(file);

            var exists = existingScreens.Contains(item.ItemName);
            if (exists && !context.OverwriteExisting) { log.Skipped.Add($"{label} - already exists, overwriteExisting=false"); continue; }
            if (!exists && !context.CreateMissing) { log.Skipped.Add($"{label} - does not exist, createMissing=false"); continue; }
            if (context.DryRun) { (exists ? log.Updated : log.Created).Add($"{label} (dry run)"); continue; }

            if (!exists)
            {
                await EnsureGroupPath(item.GroupPath, path => session.HmiScreenGroupExistsAsync(hmi, path), (parent, segment) => session.CreateHmiScreenGroupAsync(hmi, parent, segment), ensuredGroups);
                var createResult = await session.CreateHmiScreenAsync(hmi, item.GroupPath, item.ItemName);
                if (!createResult.Success) { log.Failed.Add($"{label}: {createResult.Error}"); continue; }
                existingScreens.Add(item.ItemName);
            }

            var baseJson = exists ? ReadBase(context, path) : null;
            var result = await session.WriteHmiScreenAsync(hmi, item.ItemName, json, context.WorkingCopy, baseJson);
            var counts = !exists ? $"{result.Created} item(s)" : $"{result.Created} item(s) created, {result.Updated} {(baseJson == null ? "written" : "changed")}, {result.Deleted} deleted";
            if (result.Success)
            {
                (exists ? log.Updated : log.Created).Add($"{label} ({counts})");
                context.Written.Add(item);
            }
            else
            {
                // Partly applied: the file keeps the edit, so the next write needs force=true.
                var errors = result.Messages.Where(m => m.StartsWith("FAILED") || m.StartsWith("Invalid JSON")).ToList();
                log.Failed.Add($"{label}{(exists ? "" : " (created)")}: {counts}, but: {string.Join(" | ", errors.Count > 0 ? errors : result.Messages)}");
            }
        }

        return log;
    }

    private string FilePath(string hmi, string groupPath, string name) =>
        $"{ItemDir(hmi, Name, groupPath)}/{Sanitize(name)}{SourceFormat.Extension(SourceFormat.Json)}";
}
