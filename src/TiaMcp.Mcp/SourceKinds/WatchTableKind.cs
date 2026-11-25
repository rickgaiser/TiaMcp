using TiaMcp.Openness;
using static TiaMcp.Mcp.SourceTree;

namespace TiaMcp.Mcp;

// PLC watch tables, in groups, and the force table (read-only) - one CSV per table (see
// WatchTableCsv), or the raw XML export. Both kinds share TIA Portal's folder; a new file there is
// a watch table (Watch comes first in SourceKinds.All).
internal sealed class WatchTableKind : ISourceKind
{
    public static WatchTableKind Watch { get; } = new(force: false);
    public static WatchTableKind Force { get; } = new(force: true);

    private readonly bool _force;
    private WatchTableKind(bool force) { _force = force; }

    public string Name => _force ? "force table" : "watch table";
    public string Section => "Watch and force tables";
    public DeviceFamily Family => DeviceFamily.Plc;
    public string? FixedName => null;
    public bool XmlNeedsCompile => false;
    public MarkTarget? MarkTarget => _force ? null : Openness.MarkTarget.WatchTable;
    public IReadOnlyList<string> Formats { get; } = new[] { SourceFormat.Csv, SourceFormat.Xml };

    public async Task<IReadOnlyList<ListedEntry>> List(TiaSession session, string plc)
    {
        var all = await session.ListWatchTablesAsync(plc);
        var tables = all.Where(t => t.IsForceTable == _force).ToList();
        var entries = tables.Select(t => new ListedEntry(t.GroupPath, t.Name, Describe(Name, t.GroupPath, t.Name) + (_force ? " (read-only)" : ""), Formats)).ToList();
        if (!_force)
            entries.AddRange(EmptyGroups(all.Select(t => t.GroupPath), await session.ListWatchTableGroupsAsync(plc))
                .Select(g => new ListedEntry(g, "", $"watch table group '{g}' (empty)", Array.Empty<string>(), isItem: false)));
        return entries;
    }

    public async Task Export(TiaSession session, string plc, Func<string, string, IReadOnlyCollection<string>?> formats, ExportOptions options, List<ExportedItem> items, ExportingItem? exporting)
    {
        foreach (var (t, result) in await session.ReadAllWatchTablesAsync(plc, _force, options.OpenWatchTables, formats, exporting))
            items.Add(ExportedItem.FromDocuments(plc, Name, t.GroupPath, t.Name, result, Formats));
    }

    // A watch table is written whole: from its CSV or its XML, it replaces the table, or creates
    // it. Before that, TIA Portal must resolve every row; otherwise nothing is written and the
    // rows it can't resolve are reported (see TiaConnection.WriteWatchTable).
    public async Task<WriteLog> Write(WriteContext context, IReadOnlyList<ImportListItem> items)
    {
        var log = new WriteLog();
        if (_force)
        {
            foreach (var item in items)
                log.Failed.Add($"{Describe(Name, item.GroupPath, item.ItemName)}: the force table is read-only - change it in TIA Portal.");
            return log;
        }

        var session = context.Session;
        var plc = context.Device;
        var existing = (await session.ListWatchTablesAsync(plc)).Where(t => !t.IsForceTable).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ensuredGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var done = 0;
        foreach (var item in items)
        {
            var label = Describe(Name, item.GroupPath, item.ItemName) + FromXml(item);
            if (context.Progress.Cancelled) { log.Skipped.Add($"{label} - not written, the call was cancelled"); continue; }
            var exists = existing.Contains(item.ItemName);

            string? xml = null;
            WatchTable? table = null;
            if (item.Format == SourceFormat.Xml)
            {
                xml = ReadXml(context, this, item, log);
                if (xml == null) continue;
            }
            else
            {
                var file = Path.Combine(GroupDir(context.DeviceDir, Section, item.GroupPath), Sanitize(item.ItemName) + SourceFormat.Extension(SourceFormat.Csv));
                if (!File.Exists(file)) { log.Failed.Add($"{label}: no .csv file found at {file}"); continue; }
                try { table = WatchTableCsv.Parse(item.ItemName, File.ReadAllText(file)); }
                catch (FormatException ex) { log.Failed.Add($"{label}: {ex.Message}"); continue; }
            }

            if (exists && !context.OverwriteExisting) { log.Skipped.Add($"{label} - already exists, overwriteExisting=false"); continue; }
            if (!exists && !context.CreateMissing) { log.Skipped.Add($"{label} - does not exist, createMissing=false"); continue; }
            var rows = table == null ? "" : $", {table.Rows.Count(r => !r.IsComment)} row(s)";
            if (context.DryRun)
            {
                (exists ? log.Updated : log.Created).Add($"{label} (dry run{rows}; the symbols are checked by the real write)");
                continue;
            }

            if (!exists) await EnsureGroupPath(item.GroupPath, path => session.WatchTableGroupExistsAsync(plc, path), (parent, segment) => session.CreateWatchTableGroupAsync(plc, parent, segment), ensuredGroups);
            context.Progress.Item($"{plc}: {Name}", ++done, items.Count, label);
            var result = table != null
                ? await session.WriteWatchTableAsync(plc, item.GroupPath, table)
                : await session.WriteWatchTableXmlAsync(plc, item.GroupPath, item.ItemName, xml!);
            if (!result.Success)
            {
                var bad = result.BadRows.Count == 0 ? "" : $" {result.BadRows.Count} row(s): {string.Join(", ", result.BadRows)}.";
                log.Failed.Add($"{label}: {result.Error}{bad}");
                continue;
            }
            (exists ? log.Updated : log.Created).Add(table == null ? label : $"{label} ({table.Rows.Count(r => !r.IsComment)} row(s))");
            context.Written.Add(item);
        }
        return log;
    }
}
