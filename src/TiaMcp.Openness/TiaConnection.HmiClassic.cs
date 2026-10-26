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
/// The kinds of classic WinCC (Comfort/Advanced) objects. The array order is the import order
/// (lists before the tags that use them, templates/popups before the screens).
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

    // A folder name may contain '/' itself: see FindByPath.
    private static ClassicFolder? FindClassicFolder(ClassicFolder root, string folderPath)
    {
        var path = NormalizeFolderPath(folderPath);
        return path.Length == 0 ? root : FindByPath<ClassicFolder>(path, ChildNamed(root), ChildNamed);
    }

    private static Func<string, ClassicFolder?> ChildNamed(ClassicFolder folder) =>
        name => folder.Children().FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    private static string[] SplitFolderPath(string? folderPath) =>
        (folderPath ?? "").Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

    private static string NormalizeFolderPath(string? folderPath) => string.Join("/", SplitFolderPath(folderPath));

    // ---- source tree support (read_source_tree / write_source_tree / list_project) ----

    public IReadOnlyList<string> ListHmiClassicNames()
    {
        EnsureConnected();
        return _hmiTargetByName.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Every object of the given kinds (graphic lists excluded, see ReadHmiClassicObjects) with its folder path.</summary>
    public IReadOnlyList<HmiClassicObjectSummary> ListHmiClassicObjects(string hmiName)
    {
        EnsureConnected();
        var target = GetHmiTarget(hmiName);
        var result = new List<HmiClassicObjectSummary>();
        foreach (var kind in HmiClassicKinds.All)
        {
            WalkClassic(GetClassicRoot(target, kind), "", (path, folder) =>
            {
                foreach (var item in folder.Items()) result.Add(new HmiClassicObjectSummary(target.Name, kind, path, item.Name));
            });
        }
        return result;
    }

    /// <summary>
    /// Exports the selected objects as Openness XML text. The DocumentInfo block (export time,
    /// installed products) is removed so an unchanged object always gives the same text.
    /// Graphic lists are never exported: in TIA Portal V21 that crashed the whole TIA Portal
    /// process (InvalidCastException String -> HmiOpenLink in UpdateOpenLinks).
    /// </summary>
    public IReadOnlyList<HmiClassicObjectXml> ReadHmiClassicObjects(string hmiName, Func<string, string, string, bool>? include)
    {
        EnsureConnected();
        var target = GetHmiTarget(hmiName);
        var result = new List<HmiClassicObjectXml>();
        var tempDir = Path.Combine(Path.GetTempPath(), "TiaMcp", "classic-read", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var index = 0;
            foreach (var kind in HmiClassicKinds.All.Where(k => k != HmiClassicKinds.GraphicList))
            {
                WalkClassic(GetClassicRoot(target, kind), "", (path, folder) =>
                {
                    foreach (var item in folder.Items())
                    {
                        if (include != null && !include(kind, path, item.Name)) continue;
                        var file = new FileInfo(Path.Combine(tempDir, $"{index++}.xml"));
                        try
                        {
                            item.Export(file);
                            var doc = XDocument.Load(file.FullName);
                            doc.Root?.Element("DocumentInfo")?.Remove();
                            result.Add(new HmiClassicObjectXml(kind, path, item.Name, doc.ToString(), null));
                        }
                        catch (Exception ex)
                        {
                            result.Add(new HmiClassicObjectXml(kind, path, item.Name, null, DescribeException(ex)));
                        }
                    }
                });
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return result;
    }

    private readonly Dictionary<string, Dictionary<int, string>> _classicScreenNumbers = new(StringComparer.OrdinalIgnoreCase);

    public void ClearHmiClassicScreenNumbers() => _classicScreenNumbers.Clear();

    public int CountHmiClassicGraphicLists(string hmiName)
    {
        EnsureConnected();
        return GetHmiTarget(hmiName).GraphicLists.Count;
    }

    /// <summary>
    /// Creates or overwrites one classic object from its Openness XML in folderPath (created if
    /// missing). Checked first, because TIA Portal terminates itself on these: the XML must be an
    /// object of the expected kind and name, and a screen's number must not be used by another
    /// screen. The numbers of the existing screens are read once per HMI and kept until
    /// ClearHmiClassicScreenNumbers (reading them means exporting every screen).
    /// </summary>
    public SimpleResult WriteHmiClassicObject(string hmiName, string kind, string folderPath, string name, string xml, bool create)
    {
        EnsureConnected();
        var target = GetHmiTarget(hmiName);
        var tempDir = Path.Combine(Path.GetTempPath(), "TiaMcp", "classic-write", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var file = new FileInfo(Path.Combine(tempDir, "object.xml"));
            File.WriteAllText(file.FullName, xml);
            var (xmlKind, xmlName, number) = DetectClassicXml(file);
            if (xmlKind != kind)
                return new SimpleResult(false, $"Nothing was written: the XML is not a {kind} object (found {xmlKind ?? "no classic WinCC object"}).");
            if (!string.Equals(xmlName, name, StringComparison.Ordinal))
                return new SimpleResult(false, $"Nothing was written: the XML's <Name> is '{xmlName}', but the file is named '{name}'. Rename both together.");

            Dictionary<int, string>? screenNumbers = null;
            if (number is int n)
            {
                if (!_classicScreenNumbers.TryGetValue(target.Name, out screenNumbers))
                {
                    screenNumbers = new Dictionary<int, string>();
                    CollectScreenNumbers(target, screenNumbers, new List<string>());
                    _classicScreenNumbers[target.Name] = screenNumbers;
                }
                if (screenNumbers.TryGetValue(n, out var owner) && owner != name)
                    return new SimpleResult(false, $"Nothing was written: screen number {n} is already used by screen '{owner}' (importing it would make TIA Portal terminate itself). Change <Number> to a free number.");
            }

            var root = GetClassicRoot(target, kind);
            var folder = HmiClassicKinds.HasFolders(kind) ? EnsureClassicFolder(root, folderPath) : root;
            folder.Import(file, create ? ImportOptions.None : ImportOptions.Override);
            if (number is int written && screenNumbers != null) screenNumbers[written] = name;
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, DescribeException(ex));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // Openness wraps the real reason ("Error when calling method 'Import'...") - add the inner messages.
    private static string DescribeException(Exception ex)
    {
        var parts = new List<string> { ex.Message.Trim() };
        for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
            if (!parts.Contains(inner.Message.Trim())) parts.Add(inner.Message.Trim());
        return string.Join(" - ", parts);
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

}
