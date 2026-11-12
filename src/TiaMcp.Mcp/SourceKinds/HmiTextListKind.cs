using TiaMcp.Openness;
using static TiaMcp.Mcp.SourceTree;

namespace TiaMcp.Mcp;

// WinCC Unified text lists: one CSV per list in 'Text lists/' (the same columns as a PLC alarm
// text list, see AlarmTextListCsv), no groups. Openness reads and writes them only all at once,
// through the export/import of every list of the HMI (see TiaConnection.HmiTextLists); a new CSV
// creates a list. Classic WinCC text lists share the kind name and folder, but are an HmiClassicKind.
internal sealed class HmiTextListKind : ISourceKind
{
    public string Name => "HMI text list";
    public string Section => "Text lists";
    public DeviceFamily Family => DeviceFamily.HmiUnified;
    public string? FixedName => null;
    public bool XmlNeedsCompile => false;
    public MarkTarget? MarkTarget => Openness.MarkTarget.HmiTextList;
    public IReadOnlyList<string> Formats { get; } = new[] { SourceFormat.Csv };

    public async Task<IReadOnlyList<ListedEntry>> List(TiaSession session, string hmi) =>
        (await session.ListHmiTextListNamesAsync(hmi)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new ListedEntry("", n, Describe(Name, "", n), Formats)).ToList();

    public async Task Export(TiaSession session, string hmi, Func<string, string, IReadOnlyCollection<string>?> formats, ExportOptions options, List<ExportedItem> items, ExportingItem? exporting)
    {
        var names = (await session.ListHmiTextListNamesAsync(hmi)).Where(n => formats("", n) != null).ToList();
        if (names.Count == 0) return;
        // One export holds every list.
        exporting?.Invoke(1, 1, "", names.Count == 1 ? names[0] : $"{names.Count} lists");
        var read = await session.ReadHmiTextListsAsync(hmi);
        foreach (var name in names)
        {
            var list = read.Lists.FirstOrDefault(l => l.Name == name);
            items.Add(ExportedItem.FromCsv(hmi, Name, "", name,
                list == null ? null : AlarmTextListCsv.Format(list),
                read.Error ?? (list == null ? "not in the text list export" : null)));
        }
    }

    // Before the screens, which show the lists. All lists in one import; in a working copy a
    // removed row deletes that entry.
    public async Task<WriteLog> Write(WriteContext context, IReadOnlyList<ImportListItem> items)
    {
        var log = new WriteLog();
        var hmi = context.Device;
        var existing = new HashSet<string>(await context.Session.ListHmiTextListNamesAsync(hmi), StringComparer.Ordinal);

        var lists = new List<(ImportListItem Item, PlcAlarmTextList List)>();
        foreach (var item in items)
        {
            var label = Describe(Name, "", item.ItemName);
            var file = Path.Combine(context.DeviceDir, Section, Sanitize(item.ItemName) + ".csv");
            if (!File.Exists(file)) { log.Failed.Add($"{label}: no .csv file found at {file}"); continue; }
            var exists = existing.Contains(item.ItemName);
            if (exists && !context.OverwriteExisting) { log.Skipped.Add($"{label} - already exists, overwriteExisting=false"); continue; }
            if (!exists && !context.CreateMissing) { log.Skipped.Add($"{label} - does not exist, createMissing=false"); continue; }
            try { lists.Add((item, AlarmTextListCsv.Parse(item.ItemName, File.ReadAllText(file)))); }
            catch (FormatException ex) { log.Failed.Add($"{label}: {ex.Message}"); }
        }
        if (lists.Count == 0) return log;

        var result = await context.Session.WriteHmiTextListsAsync(hmi, lists.Select(l => l.List).ToList(), context.WorkingCopy, context.DryRun);
        var dryRun = context.DryRun ? "dry run, " : "";
        foreach (var (item, list) in lists)
        {
            var label = Describe(Name, "", list.Name);
            var change = result.Changes.FirstOrDefault(c => c.List == list.Name);
            var counts = change == null ? "" : $"{change.Added.Count} new, {change.Changed.Count} changed, {change.Deleted.Count} deleted";
            var detail = change == null ? "" : string.Concat(change.Added.Take(5).Select(a => $"\n      + {a}"))
                + string.Concat(change.Changed.Take(5).Select(c => $"\n      ~ {c}"))
                + string.Concat(change.Deleted.Take(5).Select(d => $"\n      - {d}"));
            if (!result.Success)
            {
                log.Failed.Add($"{label}: {result.Message}");
                // Imported but not as meant: the project changed all the same.
                if (!context.DryRun && result.Message?.StartsWith("Imported") == true) context.Touched.Add(item);
                continue;
            }
            if (change != null && change.Added.Count + change.Changed.Count + change.Deleted.Count == 0 && existing.Contains(list.Name))
            {
                log.Notes.Add($"{label}: unchanged.");
                continue;
            }
            (existing.Contains(list.Name) ? log.Updated : log.Created).Add($"{label} ({dryRun}{counts}){detail}");
            if (!context.DryRun) context.Written.Add(item);
        }
        return log;
    }
}
