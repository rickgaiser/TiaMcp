using TiaMcp.Openness;
using static TiaMcp.Mcp.SourceTree;

namespace TiaMcp.Mcp;

// PLC tag tables, in groups: one CSV per table (see TagTableCsv), or the raw XML export.
internal sealed class PlcTagTableKind : ISourceKind
{
    public string Name => "tag table";
    public string Section => "PLC tags";
    public DeviceFamily Family => DeviceFamily.Plc;
    public string? FixedName => null;
    public bool XmlNeedsCompile => false;
    public IReadOnlyList<string> Formats { get; } = new[] { SourceFormat.Csv, SourceFormat.Xml };

    public async Task<IReadOnlyList<ListedEntry>> List(TiaSession session, string plc)
    {
        var tables = await session.ListTagTablesAsync(plc);
        var entries = tables.Select(t => new ListedEntry(t.GroupPath, t.Name, Describe(Name, t.GroupPath, t.Name), Formats)).ToList();
        entries.AddRange(EmptyGroups(tables.Select(t => t.GroupPath), await session.ListTagTableGroupsAsync(plc))
            .Select(g => new ListedEntry(g, "", $"tag table group '{g}' (empty)", Array.Empty<string>(), isItem: false)));
        return entries;
    }

    public async Task Export(TiaSession session, string plc, Func<string, string, IReadOnlyCollection<string>?> formats, ExportOptions options, List<ExportedItem> items)
    {
        foreach (var (t, result) in await session.ReadAllTagTablesAsync(plc, formats))
            items.Add(ExportedItem.FromDocuments(plc, Name, t.GroupPath, t.Name, result, Formats));
    }

    // From the CSV, rows update or add tags, and in a working copy tags of an existing table that
    // its CSV no longer has are deleted. From the XML, the whole table is replaced.
    public async Task<WriteLog> Write(WriteContext context, IReadOnlyList<ImportListItem> items)
    {
        var log = new WriteLog();
        var session = context.Session;
        var plc = context.Device;
        var existingTables = (await session.ListTagTablesAsync(plc)).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ensuredGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            var label = Describe(Name, item.GroupPath, item.ItemName);
            if (item.Format == SourceFormat.Xml)
            {
                var (groupPath, tableName) = (item.GroupPath, item.ItemName);
                await WriteXml(context, this, item, log, existingTables.Contains(item.ItemName), ensuredGroups,
                    path => session.TagTableGroupExistsAsync(plc, path),
                    (parent, segment) => session.CreateTagTableGroupAsync(plc, parent, segment),
                    xml => session.ImportTagTableXmlAsync(plc, groupPath, tableName, xml));
                continue;
            }

            var file = Path.Combine(GroupDir(context.DeviceDir, Section, item.GroupPath), Sanitize(item.ItemName) + ".csv");
            if (!File.Exists(file)) { log.Failed.Add($"{label}: no .csv file found at {file}"); continue; }

            TagSpec[] tags;
            try { tags = TagTableCsv.Parse(File.ReadAllText(file)); }
            catch (FormatException ex) { log.Failed.Add($"{label}: {ex.Message}"); continue; }

            if (existingTables.Contains(item.ItemName))
            {
                if (!context.OverwriteExisting) { log.Skipped.Add($"{label} - already exists, overwriteExisting=false"); continue; }
                if (context.DryRun) { log.Updated.Add($"{label} (dry run, {tags.Length} tag(s))"); continue; }

                var result = await session.WriteTagTableAsync(plc, item.ItemName, tags, context.WorkingCopy);
                if (result.Success) { log.Updated.Add($"{label} ({tags.Length} tag(s){DeletedTags(result)})"); context.Written.Add(item); }
                else log.Failed.Add($"{label}: {string.Join(" | ", result.Messages)}");
            }
            else
            {
                if (!context.CreateMissing) { log.Skipped.Add($"{label} - does not exist, createMissing=false"); continue; }
                if (context.DryRun) { log.Created.Add($"{label} (dry run, {tags.Length} tag(s))"); continue; }

                await EnsureGroupPath(item.GroupPath, path => session.TagTableGroupExistsAsync(plc, path), (parent, segment) => session.CreateTagTableGroupAsync(plc, parent, segment), ensuredGroups);
                var createResult = await session.CreateTagTableAsync(plc, item.GroupPath, item.ItemName);
                if (!createResult.Success) { log.Failed.Add($"{label}: {createResult.Error}"); continue; }

                var writeResult = await session.WriteTagTableAsync(plc, item.ItemName, tags);
                if (writeResult.Success) { log.Created.Add($"{label} ({tags.Length} tag(s))"); context.Written.Add(item); }
                else log.Failed.Add($"{label}: created empty, but writing tags failed: {string.Join(" | ", writeResult.Messages)}");
            }
        }

        return log;
    }
}
