using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Hmi;
using ClassicScreenFolder = Siemens.Engineering.Hmi.Screen.ScreenFolder;
using ClassicScreenTemplateFolder = Siemens.Engineering.Hmi.Screen.ScreenTemplateFolder;
using ClassicScreenPopupFolder = Siemens.Engineering.Hmi.Screen.ScreenPopupFolder;
using ClassicTagFolder = Siemens.Engineering.Hmi.Tag.TagFolder;
using ClassicTextListComposition = Siemens.Engineering.Hmi.TextGraphicList.TextListComposition;
using ClassicGraphicListComposition = Siemens.Engineering.Hmi.TextGraphicList.GraphicListComposition;

namespace TiaMcp.Openness;

/// <summary>
/// The kinds of classic WinCC (Comfort/Advanced) objects the hmi_classic tools handle. Each kind
/// is also the name of its subdirectory in an export_hmi_classic tree, and the array order is the
/// import order (lists before the tags that use them, templates/popups before the screens).
/// </summary>
public static class HmiClassicKinds
{
    public const string TextList = "TextLists";
    public const string GraphicList = "GraphicLists";
    public const string TagTable = "TagTables";
    public const string ScreenTemplate = "ScreenTemplates";
    public const string PopupScreen = "PopupScreens";
    public const string Screen = "Screens";

    public static readonly string[] All = { TextList, GraphicList, TagTable, ScreenTemplate, PopupScreen, Screen };

    // Openness XML root object element -> kind, for import_hmi_classic's file detection.
    internal static readonly Dictionary<string, string> ByXmlElement = new(StringComparer.Ordinal)
    {
        ["Hmi.TextGraphicList.TextList"] = TextList,
        ["Hmi.TextGraphicList.GraphicList"] = GraphicList,
        ["Hmi.Tag.TagTable"] = TagTable,
        ["Hmi.Screen.ScreenTemplate"] = ScreenTemplate,
        ["Hmi.Screen.ScreenPopup"] = PopupScreen,
        ["Hmi.Screen.Screen"] = Screen,
    };

    // Accepts the canonical kind name or a loose alias ("screen", "templates", "popup", "tags", ...).
    public static string? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text!.Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "");
        if (t.EndsWith("s")) t = t.Substring(0, t.Length - 1);
        return t switch
        {
            "textlist" => TextList,
            "graphiclist" => GraphicList,
            "tagtable" or "tag" or "hmitagtable" => TagTable,
            "screentemplate" or "template" => ScreenTemplate,
            "popupscreen" or "popup" or "screenpopup" => PopupScreen,
            "screen" => Screen,
            _ => null,
        };
    }

    public static bool HasFolders(string kind) => kind != TextList && kind != GraphicList;
}

/// <summary>
/// Classic WinCC (HmiTarget) support: screens, screen templates, popup screens, tag tables,
/// text lists and graphic lists, as Openness XML export/import. Classic Openness has no typed
/// object model for these (only GetAttribute/SetAttribute), so everything here goes through
/// whole-object XML. HMI discrete/analog alarms are not exposed by classic Openness at all.
/// </summary>
public sealed partial class TiaConnection
{
    private readonly Dictionary<string, HmiTarget> _hmiTargetByName = new(StringComparer.OrdinalIgnoreCase);

    private void IndexHmiClassic(SoftwareContainer? container)
    {
        if (container?.Software is HmiTarget hmiTarget)
        {
            _hmiTargetByName[hmiTarget.Name] = hmiTarget;
        }
    }

    private HmiTarget GetHmiTarget(string hmiName)
    {
        if (!_hmiTargetByName.TryGetValue(hmiName, out var target))
        {
            var available = string.Join(", ", _hmiTargetByName.Keys);
            throw new InvalidOperationException(
                $"Unknown classic HMI '{hmiName}'. Known classic HMIs: [{available}]. " +
                "If an HMI device was renamed in the TIA Portal GUI since the last tia_connect, call tia_connect again - the HMI name index is only built at connect time.");
        }
        return target;
    }

    public IReadOnlyList<HmiClassicDeviceSummary> ListHmiClassicDevices()
    {
        EnsureConnected();
        var result = new List<HmiClassicDeviceSummary>();
        foreach (Siemens.Engineering.HW.Device device in _project!.Devices)
        {
            CollectHmiClassicDeviceSummaries(device.Name, device.DeviceItems, result);
        }
        return result;
    }

    private static void CollectHmiClassicDeviceSummaries(string deviceName, Siemens.Engineering.HW.DeviceItemComposition items, List<HmiClassicDeviceSummary> result)
    {
        foreach (Siemens.Engineering.HW.DeviceItem item in items)
        {
            if (item.GetService<SoftwareContainer>()?.Software is HmiTarget target)
            {
                result.Add(new HmiClassicDeviceSummary(deviceName, item.Name, target.Name));
            }
            CollectHmiClassicDeviceSummaries(deviceName, item.DeviceItems, result);
        }
    }

    // ---- Uniform folder model over the six differently-typed classic compositions ----

    private sealed class ClassicItem
    {
        public ClassicItem(string name, Action<FileInfo> export)
        {
            Name = name;
            Export = export;
        }

        public string Name { get; }
        public Action<FileInfo> Export { get; }
    }

    private sealed class ClassicFolder
    {
        public ClassicFolder(string name, Func<IEnumerable<ClassicFolder>> children, Func<string, ClassicFolder>? createChild,
            Func<IEnumerable<ClassicItem>> items, Action<FileInfo, ImportOptions> import)
        {
            Name = name;
            Children = children;
            CreateChild = createChild;
            Items = items;
            Import = import;
        }

        public string Name { get; }
        public Func<IEnumerable<ClassicFolder>> Children { get; }
        public Func<string, ClassicFolder>? CreateChild { get; }
        public Func<IEnumerable<ClassicItem>> Items { get; }
        public Action<FileInfo, ImportOptions> Import { get; }
    }

    // Children/Items are materialized: exporting an object while a lazy Openness enumerator over its
    // composition is still open throws EngineeringObjectDisposedException.
    private static ClassicFolder Wrap(ClassicScreenFolder f) => new(
        f.Name,
        () => f.Folders.Select(c => Wrap(c)).ToList(),
        n => Wrap(f.Folders.Create(n)),
        () => f.Screens.Select(s => new ClassicItem(s.Name, fi => s.Export(fi, ExportOptions.WithDefaults))).ToList(),
        (fi, o) => f.Screens.Import(fi, o));

    private static ClassicFolder Wrap(ClassicScreenTemplateFolder f) => new(
        f.Name,
        () => f.Folders.Select(c => Wrap(c)).ToList(),
        n => Wrap(f.Folders.Create(n)),
        () => f.ScreenTemplates.Select(s => new ClassicItem(s.Name, fi => s.Export(fi, ExportOptions.WithDefaults))).ToList(),
        (fi, o) => f.ScreenTemplates.Import(fi, o));

    private static ClassicFolder Wrap(ClassicScreenPopupFolder f) => new(
        f.Name,
        () => f.Folders.Select(c => Wrap(c)).ToList(),
        n => Wrap(f.Folders.Create(n)),
        () => f.ScreenPopups.Select(s => new ClassicItem(s.Name, fi => s.Export(fi, ExportOptions.WithDefaults))).ToList(),
        (fi, o) => f.ScreenPopups.Import(fi, o));

    private static ClassicFolder Wrap(ClassicTagFolder f) => new(
        f.Name,
        () => f.Folders.Select(c => Wrap(c)).ToList(),
        n => Wrap(f.Folders.Create(n)),
        () => f.TagTables.Select(t => new ClassicItem(t.Name, fi => t.Export(fi, ExportOptions.WithDefaults))).ToList(),
        (fi, o) => f.TagTables.Import(fi, o));

    private static ClassicFolder Wrap(ClassicTextListComposition lists) => new(
        HmiClassicKinds.TextList,
        () => Enumerable.Empty<ClassicFolder>(),
        null,
        () => lists.Select(l => new ClassicItem(l.Name, fi => l.Export(fi, ExportOptions.WithDefaults))).ToList(),
        (fi, o) => lists.Import(fi, o));

    private static ClassicFolder Wrap(ClassicGraphicListComposition lists) => new(
        HmiClassicKinds.GraphicList,
        () => Enumerable.Empty<ClassicFolder>(),
        null,
        () => lists.Select(l => new ClassicItem(l.Name, fi => l.Export(fi, ExportOptions.WithDefaults))).ToList(),
        (fi, o) => lists.Import(fi, o));

    private static ClassicFolder GetClassicRoot(HmiTarget target, string kind) => kind switch
    {
        HmiClassicKinds.Screen => Wrap(target.ScreenFolder),
        HmiClassicKinds.ScreenTemplate => Wrap(target.ScreenTemplateFolder),
        HmiClassicKinds.PopupScreen => Wrap(target.ScreenPopupFolder),
        HmiClassicKinds.TagTable => Wrap(target.TagFolder),
        HmiClassicKinds.TextList => Wrap(target.TextLists),
        HmiClassicKinds.GraphicList => Wrap(target.GraphicLists),
        _ => throw new ArgumentException($"Unknown classic HMI object kind '{kind}'. Valid: {string.Join(", ", HmiClassicKinds.All)}."),
    };

    private static void WalkClassic(ClassicFolder folder, string path, Action<string, ClassicFolder> visitFolder)
    {
        visitFolder(path, folder);
        foreach (var child in folder.Children())
        {
            WalkClassic(child, string.IsNullOrEmpty(path) ? child.Name : $"{path}/{child.Name}", visitFolder);
        }
    }

    private static ClassicFolder? FindClassicFolder(ClassicFolder root, string folderPath)
    {
        var current = root;
        foreach (var segment in SplitFolderPath(folderPath))
        {
            current = current.Children().FirstOrDefault(c => string.Equals(c.Name, segment, StringComparison.Ordinal));
            if (current == null) return null;
        }
        return current;
    }

    private static string[] SplitFolderPath(string? folderPath) =>
        (folderPath ?? "").Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

    private static string NormalizeFolderPath(string? folderPath) => string.Join("/", SplitFolderPath(folderPath));

    private static IReadOnlyList<string> ResolveKinds(IReadOnlyList<string>? kinds)
    {
        if (kinds == null || kinds.Count == 0) return HmiClassicKinds.All;
        var result = new List<string>();
        foreach (var k in kinds)
        {
            var parsed = HmiClassicKinds.Parse(k)
                ?? throw new ArgumentException($"Unknown classic HMI object kind '{k}'. Valid: {string.Join(", ", HmiClassicKinds.All)}.");
            if (!result.Contains(parsed)) result.Add(parsed);
        }
        // Keep the canonical (import) order regardless of how the caller listed them.
        return HmiClassicKinds.All.Where(result.Contains).ToList();
    }

    // ---- list ----

    public IReadOnlyList<HmiClassicObjectSummary> ListHmiClassicObjects(string hmiName, IReadOnlyList<string>? kinds = null)
    {
        EnsureConnected();
        var target = GetHmiTarget(hmiName);
        var result = new List<HmiClassicObjectSummary>();
        foreach (var kind in ResolveKinds(kinds))
        {
            WalkClassic(GetClassicRoot(target, kind), "", (path, folder) =>
            {
                foreach (var item in folder.Items())
                {
                    result.Add(new HmiClassicObjectSummary(target.Name, kind, path, item.Name));
                }
            });
        }
        return result;
    }

    // All folder paths (including empty ones that ListHmiClassicObjects can't reveal), root excluded.
    public IReadOnlyList<HmiClassicFolderSummary> ListHmiClassicFolders(string hmiName, IReadOnlyList<string>? kinds = null)
    {
        EnsureConnected();
        var target = GetHmiTarget(hmiName);
        var result = new List<HmiClassicFolderSummary>();
        foreach (var kind in ResolveKinds(kinds).Where(HmiClassicKinds.HasFolders))
        {
            WalkClassic(GetClassicRoot(target, kind), "", (path, _) =>
            {
                if (path.Length > 0) result.Add(new HmiClassicFolderSummary(target.Name, kind, path));
            });
        }
        return result;
    }

    // ---- create folder ----

    public SimpleResult CreateHmiClassicFolder(string hmiName, string kind, string parentFolderPath, string folderName)
    {
        EnsureConnected();
        var target = GetHmiTarget(hmiName);
        var parsedKind = HmiClassicKinds.Parse(kind);
        if (parsedKind == null || !HmiClassicKinds.HasFolders(parsedKind))
        {
            return new SimpleResult(false, $"Kind '{kind}' has no folders. Valid: {HmiClassicKinds.Screen}, {HmiClassicKinds.ScreenTemplate}, {HmiClassicKinds.PopupScreen}, {HmiClassicKinds.TagTable}.");
        }
        if (string.IsNullOrWhiteSpace(folderName) || folderName.IndexOfAny(new[] { '/', '\\' }) >= 0)
        {
            return new SimpleResult(false, "folderName must be a single non-empty folder name (no '/').");
        }

        var parent = FindClassicFolder(GetClassicRoot(target, parsedKind), parentFolderPath);
        if (parent == null)
        {
            return new SimpleResult(false, $"Parent folder '{parentFolderPath}' not found under {parsedKind} in HMI '{hmiName}'.");
        }
        if (parent.Children().Any(c => string.Equals(c.Name, folderName, StringComparison.Ordinal)))
        {
            return new SimpleResult(false, $"Folder '{folderName}' already exists under '{NormalizeFolderPath(parentFolderPath)}'.");
        }

        try
        {
            parent.CreateChild!(folderName);
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    // ---- export ----

    // Writes <targetDirectory>/<kind>/<folder path>/<name>.xml for each selected object.
    // names (exact, case-sensitive) and folderPath (prefix match incl. subfolders) are optional filters.
    public HmiClassicExportResult ExportHmiClassic(string hmiName, string targetDirectory, IReadOnlyList<string>? kinds,
        IReadOnlyList<string>? names, string? folderPath, bool overwriteFiles)
    {
        EnsureConnected();
        var target = GetHmiTarget(hmiName);
        var nameFilter = names != null && names.Count > 0 ? new HashSet<string>(names, StringComparer.Ordinal) : null;
        var folderFilter = NormalizeFolderPath(folderPath);
        var root = Path.GetFullPath(targetDirectory);
        var messages = new List<string>();
        var matchedNames = new HashSet<string>(StringComparer.Ordinal);
        int exported = 0, failed = 0, skipped = 0;

        // GraphicLists only on explicit request: in V21 exporting a graphic list crashed the whole
        // TIA Portal process (InvalidCastException String -> HmiOpenLink in UpdateOpenLinks).
        var exportKinds = kinds == null || kinds.Count == 0
            ? HmiClassicKinds.All.Where(k => k != HmiClassicKinds.GraphicList).ToList()
            : ResolveKinds(kinds);
        foreach (var kind in exportKinds)
        {
            WalkClassic(GetClassicRoot(target, kind), "", (path, folder) =>
            {
                if (folderFilter.Length > 0 && path != folderFilter && !path.StartsWith(folderFilter + "/", StringComparison.Ordinal))
                {
                    return;
                }

                foreach (var item in folder.Items())
                {
                    if (nameFilter != null && !nameFilter.Contains(item.Name)) continue;
                    matchedNames.Add(item.Name);

                    var label = $"[{kind}] {(path.Length == 0 ? "" : path + "/")}{item.Name}";
                    var dir = Path.Combine(new[] { root, kind }.Concat(SplitFolderPath(path).Select(SanitizeFileName)).ToArray());
                    var file = new FileInfo(Path.Combine(dir, SanitizeFileName(item.Name) + ".xml"));
                    try
                    {
                        if (file.Exists)
                        {
                            if (!overwriteFiles)
                            {
                                skipped++;
                                messages.Add($"SKIPPED {label} - file exists: {file.FullName}");
                                continue;
                            }
                            file.Delete();
                        }
                        Directory.CreateDirectory(dir);
                        item.Export(file);
                        exported++;
                        messages.Add($"OK      {label} -> {file.FullName}");
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        messages.Add($"FAILED  {label}: {ex.Message}");
                    }
                }
            });
        }

        if (nameFilter != null)
        {
            foreach (var n in nameFilter.Where(n => !matchedNames.Contains(n)))
            {
                messages.Add($"NOTFOUND {n}");
            }
        }

        return new HmiClassicExportResult(exported, failed, skipped, messages);
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    // ---- import ----

    private sealed class ClassicImportFile
    {
        public ClassicImportFile(FileInfo file, string kind, string name, int? number, string relativeFolder)
        {
            File = file;
            Kind = kind;
            Name = name;
            Number = number;
            RelativeFolder = relativeFolder;
        }

        public FileInfo File { get; }
        public string Kind { get; }
        public string Name { get; }
        public int? Number { get; }
        public string RelativeFolder { get; }
    }

    // sourcePath: a single .xml file or a directory (searched recursively for *.xml, e.g. an
    // export_hmi_classic tree). Kind and name come from the XML itself; the subfolder below a
    // '<kind>' directory segment is used as relative folder when preserveFolders is set.
    // Without overwriteExisting, objects whose name already exists (anywhere in that kind's
    // tree - names are unique per HMI) are skipped; with it they are re-imported with
    // ImportOptions.Override into the folder where the existing object lives.
    public HmiClassicImportResult ImportHmiClassic(string hmiName, string sourcePath, string? targetFolderPath, bool preserveFolders,
        bool createMissingFolders, bool overwriteExisting, bool dryRun)
    {
        EnsureConnected();
        var target = GetHmiTarget(hmiName);
        var messages = new List<string>();
        int imported = 0, failed = 0, skipped = 0;

        List<FileInfo> files;
        string? sourceRoot = null;
        if (File.Exists(sourcePath))
        {
            files = new List<FileInfo> { new FileInfo(sourcePath) };
        }
        else if (Directory.Exists(sourcePath))
        {
            sourceRoot = Path.GetFullPath(sourcePath);
            files = new DirectoryInfo(sourceRoot).GetFiles("*.xml", SearchOption.AllDirectories)
                .OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase).ToList();
        }
        else
        {
            return new HmiClassicImportResult(0, 1, 0, new[] { $"FAILED  sourcePath not found: {sourcePath}" });
        }

        var parsed = new List<ClassicImportFile>();
        foreach (var file in files)
        {
            try
            {
                var (kind, name, number) = DetectClassicXml(file);
                if (kind == null)
                {
                    skipped++;
                    messages.Add($"SKIPPED {file.FullName} - not a classic WinCC screen/template/popup/tag table/text list/graphic list export");
                    continue;
                }
                parsed.Add(new ClassicImportFile(file, kind, name ?? Path.GetFileNameWithoutExtension(file.Name), number,
                    preserveFolders ? RelativeFolderBelowKind(file, sourceRoot, kind) : ""));
            }
            catch (Exception ex)
            {
                failed++;
                messages.Add($"FAILED  {file.FullName}: cannot read XML - {ex.Message}");
            }
        }

        var baseFolder = NormalizeFolderPath(targetFolderPath);
        foreach (var kind in HmiClassicKinds.All)
        {
            var batch = parsed.Where(p => p.Kind == kind).ToList();
            if (batch.Count == 0) continue;

            var root = GetClassicRoot(target, kind);
            var existing = new Dictionary<string, string>(StringComparer.Ordinal);
            WalkClassic(root, "", (path, folder) =>
            {
                foreach (var item in folder.Items()) existing[item.Name] = path;
            });

            // Screen number -> screen name, read lazily (only if a screen is actually about to be
            // created/overwritten). A duplicate screen number on import is not a recoverable Openness
            // error: TIA Portal V21 throws HmiNonRecoverableException and terminates itself, so it
            // must be caught here before Import is ever called. Classic Openness only
            // exposes Name via GetAttribute, so the numbers come from a temporary XML export.
            Dictionary<int, string>? screenNumbers = null;

            foreach (var entry in batch)
            {
                var exists = existing.TryGetValue(entry.Name, out var existingFolder);
                var folderPath = !HmiClassicKinds.HasFolders(kind) ? ""
                    : exists ? existingFolder!
                    : NormalizeFolderPath(string.IsNullOrEmpty(entry.RelativeFolder) ? baseFolder
                        : string.IsNullOrEmpty(baseFolder) ? entry.RelativeFolder : $"{baseFolder}/{entry.RelativeFolder}");
                var label = $"[{kind}] {(folderPath.Length == 0 ? "" : folderPath + "/")}{entry.Name}";

                if (exists && !overwriteExisting)
                {
                    skipped++;
                    messages.Add($"SKIPPED {label} - already exists (pass overwriteExisting=true to replace it)");
                    continue;
                }

                if (entry.Number is int && screenNumbers == null)
                {
                    screenNumbers = new Dictionary<int, string>();
                    var numberErrors = new List<string>();
                    CollectScreenNumbers(target, screenNumbers, numberErrors);
                    if (numberErrors.Count > 0)
                    {
                        messages.Add($"WARNING could not read the screen number of {numberErrors.Count} existing screen(s), first: {numberErrors[0]}");
                    }
                }
                if (entry.Number is int number && screenNumbers!.TryGetValue(number, out var numberOwner) && numberOwner != entry.Name)
                {
                    failed++;
                    messages.Add($"FAILED  {label} - screen number {number} is already used by screen '{numberOwner}'. Change <Number> in the XML to a free number (importing it would make TIA Portal terminate itself)");
                    continue;
                }
                if (entry.Number is int reserved) screenNumbers![reserved] = entry.Name;

                var folder = FindClassicFolder(root, folderPath);
                var missingFolder = folder == null;
                if (missingFolder && !createMissingFolders)
                {
                    failed++;
                    messages.Add($"FAILED  {label} - target folder '{folderPath}' does not exist (create it with create_hmi_classic_folder or pass createMissingFolders=true)");
                    continue;
                }

                if (dryRun)
                {
                    imported++;
                    messages.Add($"{(exists ? "WOULD OVERWRITE" : "WOULD CREATE")} {label}{(missingFolder ? $" (would create folder '{folderPath}')" : "")} <- {entry.File.FullName}");
                    continue;
                }

                try
                {
                    folder ??= EnsureClassicFolder(root, folderPath);
                    folder.Import(entry.File, exists ? ImportOptions.Override : ImportOptions.None);
                    imported++;
                    existing[entry.Name] = folderPath;
                    messages.Add($"{(exists ? "OVERWRITTEN" : "CREATED")} {label}");
                }
                catch (Exception ex)
                {
                    failed++;
                    messages.Add($"FAILED  {label}: {ex.Message}");
                }
            }
        }

        return new HmiClassicImportResult(imported, failed, skipped, messages);
    }

    private static ClassicFolder EnsureClassicFolder(ClassicFolder root, string folderPath)
    {
        var current = root;
        foreach (var segment in SplitFolderPath(folderPath))
        {
            current = current.Children().FirstOrDefault(c => string.Equals(c.Name, segment, StringComparison.Ordinal))
                ?? current.CreateChild!(segment);
        }
        return current;
    }

    private static (string? Kind, string? Name, int? Number) DetectClassicXml(FileInfo file)
    {
        var doc = XDocument.Load(file.FullName);
        foreach (var element in doc.Root?.Elements() ?? Enumerable.Empty<XElement>())
        {
            if (HmiClassicKinds.ByXmlElement.TryGetValue(element.Name.LocalName, out var kind))
            {
                var attributes = element.Element("AttributeList");
                var name = attributes?.Element("Name")?.Value;
                int? number = int.TryParse(attributes?.Element("Number")?.Value, out var n) ? n : null;
                return (kind, string.IsNullOrWhiteSpace(name) ? null : name, number);
            }
        }
        return (null, null, null);
    }

    private static void CollectScreenNumbers(HmiTarget target, Dictionary<int, string> result, List<string> errors)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "TiaMcpScreenNumbers", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var index = 0;
            WalkClassic(GetClassicRoot(target, HmiClassicKinds.Screen), "", (_, folder) =>
            {
                foreach (var item in folder.Items())
                {
                    var file = new FileInfo(Path.Combine(tempDir, $"{index++}.xml"));
                    try
                    {
                        item.Export(file);
                        var (_, _, number) = DetectClassicXml(file);
                        if (number is int n) result[n] = item.Name;
                        else errors.Add($"'{item.Name}': no <Number> in export");
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"'{item.Name}': {ex.GetType().Name}: {ex.Message}");
                    }
                }
            });
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // For .../<kind>/<a>/<b>/X.xml returns "a/b"; "" when no '<kind>' directory segment exists at or
    // below sourceRoot (sourceRoot itself may be the '<kind>' directory) or the file sits directly in it.
    private static string RelativeFolderBelowKind(FileInfo file, string? sourceRoot, string kind)
    {
        if (sourceRoot == null) return "";
        var rootSegments = SplitFolderPath(sourceRoot).Length;
        var segments = SplitFolderPath(file.DirectoryName);
        var index = Array.FindLastIndex(segments, s => string.Equals(s, kind, StringComparison.OrdinalIgnoreCase));
        return index < rootSegments - 1 ? "" : string.Join("/", segments.Skip(index + 1));
    }
}
