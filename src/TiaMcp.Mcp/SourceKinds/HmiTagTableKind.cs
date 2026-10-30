using TiaMcp.Openness;
using static TiaMcp.Mcp.SourceTree;

namespace TiaMcp.Mcp;

// WinCC Unified HMI tag tables: one CSV per table (see HmiTagTableCsv), in groups. Classic WinCC
// tag tables share the kind name and folder, but are an HmiClassicKind.
internal sealed class HmiTagTableKind : ISourceKind
{
    public string Name => "HMI tag table";
    public string Section => "HMI tags";
    public DeviceFamily Family => DeviceFamily.HmiUnified;
    public string? FixedName => null;
    public bool XmlNeedsCompile => false;
    public IReadOnlyList<string> Formats { get; } = new[] { SourceFormat.Csv };

    public async Task<IReadOnlyList<ListedEntry>> List(TiaSession session, string hmi)
    {
        var tables = await session.ListHmiTagTablesAsync(hmi);
        var entries = tables.Select(t => new ListedEntry(t.GroupPath, t.Name, Describe(Name, t.GroupPath, t.Name), Formats)).ToList();
        entries.AddRange(EmptyGroups(tables.Select(t => t.GroupPath), await session.ListHmiTagTableGroupsAsync(hmi))
            .Select(g => new ListedEntry(g, "", $"HMI tag table group '{g}' (empty)", Array.Empty<string>(), isItem: false)));
        return entries;
    }

    public async Task Export(TiaSession session, string hmi, Func<string, string, IReadOnlyCollection<string>?> formats, ExportOptions options, List<ExportedItem> items)
    {
        foreach (var table in (await session.ListHmiTagTablesAsync(hmi)).Where(t => formats(t.GroupPath, t.Name) != null))
        {
            try
            {
                var result = await session.ReadHmiTagTableAsync(hmi, table.Name, options.HmiTagLength);
                items.Add(ExportedItem.FromCsv(hmi, Name, table.GroupPath, table.Name, result.Success ? HmiTagTableCsv.Format(result.Tags, options.HmiTagLength) : null, result.Error));
            }
            catch (Exception ex)
            {
                items.Add(ExportedItem.FromCsv(hmi, Name, table.GroupPath, table.Name, null, ex.Message));
            }
        }
    }

    // In a working copy, tags of an existing table that its CSV no longer has are deleted.
    public async Task<WriteLog> Write(WriteContext context, IReadOnlyList<ImportListItem> items)
    {
        var log = new WriteLog();
        var session = context.Session;
        var hmi = context.Device;
        var existingTables = (await session.ListHmiTagTablesAsync(hmi)).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ensuredGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            var label = Describe(Name, item.GroupPath, item.ItemName);
            var file = Path.Combine(GroupDir(context.DeviceDir, Section, item.GroupPath), Sanitize(item.ItemName) + ".csv");
            if (!File.Exists(file)) { log.Failed.Add($"{label}: no .csv file found at {file}"); continue; }

            HmiTagSpec[] tags;
            try { tags = HmiTagTableCsv.Parse(File.ReadAllText(file)); }
            catch (FormatException ex) { log.Failed.Add($"{label}: {ex.Message}"); continue; }

            if (existingTables.Contains(item.ItemName))
            {
                if (!context.OverwriteExisting) { log.Skipped.Add($"{label} - already exists, overwriteExisting=false"); continue; }
                if (context.DryRun) { log.Updated.Add($"{label} (dry run, {tags.Length} tag(s))"); continue; }

                var result = await session.WriteHmiTagTableAsync(hmi, item.ItemName, tags, context.WorkingCopy);
                if (result.Success) { log.Updated.Add($"{label} ({tags.Length} tag(s){DeletedTags(result)})"); context.Written.Add(item); }
                else log.Failed.Add($"{label}: {string.Join(" | ", result.Messages)}");
            }
            else
            {
                if (!context.CreateMissing) { log.Skipped.Add($"{label} - does not exist, createMissing=false"); continue; }
                if (context.DryRun) { log.Created.Add($"{label} (dry run, {tags.Length} tag(s))"); continue; }

                await EnsureGroupPath(item.GroupPath, path => session.HmiTagTableGroupExistsAsync(hmi, path), (parent, segment) => session.CreateHmiTagTableGroupAsync(hmi, parent, segment), ensuredGroups);
                var createResult = await session.CreateHmiTagTableAsync(hmi, item.GroupPath, item.ItemName);
                if (!createResult.Success) { log.Failed.Add($"{label}: {createResult.Error}"); continue; }

                var writeResult = await session.WriteHmiTagTableAsync(hmi, item.ItemName, tags);
                if (writeResult.Success) { log.Created.Add($"{label} ({tags.Length} tag(s))"); context.Written.Add(item); }
                else log.Failed.Add($"{label}: created empty, but writing tags failed: {string.Join(" | ", writeResult.Messages)}");
            }
        }

        return log;
    }
}
