using TiaMcp.Openness;
using static TiaMcp.Mcp.SourceTree;

namespace TiaMcp.Mcp;

// PLC alarm text lists: one CSV per list (see AlarmTextListCsv), no groups. Openness can't create
// one, and imports all lists of a PLC at once.
internal sealed class PlcAlarmTextListKind : ISourceKind
{
    public string Name => "alarm text list";
    public string Section => "PLC alarm text lists";
    public DeviceFamily Family => DeviceFamily.Plc;
    public string? FixedName => null;
    public bool XmlNeedsCompile => false;
    public MarkTarget? MarkTarget => null;
    public IReadOnlyList<string> Formats { get; } = new[] { SourceFormat.Csv };

    public async Task<IReadOnlyList<ListedEntry>> List(TiaSession session, string plc) =>
        (await session.ListPlcAlarmTextListNamesAsync(plc)).Select(n => new ListedEntry("", n, Describe(Name, "", n), Formats)).ToList();

    public async Task Export(TiaSession session, string plc, Func<string, string, IReadOnlyCollection<string>?> formats, ExportOptions options, List<ExportedItem> items)
    {
        var names = (await session.ListPlcAlarmTextListNamesAsync(plc)).Where(n => formats("", n) != null).ToList();
        if (names.Count == 0) return;
        var read = await session.ReadPlcAlarmTextListsAsync(plc);
        foreach (var name in names)
        {
            var list = read.Lists.FirstOrDefault(l => l.Name == name);
            items.Add(ExportedItem.FromCsv(plc, Name, "", name,
                list == null ? null : AlarmTextListCsv.Format(list),
                read.Error ?? (list == null ? "not in the text list export" : null)));
        }
    }

    // After the blocks: the text lists' messages belong to the alarms the blocks define.
    public async Task<WriteLog> Write(WriteContext context, IReadOnlyList<ImportListItem> items)
    {
        var log = new WriteLog();
        var plc = context.Device;
        var existing = new HashSet<string>(await context.Session.ListPlcAlarmTextListNamesAsync(plc), StringComparer.Ordinal);

        var lists = new List<(ImportListItem Item, PlcAlarmTextList List)>();
        foreach (var item in items)
        {
            var label = Describe(Name, "", item.ItemName);
            var file = Path.Combine(context.DeviceDir, Section, Sanitize(item.ItemName) + ".csv");
            if (!File.Exists(file)) { log.Failed.Add($"{label}: no .csv file found at {file}"); continue; }
            if (!existing.Contains(item.ItemName))
            {
                if (context.CreateMissing) log.Failed.Add($"{label}: the list doesn't exist in PLC '{plc}' - text lists can't be created through Openness, create it in TIA Portal first");
                else log.Skipped.Add($"{label} - does not exist, createMissing=false");
                continue;
            }
            if (!context.OverwriteExisting) { log.Skipped.Add($"{label} - already exists, overwriteExisting=false"); continue; }
            try { lists.Add((item, AlarmTextListCsv.Parse(item.ItemName, File.ReadAllText(file)))); }
            catch (FormatException ex) { log.Failed.Add($"{label}: {ex.Message}"); }
        }
        if (lists.Count == 0) return log;

        // All lists of the PLC in one import: TIA Portal replaces whole lists, so one fresh export
        // is patched with every list being written.
        var result = await context.Session.WritePlcAlarmTextListsAsync(plc, lists.Select(l => l.List).ToList(), context.WorkingCopy, context.DryRun);
        foreach (var (item, list) in lists)
        {
            var change = result.Changes.FirstOrDefault(c => c.List == list.Name);
            var label = Describe(Name, "", list.Name);
            if (!result.Success) { log.Failed.Add($"{label}: {result.Message}"); continue; }
            var counts = change == null ? "" : $"{change.Added.Count} new, {change.Changed.Count} changed, {change.Deleted.Count} deleted";
            var detail = change == null ? "" : string.Concat(change.Added.Take(5).Select(a => $"\n      + {a}"))
                + string.Concat(change.Changed.Take(5).Select(c => $"\n      ~ {c}"))
                + string.Concat(change.Deleted.Take(5).Select(d => $"\n      - {d}"));
            log.Updated.Add($"{label} ({(context.DryRun ? "dry run, " : "")}{counts}){detail}");
            if (!context.DryRun) context.Written.Add(item);
        }
        if (result.Success && result.Message != null) log.Updated.Add($"alarm text lists: {result.Message}");
        return log;
    }
}
