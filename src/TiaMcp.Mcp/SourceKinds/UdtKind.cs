using TiaMcp.Openness;
using static TiaMcp.Mcp.SourceTree;

namespace TiaMcp.Mcp;

// PLC data types, in groups: TIA Portal's own document format (.s7dcl plus .s7res for the texts),
// or the raw XML export.
internal sealed class UdtKind : ISourceKind
{
    public string Name => "udt";
    public string Section => "PLC data types";
    public DeviceFamily Family => DeviceFamily.Plc;
    public string? FixedName => null;
    public bool XmlNeedsCompile => true;
    public IReadOnlyList<string> Formats { get; } = new[] { SourceFormat.S7dcl, SourceFormat.Xml };

    public async Task<IReadOnlyList<ListedEntry>> List(TiaSession session, string plc) =>
        (await session.ListPlcTypesAsync(plc)).Select(t => new ListedEntry(t.GroupPath, t.Name, Describe(Name, t.GroupPath, t.Name), Formats)).ToList();

    public async Task Export(TiaSession session, string plc, Func<string, string, IReadOnlyCollection<string>?> formats, ExportOptions options, List<ExportedItem> items)
    {
        foreach (var (t, result) in await session.ReadAllUdtsAsync(plc, formats))
            items.Add(ExportedItem.FromDocuments(plc, Name, t.GroupPath, t.Name, result, Formats));
    }

    // Nested UDTs first, compiling between the layers - see DependencyWrites.
    public async Task<WriteLog> Write(WriteContext context, IReadOnlyList<ImportListItem> items)
    {
        var log = new WriteLog();
        var session = context.Session;
        var plc = context.Device;
        var existingTypes = (await session.ListPlcTypesAsync(plc)).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ensuredGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var writes = new List<PendingWrite>();

        foreach (var item in items)
        {
            var label = Describe(Name, item.GroupPath, item.ItemName) + FromXml(item);
            var dir = GroupDir(context.DeviceDir, Section, item.GroupPath);
            var sanitizedName = Sanitize(item.ItemName);
            var (groupPath, typeName) = (item.GroupPath, item.ItemName);
            var exists = existingTypes.Contains(item.ItemName);
            List<BlockDocument> docs;
            Func<Task<ImportResult>> write;
            if (item.Format == SourceFormat.Xml)
            {
                var xml = ReadXml(context, this, item, log);
                if (xml == null) continue;
                docs = new List<BlockDocument> { new(typeName + SourceFormat.Extension(SourceFormat.Xml), xml) };
                write = () => session.ImportUdtXmlAsync(plc, groupPath, typeName, xml);
            }
            else
            {
                var file = Path.Combine(dir, sanitizedName + ".s7dcl");
                if (!File.Exists(file)) file = Path.Combine(dir, sanitizedName + ".txt");
                if (!File.Exists(file)) { log.Failed.Add($"{label}: no source file found in {dir}"); continue; }

                var resFile = Path.Combine(dir, sanitizedName + ".s7res");
                var dclDocs = Documents(typeName, File.ReadAllText(file), File.Exists(resFile) ? File.ReadAllText(resFile) : null);
                docs = dclDocs;
                write = exists ? () => session.WriteUdtAsync(plc, typeName, dclDocs) : () => session.CreateUdtAsync(plc, groupPath, typeName, dclDocs);
            }

            if (exists)
            {
                if (!context.OverwriteExisting) { log.Skipped.Add($"{label} - already exists, overwriteExisting=false"); continue; }
                if (context.DryRun) { log.Updated.Add($"{label} (dry run)"); continue; }
                writes.Add(new PendingWrite(item, label, docs, false, write));
            }
            else
            {
                if (!context.CreateMissing) { log.Skipped.Add($"{label} - does not exist, createMissing=false"); continue; }
                if (context.DryRun) { log.Created.Add($"{label} (dry run)"); continue; }

                await EnsureGroupPath(item.GroupPath, path => session.TypeGroupExistsAsync(plc, path), (parent, segment) => session.CreateTypeGroupAsync(plc, parent, segment), ensuredGroups);
                writes.Add(new PendingWrite(item, label, docs, true, write));
            }
        }

        await DependencyWrites.Write(context, writes, log, compileBetweenLayers: true);
        return log;
    }

    private static List<BlockDocument> Documents(string typeName, string content, string? resContent)
    {
        var docs = new List<BlockDocument> { new($"{typeName}.s7dcl", content) };
        if (!string.IsNullOrEmpty(resContent)) docs.Add(new BlockDocument($"{typeName}.s7res", resContent!));
        return docs;
    }
}
