using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Siemens.Engineering;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HmiUnified.UI.Base;
using Siemens.Engineering.HmiUnified.UI.Controls;
using Siemens.Engineering.HmiUnified.UI.Enum;
using Siemens.Engineering.HmiUnified.UI.ScreenGroup;
using Siemens.Engineering.HmiUnified.UI.Screens;
using Siemens.Engineering.Library.Types;

namespace TiaMcp.Openness;

public sealed partial class TiaConnection
{
    // ---- WinCC Unified screens ----
    // Same layout as HMI tag tables: a flat root composition plus a group tree, names unique per
    // HMI. Screen content goes through HmiScreenConverter (JSON), since Openness has no
    // export/import for Unified screens.

    private const string ScratchScreenPrefix = "__TiaMcp_Scratch_";
    private static readonly JsonSerializerOptions ScreenJsonOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // Property values of a freshly created screen/item, per HMI and type (+ ContainedType for
    // faceplates), so a read can leave out everything still at its default - per HMI, as defaults
    // can follow from the device (e.g. its screen size). Null = sampling failed.
    private readonly Dictionary<string, JsonObject?> _screenDefaults = new(StringComparer.Ordinal);

    private HmiScreenConverter NewScreenConverter() => new(_project!.LanguageSettings.EditingLanguage);

    private static string DefaultsKey(HmiSoftware software, Type type, string? containedType) => $"{software.Name}|{type.FullName}|{containedType}";

    public IReadOnlyList<HmiScreenSummary> ListHmiScreens(string hmiName)
    {
        EnsureConnected();
        var software = GetHmiSoftware(hmiName);
        var result = new List<HmiScreenSummary>();
        WalkHmiScreens(hmiName, software.Screens, software.ScreenGroups, "", result);
        return result;
    }

    private static void WalkHmiScreens(string hmiName, HmiScreenComposition screens, HmiScreenGroupComposition groups, string groupPath, List<HmiScreenSummary> result)
    {
        foreach (HmiScreen screen in screens)
        {
            if (screen.Name.StartsWith(ScratchScreenPrefix, StringComparison.Ordinal)) continue;
            result.Add(new HmiScreenSummary(hmiName, groupPath, screen.Name, screen.ScreenNumber, screen.Width, screen.Height, screen.ScreenItems.Count));
        }
        foreach (HmiScreenGroup group in groups)
        {
            WalkHmiScreens(hmiName, group.Screens, group.Groups, string.IsNullOrEmpty(groupPath) ? group.Name : $"{groupPath}/{group.Name}", result);
        }
    }

    // All screen group paths, including empty groups that ListHmiScreens can't reveal.
    public IReadOnlyList<string> ListHmiScreenGroups(string hmiName)
    {
        EnsureConnected();
        var software = GetHmiSoftware(hmiName);
        var result = new List<string>();
        WalkHmiScreenGroups(software.ScreenGroups, "", result);
        return result;
    }

    private static void WalkHmiScreenGroups(HmiScreenGroupComposition groups, string groupPath, List<string> result)
    {
        foreach (HmiScreenGroup group in groups)
        {
            var childPath = string.IsNullOrEmpty(groupPath) ? group.Name : $"{groupPath}/{group.Name}";
            result.Add(childPath);
            WalkHmiScreenGroups(group.Groups, childPath, result);
        }
    }

    // Accepts the bare name or the "<group path>/<name>" form list_project shows, like FindBlockByName.
    private static HmiScreen? FindHmiScreenByName(HmiScreenComposition screens, HmiScreenGroupComposition groups, string name)
    {
        var bareName = name.Substring(name.LastIndexOf('/') + 1);
        var direct = screens.Find(bareName);
        if (direct != null) return direct;

        foreach (HmiScreenGroup group in groups)
        {
            var found = FindHmiScreenByName(group.Screens, group.Groups, bareName);
            if (found != null) return found;
        }
        return null;
    }

    public bool HmiScreenGroupExists(string hmiName, string groupPath)
    {
        EnsureConnected();
        return ResolveHmiScreenGroup(GetHmiSoftware(hmiName), groupPath) != null;
    }

    private static (HmiScreenComposition Screens, HmiScreenGroupComposition Groups)? ResolveHmiScreenGroup(HmiSoftware software, string groupPath)
    {
        if (string.IsNullOrEmpty(groupPath))
        {
            return (software.Screens, software.ScreenGroups);
        }

        var group = FindHmiScreenGroupByPath(software.ScreenGroups, groupPath);
        if (group == null) return null;
        return (group.Screens, group.Groups);
    }

    private static HmiScreenGroup? FindHmiScreenGroupByPath(HmiScreenGroupComposition rootGroups, string path) =>
        FindByPath<HmiScreenGroup>(path, rootGroups.Find, g => g.Groups.Find);

    public SimpleResult CreateHmiScreen(string hmiName, string groupPath, string screenName)
    {
        EnsureConnected();
        var software = GetHmiSoftware(hmiName);

        if (FindHmiScreenByName(software.Screens, software.ScreenGroups, screenName) != null)
        {
            return new SimpleResult(false, $"A screen named '{screenName}' already exists in HMI '{hmiName}' (screen names are unique per-HMI, not per-group).");
        }

        var resolved = ResolveHmiScreenGroup(software, groupPath);
        if (resolved == null)
        {
            return new SimpleResult(false, $"Group '{groupPath}' not found in HMI '{hmiName}'. Create the group first, or pass an empty groupPath for the root.");
        }

        try
        {
            resolved.Value.Screens.Create(screenName);
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    public SimpleResult DeleteHmiScreen(string hmiName, string screenName)
    {
        EnsureConnected();
        var software = GetHmiSoftware(hmiName);
        var screen = FindHmiScreenByName(software.Screens, software.ScreenGroups, screenName);
        if (screen == null)
        {
            return new SimpleResult(false, $"Screen '{screenName}' not found in HMI '{hmiName}'.");
        }

        try
        {
            screen.Delete();
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    public SimpleResult RenameHmiScreen(string hmiName, string screenName, string newName)
    {
        EnsureConnected();
        var software = GetHmiSoftware(hmiName);
        var screen = FindHmiScreenByName(software.Screens, software.ScreenGroups, screenName);
        if (screen == null)
        {
            return new SimpleResult(false, $"Screen '{screenName}' not found in HMI '{hmiName}'.");
        }
        if (FindHmiScreenByName(software.Screens, software.ScreenGroups, newName) != null)
        {
            return new SimpleResult(false, $"A screen named '{newName}' already exists in HMI '{hmiName}' (screen names are unique per-HMI).");
        }

        try
        {
            screen.Name = newName;
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    public SimpleResult CreateHmiScreenGroup(string hmiName, string parentGroupPath, string groupName)
    {
        EnsureConnected();
        var software = GetHmiSoftware(hmiName);
        var resolved = ResolveHmiScreenGroup(software, parentGroupPath);
        if (resolved == null)
        {
            return new SimpleResult(false, $"Parent group '{parentGroupPath}' not found in HMI '{hmiName}'.");
        }

        try
        {
            resolved.Value.Groups.Create(groupName);
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    public SimpleResult DeleteHmiScreenGroup(string hmiName, string groupPath)
    {
        EnsureConnected();
        var software = GetHmiSoftware(hmiName);
        var group = FindHmiScreenGroupByPath(software.ScreenGroups, groupPath);
        if (group == null)
        {
            return new SimpleResult(false, $"Group '{groupPath}' not found in HMI '{hmiName}'.");
        }

        try
        {
            group.Delete();
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    public SimpleResult RenameHmiScreenGroup(string hmiName, string groupPath, string newName)
    {
        EnsureConnected();
        var software = GetHmiSoftware(hmiName);
        var group = FindHmiScreenGroupByPath(software.ScreenGroups, groupPath);
        if (group == null)
        {
            return new SimpleResult(false, $"Group '{groupPath}' not found in HMI '{hmiName}'.");
        }

        try
        {
            group.Name = newName;
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    public HmiScreenResult ReadHmiScreen(string hmiName, string screenName, bool includeDefaults = false)
    {
        EnsureConnected();
        var software = GetHmiSoftware(hmiName);
        var screen = FindHmiScreenByName(software.Screens, software.ScreenGroups, screenName);
        if (screen == null)
        {
            return new HmiScreenResult(false, null, $"Screen '{screenName}' not found in HMI '{hmiName}'.");
        }

        try
        {
            var converter = NewScreenConverter();
            Func<Type, string?, JsonObject?>? defaults = null;
            if (!includeDefaults)
            {
                var needed = new List<(Type, string?)> { (typeof(HmiScreen), null) };
                foreach (HmiScreenItemBase item in screen.ScreenItems)
                    needed.Add((item.GetType(), (item as HmiContainerBase)?.ContainedType));
                EnsureScreenDefaults(software, needed);
                defaults = (type, containedType) => _screenDefaults.TryGetValue(DefaultsKey(software, type, containedType), out var d) ? d : null;
            }

            var json = converter.SerializeScreen(screen, defaults);
            return new HmiScreenResult(true, json.ToJsonString(ScreenJsonOptions), null);
        }
        catch (Exception ex)
        {
            return new HmiScreenResult(false, null, ex.Message);
        }
    }

    // baseJsonText: the screen as last read, still matching the project - only what differs from
    // it is written (see HmiScreenConverter.ChangedSince). Without it every key is written.
    public WriteHmiScreenResult WriteHmiScreen(string hmiName, string screenName, string jsonText, bool deleteMissing = false, string? baseJsonText = null)
    {
        EnsureConnected();
        var software = GetHmiSoftware(hmiName);
        var screen = FindHmiScreenByName(software.Screens, software.ScreenGroups, screenName);
        if (screen == null)
        {
            return new WriteHmiScreenResult(false, 0, 0, 0, new[] { $"Screen '{screenName}' not found in HMI '{hmiName}'." });
        }

        JsonObject json;
        try
        {
            json = JsonNode.Parse(jsonText) as JsonObject ?? throw new FormatException("the top level must be a JSON object.");
        }
        catch (Exception ex)
        {
            return new WriteHmiScreenResult(false, 0, 0, 0, new[] { $"Invalid JSON: {ex.Message} Nothing was written." });
        }

        JsonObject? baseJson = null;
        try { if (baseJsonText != null) baseJson = JsonNode.Parse(baseJsonText) as JsonObject; }
        catch (JsonException) { /* no usable base: write everything */ }

        var stats = NewScreenConverter().WriteScreen(screen, json, deleteMissing, baseJson);
        var name = json[HmiScreenConverter.NameKey]?.ToString();
        if (name != null && !string.Equals(name, screen.Name, StringComparison.Ordinal))
            stats.Messages.Add($"\"name\" '{name}' ignored - use rename_hmi_screen to rename a screen.");
        var success = !stats.Messages.Any(m => m.StartsWith("FAILED", StringComparison.Ordinal));
        return new WriteHmiScreenResult(success, stats.Created, stats.Updated, stats.Deleted, stats.Messages);
    }

    public static string DescribeHmiItemType(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return "Screen item types (the \"type\" of an item): " + string.Join(", ", HmiScreenConverter.ItemTypes.Keys.OrderBy(k => k, StringComparer.Ordinal)) +
                   "\nPass one of these (or 'Screen' for the screen's own properties) to see its properties, events and dynamization options.";
        }
        if (string.Equals(typeName, "Screen", StringComparison.OrdinalIgnoreCase) || string.Equals(typeName, "HmiScreen", StringComparison.OrdinalIgnoreCase))
        {
            return HmiScreenConverter.Describe(typeof(HmiScreen));
        }
        var type = HmiScreenConverter.ResolveItemType(typeName!);
        return type == null
            ? $"Unknown item type '{typeName}'. Known types: {string.Join(", ", HmiScreenConverter.ItemTypes.Keys.OrderBy(k => k, StringComparer.Ordinal))}."
            : HmiScreenConverter.Describe(type);
    }

    // Samples defaults for every (type, ContainedType) not cached yet, in one go.
    private void EnsureScreenDefaults(HmiSoftware software, IEnumerable<(Type Type, string? ContainedType)> needed)
    {
        var missing = needed.Distinct().Where(n => !_screenDefaults.ContainsKey(DefaultsKey(software, n.Type, n.ContainedType))).ToList();
        if (missing.Count == 0) return;

        var converter = NewScreenConverter();
        try
        {
            WithScratchScreen(software, scratch =>
            {
                var index = 0;
                foreach (var (type, containedType) in missing)
                {
                    var key = DefaultsKey(software, type, containedType);
                    try
                    {
                        _screenDefaults[key] = type == typeof(HmiScreen)
                            ? converter.SerializeScreenRaw(scratch)
                            : converter.SerializeItemRaw(HmiScreenConverter.CreateItem(scratch.ScreenItems, type, $"Default_{index++}", containedType));
                    }
                    catch
                    {
                        _screenDefaults[key] = null;
                    }
                }
                return true;
            });
        }
        catch
        {
            // No defaults then: the read just shows every property.
        }
    }

    // Runs action against a throw-away screen. Wrapped in a transaction that is never committed,
    // so TIA Portal rolls everything back (and the screen is deleted explicitly as well, in case
    // exclusive access/transactions aren't available). Used to sample defaults and to inspect a
    // faceplate type's interface, which Openness only exposes on a placed instance.
    private T WithScratchScreen<T>(HmiSoftware software, Func<HmiScreen, T> action)
    {
        ExclusiveAccess? access = null;
        Transaction? transaction = null;
        try
        {
            access = _tiaPortal!.ExclusiveAccess("TiaMcp: inspecting WinCC Unified screen defaults");
            transaction = access.Transaction(_project!, "TiaMcp scratch screen");
        }
        catch
        {
            transaction?.Dispose();
            access?.Dispose();
            transaction = null;
            access = null;
        }

        HmiScreen? scratch = null;
        try
        {
            scratch = software.Screens.Create(ScratchScreenPrefix + Guid.NewGuid().ToString("N").Substring(0, 8));
            return action(scratch);
        }
        finally
        {
            try { scratch?.Delete(); } catch { }
            transaction?.Dispose();
            access?.Dispose();
        }
    }

    // ---- Project library ----

    public IReadOnlyList<LibraryTypeSummary> ListProjectLibraryTypes(string? nameFilter = null)
    {
        EnsureConnected();
        var result = new List<LibraryTypeSummary>();
        WalkLibraryTypes(_project!.ProjectLibrary.TypeFolder, "", nameFilter, result);
        return result;
    }

    private static void WalkLibraryTypes(LibraryTypeFolder folder, string path, string? nameFilter, List<LibraryTypeSummary> result)
    {
        foreach (LibraryType type in folder.Types)
        {
            if (!string.IsNullOrEmpty(nameFilter) && type.Name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var versions = type.Versions
                .Select(v => new LibraryTypeVersionInfo(v.VersionNumber.ToString(), v.IsDefault, v.State.ToString(), FaceplateContainedType(type.Name, v.VersionNumber)))
                .ToArray();
            result.Add(new LibraryTypeSummary(path, type.Name, type.GetType().Name, versions));
        }
        foreach (LibraryTypeUserFolder child in folder.Folders)
        {
            WalkLibraryTypes(child, string.IsNullOrEmpty(path) ? child.Name : $"{path}/{child.Name}", nameFilter, result);
        }
    }

    // The ContainedType string a faceplate container refers to its library type version by, as
    // read back from placed faceplates: "V<version>\<type name>", e.g. "V0.0.35\FP_Motor".
    private static string FaceplateContainedType(string typeName, Version version) => $"V{version}\\{typeName}";

    // Openness exposes a faceplate type's interface only on a placed container, so this places
    // one on a scratch screen (rolled back afterwards) and reads it from there.
    public FaceplateTypeResult ReadFaceplateType(string hmiName, string containedType)
    {
        EnsureConnected();
        var software = GetHmiSoftware(hmiName);
        try
        {
            var converter = NewScreenConverter();
            return WithScratchScreen(software, scratch =>
            {
                var container = (HmiFaceplateContainer)HmiScreenConverter.CreateItem(scratch.ScreenItems, typeof(HmiFaceplateContainer), "Inspect", containedType);
                _screenDefaults[DefaultsKey(software, typeof(HmiFaceplateContainer), container.ContainedType)] = converter.SerializeItemRaw(container);
                var iface = container.Interface
                    .Select(p => new FaceplateInterfaceInfo(p.PropertyName, p.Value?.GetType().Name, p.Value is string text ? text : HmiScreenConverter.LeafToJson(p.Value)?.ToJsonString()))
                    .ToArray();
                var events = Enum.GetNames(typeof(HmiFaceplateContainerEventType)).Where(n => n != "None").ToArray();
                return new FaceplateTypeResult(true, container.ContainedType, iface, events, null);
            });
        }
        catch (Exception ex)
        {
            return new FaceplateTypeResult(false, containedType, Array.Empty<FaceplateInterfaceInfo>(), Array.Empty<string>(),
                $"{ex.Message} (ContainedType must name a faceplate type version in the project library, as list_library_types shows it, e.g. 'V1.0.0\\MyFaceplate'.)");
        }
    }
}
