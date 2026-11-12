using TiaMcp.Openness;
using static TiaMcp.Mcp.SourceTree;

namespace TiaMcp.Mcp;

// WinCC Unified global script modules. Openness exports the module as JavaScript; imports are
// deliberately not wired into write_source_tree yet.
internal sealed class HmiGlobalScriptKind : ISourceKind
{
    public string Name => "HMI global script module";
    public string Section => "Scripts";
    public DeviceFamily Family => DeviceFamily.HmiUnified;
    public string? FixedName => null;
    public bool XmlNeedsCompile => false;
    public MarkTarget? MarkTarget => null;
    public IReadOnlyList<string> Formats { get; } = new[] { SourceFormat.Js };

    public async Task<IReadOnlyList<ListedEntry>> List(TiaSession session, string hmi) =>
        (await session.ListHmiScriptModulesAsync(hmi))
            .OrderBy(module => module.Name, StringComparer.OrdinalIgnoreCase)
            .Select(module => new ListedEntry("", module.Name, Describe(Name, "", module.Name), Formats))
            .ToList();

    public async Task Export(TiaSession session, string hmi, Func<string, string, IReadOnlyCollection<string>?> formats, ExportOptions options, List<ExportedItem> items, ExportingItem? exporting)
    {
        foreach (var (module, result) in await session.ReadHmiScriptModulesAsync(hmi, name => formats("", name) != null, exporting))
            items.Add(ExportedItem.FromDocuments(hmi, Name, "", module.Name, result, Formats));
    }

    public Task<WriteLog> Write(WriteContext context, IReadOnlyList<ImportListItem> items)
    {
        var log = new WriteLog();
        foreach (var item in items)
            log.Failed.Add($"{Describe(Name, item.GroupPath, item.ItemName)}: global script modules are read-only");
        return Task.FromResult(log);
    }
}
