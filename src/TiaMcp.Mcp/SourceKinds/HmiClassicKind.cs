using TiaMcp.Openness;
using static TiaMcp.Mcp.SourceTree;

namespace TiaMcp.Mcp;

// Classic WinCC (Comfort/Advanced/RT Advanced) objects. They have no typed model in Openness, so
// they live in the tree as their native Openness XML, in their folders. Graphic lists are left
// out: exporting one crashes TIA Portal V21.
internal sealed class HmiClassicKind : ISourceKind
{
    // In write order: text lists, tag tables, templates and pop-ups, then screens.
    public static readonly HmiClassicKind TextList = new("HMI text list", "Text lists", HmiClassicKinds.TextList);
    public static readonly HmiClassicKind TagTable = new("HMI tag table", "HMI tags", HmiClassicKinds.TagTable);
    public static readonly HmiClassicKind ScreenTemplate = new("HMI screen template", "Screen templates", HmiClassicKinds.ScreenTemplate);
    public static readonly HmiClassicKind PopupScreen = new("HMI pop-up screen", "Pop-up screens", HmiClassicKinds.PopupScreen);
    public static readonly HmiClassicKind Screen = new("HMI screen", "Screens", HmiClassicKinds.Screen);

    // The kind as HmiClassicKinds (and so TiaConnection) names it.
    private readonly string _classicKind;

    private HmiClassicKind(string name, string section, string classicKind)
    {
        Name = name;
        Section = section;
        _classicKind = classicKind;
    }

    public string Name { get; }
    public string Section { get; }
    public DeviceFamily Family => DeviceFamily.HmiClassic;
    public string? FixedName => null;
    public bool XmlNeedsCompile => false;
    public IReadOnlyList<string> Formats { get; } = new[] { SourceFormat.Xml };

    public async Task<IReadOnlyList<ListedEntry>> List(TiaSession session, string hmi) =>
        (await session.ListHmiClassicObjectsAsync(hmi))
            .Where(o => o.Kind == _classicKind)
            .OrderBy(o => Label(o.FolderPath, o.Name), StringComparer.OrdinalIgnoreCase)
            .Select(o => new ListedEntry(o.FolderPath, o.Name, Describe(Name, o.FolderPath, o.Name), Formats))
            .ToList();

    public async Task Export(TiaSession session, string hmi, Func<string, string, IReadOnlyCollection<string>?> formats, ExportOptions options, List<ExportedItem> items)
    {
        foreach (var o in await session.ReadHmiClassicObjectsAsync(hmi, (kind, folder, name) => kind == _classicKind && formats(folder, name) != null))
        {
            var files = o.Xml == null
                ? new List<(string, string, bool)>()
                : new List<(string, string, bool)> { ($"{ItemDir(hmi, Name, o.FolderPath)}/{Sanitize(o.Name)}.xml", o.Xml, false) };
            items.Add(new ExportedItem(hmi, Name, o.FolderPath, o.Name, files, o.Xml == null ? o.Error ?? "export failed" : null));
        }
    }

    // A screen's number is checked against the other screens before the import (a duplicate makes
    // TIA Portal V21 terminate itself); write_source_tree resets the numbers it caches once per write.
    public async Task<WriteLog> Write(WriteContext context, IReadOnlyList<ImportListItem> items)
    {
        var log = new WriteLog();
        var existing = new HashSet<string>((await context.Session.ListHmiClassicObjectsAsync(context.Device))
            .Where(o => o.Kind == _classicKind)
            .Select(o => o.Name), StringComparer.Ordinal);

        foreach (var item in items)
        {
            var label = Describe(Name, item.GroupPath, item.ItemName);
            var file = Path.Combine(GroupDir(context.DeviceDir, Section, item.GroupPath), Sanitize(item.ItemName) + ".xml");
            if (!File.Exists(file)) { log.Failed.Add($"{label}: no .xml file found at {file}"); continue; }

            var exists = existing.Contains(item.ItemName);
            if (exists && !context.OverwriteExisting) { log.Skipped.Add($"{label} - already exists, overwriteExisting=false"); continue; }
            if (!exists && !context.CreateMissing) { log.Skipped.Add($"{label} - does not exist, createMissing=false"); continue; }
            if (context.DryRun) { (exists ? log.Updated : log.Created).Add($"{label} (dry run)"); continue; }

            var result = await context.Session.WriteHmiClassicObjectAsync(context.Device, _classicKind, item.GroupPath, item.ItemName, File.ReadAllText(file), create: !exists);
            if (!result.Success) { log.Failed.Add($"{label}: {result.Error}"); continue; }
            (exists ? log.Updated : log.Created).Add(label);
            existing.Add(item.ItemName);
            context.Written.Add(item);
        }
        return log;
    }
}
