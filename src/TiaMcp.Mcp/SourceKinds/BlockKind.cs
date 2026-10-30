using TiaMcp.Openness;
using static TiaMcp.Mcp.SourceTree;

namespace TiaMcp.Mcp;

// Program blocks, in groups: TIA Portal's own document format (.s7dcl/.s7res), a GRAPH block as
// '.graph.il', a whole-block STL block as '.awl' (see read_source_tree for the read-only
// companions) - or any block as the raw XML export.
internal sealed class BlockKind : ISourceKind
{
    public string Name => "block";
    public string Section => "Program blocks";
    public DeviceFamily Family => DeviceFamily.Plc;
    public string? FixedName => null;
    public bool XmlNeedsCompile => true;
    public IReadOnlyList<string> Formats { get; } = new[] { SourceFormat.S7dcl, SourceFormat.GraphIL, SourceFormat.Awl, SourceFormat.Xml };

    // A block's own format follows from its language (see TiaConnection.ReadBlock); every block
    // also has the raw XML.
    private static IReadOnlyList<string> FormatsOf(string language) => language switch
    {
        "GRAPH" => new[] { SourceFormat.GraphIL, SourceFormat.Xml },
        "STL" => new[] { SourceFormat.Awl, SourceFormat.Xml },
        _ => new[] { SourceFormat.S7dcl, SourceFormat.Xml },
    };

    public async Task<IReadOnlyList<ListedEntry>> List(TiaSession session, string plc) =>
        (await session.ListBlocksAsync(plc))
            .Select(b => new ListedEntry(b.GroupPath, b.Name, $"{Describe(Name, b.GroupPath, b.Name)} [{b.Language}]{(b.Consistent ? "" : " INCONSISTENT")}", FormatsOf(b.Language), inconsistent: !b.Consistent))
            .ToList();

    public async Task Export(TiaSession session, string plc, Func<string, string, IReadOnlyCollection<string>?> formats, ExportOptions options, List<ExportedItem> items)
    {
        foreach (var (b, result) in await session.ReadAllBlocksAsync(plc, formats))
        {
            var item = ExportedItem.FromDocuments(plc, Name, b.GroupPath, b.Name, result, FormatsOf(b.Language));
            foreach (var dcl in result.Documents.Where(d => d.FileName.EndsWith(".s7dcl", StringComparison.OrdinalIgnoreCase)))
                item.Warnings.AddRange(S7dclSource.FindWriteProblems(dcl.Content));
            items.Add(item);
        }
    }

    // Global DBs and called FBs before their callers and instance DBs - see DependencyWrites.
    public async Task<WriteLog> Write(WriteContext context, IReadOnlyList<ImportListItem> items)
    {
        var log = new WriteLog();
        var session = context.Session;
        var plc = context.Device;
        var existingBlocks = (await session.ListBlocksAsync(plc)).Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ensuredGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var writes = new List<PendingWrite>();
        string? graphTemplateName = null;
        var graphTemplateSearched = false;

        foreach (var item in items)
        {
            var label = Describe(Name, item.GroupPath, item.ItemName);
            if (item.Format == SourceFormat.Xml)
            {
                // Any block, including GRAPH and blocks with embedded STL networks: the import
                // replaces or creates it as a whole.
                var xml = ReadXml(context, this, item, log);
                if (xml == null) continue;
                var exists = existingBlocks.Contains(item.ItemName);
                if (exists && !context.OverwriteExisting) { log.Skipped.Add($"{label} - already exists, overwriteExisting=false"); continue; }
                if (!exists && !context.CreateMissing) { log.Skipped.Add($"{label} - does not exist, createMissing=false"); continue; }
                if (context.DryRun) { (exists ? log.Updated : log.Created).Add($"{label}{FromXml(item)} (dry run)"); continue; }

                if (!exists) await EnsureGroupPath(item.GroupPath, path => session.BlockGroupExistsAsync(plc, path), (parent, segment) => session.CreateBlockGroupAsync(plc, parent, segment), ensuredGroups);
                var (xmlGroup, xmlName) = (item.GroupPath, item.ItemName);
                writes.Add(new PendingWrite(item, label + FromXml(item), new List<BlockDocument> { new(xmlName + SourceFormat.Extension(SourceFormat.Xml), xml) }, !exists,
                    () => session.ImportBlockXmlAsync(plc, xmlGroup, xmlName, xml)));
                continue;
            }

            var (docs, error) = ReadDocuments(GroupDir(context.DeviceDir, Section, item.GroupPath), item.ItemName);
            if (docs == null)
            {
                log.Failed.Add($"{label}: {error}");
                continue;
            }

            // What TIA Portal's .s7dcl import would get wrong - refused, also in a dry run, so the
            // file can be worked around first.
            var problems = docs.Where(d => d.FileName.EndsWith(".s7dcl", StringComparison.OrdinalIgnoreCase))
                .SelectMany(d => S7dclSource.FindWriteProblems(d.Content)).ToList();
            if (problems.Count > 0)
            {
                log.Failed.Add($"{label}: refused, the .s7dcl import would change it - {string.Join(" | ", problems)}");
                continue;
            }

            if (existingBlocks.Contains(item.ItemName))
            {
                if (!context.OverwriteExisting) { log.Skipped.Add($"{label} - already exists, overwriteExisting=false"); continue; }
                if (context.DryRun) { log.Updated.Add($"{label} (dry run)"); continue; }

                var name = item.ItemName;
                writes.Add(new PendingWrite(item, label, docs, false, () => session.WriteBlockAsync(plc, name, docs)));
                continue;
            }

            if (!context.CreateMissing) { log.Skipped.Add($"{label} - does not exist, createMissing=false"); continue; }

            var isGraph = docs.Count == 1 && docs[0].FileName.EndsWith(".graph.il", StringComparison.OrdinalIgnoreCase);
            if (isGraph)
            {
                if (!graphTemplateSearched)
                {
                    graphTemplateName = (await session.ListBlocksAsync(plc)).FirstOrDefault(b => b.Language == "GRAPH")?.Name;
                    graphTemplateSearched = true;
                }
                if (graphTemplateName == null)
                {
                    log.Skipped.Add($"{label} - no existing GRAPH block in PLC '{plc}' to use as a clone template; create one manually first");
                    continue;
                }
                if (context.DryRun) { log.Created.Add($"{label} (dry run, GRAPH via template '{graphTemplateName}')"); continue; }

                var (graphGroup, graphName, template) = (item.GroupPath, item.ItemName, graphTemplateName);
                writes.Add(new PendingWrite(item, $"{label} (GRAPH, template '{template}')", docs, true,
                    () => session.CreateGraphBlockAsync(plc, graphGroup, graphName, template, docs[0].Content)));
                continue;
            }

            if (context.DryRun) { log.Created.Add($"{label} (dry run)"); continue; }

            await EnsureGroupPath(item.GroupPath, path => session.BlockGroupExistsAsync(plc, path), (parent, segment) => session.CreateBlockGroupAsync(plc, parent, segment), ensuredGroups);
            var (groupPath, blockName) = (item.GroupPath, item.ItemName);
            // Whole-block STL can't go through ImportFromDocuments - it goes through 'Generate blocks from source' (CreateStlBlock).
            var isStl = docs.Count == 1 && docs[0].FileName.EndsWith(".awl", StringComparison.OrdinalIgnoreCase);
            writes.Add(new PendingWrite(item, label, docs, true, isStl
                ? () => session.CreateStlBlockAsync(plc, groupPath, blockName, docs)
                : () => session.CreateBlockAsync(plc, groupPath, blockName, docs, checkExisting: false)));
        }

        await DependencyWrites.Write(context, writes, log, compileBetweenLayers: false);
        return log;
    }

    // Locates the on-disk source file(s) read_source_tree wrote for one block and packages them as the
    // BlockDocument(s) WriteBlock/CreateBlock expect - mirroring ExportBlocks' own
    // extension priority (GRAPH's '.graph.il', whole-block STL's '.awl', else the normal
    // '.s7dcl'[+'.s7res'] pair). '.interface.txt' and '.stl-networks.xml' are read-only companions
    // (see read_source_tree) and are never picked up here.
    private static (List<BlockDocument>? Docs, string? Error) ReadDocuments(string dir, string blockName)
    {
        if (!Directory.Exists(dir)) return (null, $"directory not found: {dir}");
        var sanitizedName = Sanitize(blockName);

        var graphFile = Path.Combine(dir, sanitizedName + ".graph.il");
        if (File.Exists(graphFile))
            return (new List<BlockDocument> { new($"{blockName}.graph.il", File.ReadAllText(graphFile)) }, null);

        var awlFile = Path.Combine(dir, sanitizedName + ".awl");
        if (File.Exists(awlFile))
            return (new List<BlockDocument> { new($"{blockName}.awl", File.ReadAllText(awlFile)) }, null);

        var dclFile = Path.Combine(dir, sanitizedName + ".s7dcl");
        if (!File.Exists(dclFile))
        {
            var txtFile = Path.Combine(dir, sanitizedName + ".txt");
            if (File.Exists(txtFile))
                return (new List<BlockDocument> { new($"{blockName}.s7dcl", File.ReadAllText(txtFile)) }, null);
            return (null, $"no source file found in '{dir}' (expected {sanitizedName}.s7dcl, .graph.il, or .awl)");
        }

        var docs = new List<BlockDocument> { new($"{blockName}.s7dcl", File.ReadAllText(dclFile)) };
        var resFile = Path.Combine(dir, sanitizedName + ".s7res");
        if (File.Exists(resFile)) docs.Add(new BlockDocument($"{blockName}.s7res", File.ReadAllText(resFile)));
        return (docs, null);
    }
}
