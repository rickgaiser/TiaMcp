using TiaMcp.Openness;
using static TiaMcp.Mcp.SourceTree;

namespace TiaMcp.Mcp;

// Classic WinCC VB scripts, in their folders: the code as '.vbs' (see VbScriptXml), or the raw
// XML export. The .vbs holds only the code, so writing one patches it into a fresh export of the
// script; a new script (its name, Sub/Function type and parameters) needs its .xml.
internal sealed class HmiVbScriptKind : ISourceKind
{
    public string Name => "HMI VB script";
    public string Section => "VB scripts";
    public DeviceFamily Family => DeviceFamily.HmiClassic;
    public string? FixedName => null;
    public bool XmlNeedsCompile => false;
    public MarkTarget? MarkTarget => null;
    public IReadOnlyList<string> Formats { get; } = new[] { SourceFormat.Vbs, SourceFormat.Xml };

    public async Task<IReadOnlyList<ListedEntry>> List(TiaSession session, string hmi) =>
        (await session.ListHmiClassicObjectsAsync(hmi))
            .Where(o => o.Kind == HmiClassicKinds.VBScript)
            .OrderBy(o => Label(o.FolderPath, o.Name), StringComparer.OrdinalIgnoreCase)
            .Select(o => new ListedEntry(o.FolderPath, o.Name, Describe(Name, o.FolderPath, o.Name), Formats))
            .ToList();

    // One XML export per script gives both formats.
    public async Task Export(TiaSession session, string hmi, Func<string, string, IReadOnlyCollection<string>?> formats, ExportOptions options, List<ExportedItem> items, ExportingItem? exporting)
    {
        foreach (var o in await session.ReadHmiClassicObjectsAsync(hmi, (kind, folder, name) => kind == HmiClassicKinds.VBScript && formats(folder, name) != null, exporting))
        {
            var wanted = formats(o.FolderPath, o.Name) ?? Array.Empty<string>();
            var files = new List<(string, string, bool)>();
            string? error = o.Xml == null ? o.Error ?? "export failed" : null;
            if (o.Xml != null)
            {
                var stem = $"{ItemDir(hmi, Name, o.FolderPath)}/{Sanitize(o.Name)}";
                if (wanted.Count == 0 || wanted.Contains(SourceFormat.Vbs))
                {
                    try { files.Add((stem + SourceFormat.Extension(SourceFormat.Vbs), VbScriptXml.ReadCode(o.Xml), false)); }
                    catch (FormatException ex) { error = ex.Message; }
                }
                if (wanted.Contains(SourceFormat.Xml)) files.Add((stem + SourceFormat.Extension(SourceFormat.Xml), o.Xml, false));
            }
            items.Add(new ExportedItem(hmi, Name, o.FolderPath, o.Name, files, error, Formats));
        }
    }

    public async Task<WriteLog> Write(WriteContext context, IReadOnlyList<ImportListItem> items)
    {
        var log = new WriteLog();
        var session = context.Session;
        var existing = (await session.ListHmiClassicObjectsAsync(context.Device))
            .Where(o => o.Kind == HmiClassicKinds.VBScript)
            .ToDictionary(o => o.Name, o => o.FolderPath, StringComparer.Ordinal);

        foreach (var item in items)
        {
            var label = Describe(Name, item.GroupPath, item.ItemName) + FromXml(item);
            var exists = existing.ContainsKey(item.ItemName);
            if (exists && !context.OverwriteExisting) { log.Skipped.Add($"{label} - already exists, overwriteExisting=false"); continue; }
            if (!exists && !context.CreateMissing) { log.Skipped.Add($"{label} - does not exist, createMissing=false"); continue; }

            string xml;
            if (item.Format == SourceFormat.Xml)
            {
                var fromFile = ReadXml(context, this, item, log);
                if (fromFile == null) continue;
                xml = fromFile;
            }
            else
            {
                var file = Path.Combine(GroupDir(context.DeviceDir, Section, item.GroupPath), Sanitize(item.ItemName) + SourceFormat.Extension(SourceFormat.Vbs));
                if (!File.Exists(file)) { log.Failed.Add($"{label}: no .vbs file found at {file}"); continue; }
                if (!exists)
                {
                    log.Failed.Add($"{label}: nothing was written - a new VB script needs its .xml (name, Sub/Function type and parameters); copy the .xml of a similar script and rename it");
                    continue;
                }

                var current = (await session.ReadHmiClassicObjectsAsync(context.Device,
                    (kind, folder, name) => kind == HmiClassicKinds.VBScript && name == item.ItemName)).FirstOrDefault();
                if (current?.Xml == null) { log.Failed.Add($"{label}: nothing was written - exporting the current script failed: {current?.Error ?? "not found"}"); continue; }
                try { xml = VbScriptXml.WithCode(current.Xml, File.ReadAllText(file)); }
                catch (FormatException ex) { log.Failed.Add($"{label}: nothing was written - {ex.Message}"); continue; }
            }

            if (context.DryRun) { (exists ? log.Updated : log.Created).Add($"{label} (dry run)"); continue; }

            // An existing script is replaced in its own folder; a new one goes to the file's folder.
            var folder = exists ? existing[item.ItemName] : item.GroupPath;
            var result = await session.WriteHmiClassicObjectAsync(context.Device, HmiClassicKinds.VBScript, folder, item.ItemName, xml, create: !exists);
            if (!result.Success) { log.Failed.Add($"{label}: {result.Error}"); continue; }
            (exists ? log.Updated : log.Created).Add(label);
            existing[item.ItemName] = folder;
            context.Written.Add(item);
        }
        return log;
    }
}
