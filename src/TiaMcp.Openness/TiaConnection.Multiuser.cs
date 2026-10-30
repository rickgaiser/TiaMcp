using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HmiUnified.HmiTags;
using Siemens.Engineering.HmiUnified.UI.ScreenGroup;
using Siemens.Engineering.Multiuser;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;

namespace TiaMcp.Openness;

public sealed partial class TiaConnection
{
    // ---- Multiuser ----
    // Openness has no refresh: it can tell whether the server has newer changes, per session and
    // per object, and who has an object marked. A mark state costs an Openness call per object
    // (~35 ms, not cached), and a tag table isn't markable itself - its tags are.

    public bool IsMultiuser => _localSession != null;

    // The mark state of the named items of one kind, summed over the objects each one is made of
    // (a tag table's tags, all alarms of a type). Null when no Multiuser local session is open.
    // An item that isn't found, or has nothing markable, is left out.
    public IReadOnlyDictionary<string, MarkSummary>? GetMarkStates(string device, MarkTarget target, IReadOnlyCollection<string> names)
    {
        EnsureConnected();
        if (_localSession == null) return null;
        var service = _localSession.MarkingService;
        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, MarkSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, objects) in MarkObjects(device, target))
        {
            if (!wanted.Contains(name) || result.ContainsKey(name)) continue;
            var summary = Summarize(service, objects);
            if (summary.Objects > 0) result[name] = summary;
        }
        return result;
    }

    // Per item of the kind: its name and the objects that carry its mark state. Lazy, so an
    // item's objects (a table's tags) are only enumerated when that item is wanted.
    private IEnumerable<(string Name, IEnumerable<IEngineeringObject> Objects)> MarkObjects(string device, MarkTarget target)
    {
        switch (target)
        {
            case MarkTarget.Block:
                return AllBlocks(GetSoftware(device).BlockGroup).Select(b => (b.Name, One(b)));
            case MarkTarget.Udt:
                return AllTypes(GetSoftware(device).TypeGroup).Select(t => (t.Name, One(t)));
            case MarkTarget.TagTable:
                return AllTagTables(GetSoftware(device).TagTableGroup).Select(t => (t.Name, (IEnumerable<IEngineeringObject>)t.Tags));
            case MarkTarget.HmiTagTable:
            {
                var hmi = GetHmiSoftware(device);
                return AllHmiTagTables(hmi.TagTables, hmi.TagTableGroups).Select(t => (t.Name, (IEnumerable<IEngineeringObject>)t.Tags));
            }
            case MarkTarget.HmiScreen:
            {
                var hmi = GetHmiSoftware(device);
                return AllHmiScreens(hmi.Screens, hmi.ScreenGroups).Select(s => (s.Name, One(s)));
            }
            case MarkTarget.HmiAlarmClasses:
                return new[] { ("", (IEnumerable<IEngineeringObject>)GetHmiSoftware(device).AlarmClasses) };
            case MarkTarget.HmiDiscreteAlarms:
                return new[] { ("", (IEnumerable<IEngineeringObject>)GetHmiSoftware(device).DiscreteAlarms) };
            case MarkTarget.HmiAnalogAlarms:
                return new[] { ("", (IEnumerable<IEngineeringObject>)GetHmiSoftware(device).AnalogAlarms) };
            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, null);
        }
    }

    private static IEnumerable<IEngineeringObject> One(IEngineeringObject obj) => new[] { obj };

    private static MarkSummary Summarize(MarkingService service, IEnumerable<IEngineeringObject> objects)
    {
        int count = 0, byMe = 0, byOthers = 0, notUpToDate = 0;
        var byOthersNames = new List<string>();
        var notUpToDateNames = new List<string>();
        foreach (var obj in objects)
        {
            MarkStateInfo info;
            try { info = service.GetMarkStateInfo(obj); }
            catch { continue; }
            if (!info.IsMarkable) continue;
            count++;
            var state = info.MarkState;
            if ((state & MarkState.IsMarkedByMe) != 0) byMe++;
            if ((state & MarkState.IsMarkedByOthers) != 0) { byOthers++; byOthersNames.Add(NameOf(obj) ?? "?"); }
            if ((state & MarkState.IsUptoDate) == 0) { notUpToDate++; notUpToDateNames.Add(NameOf(obj) ?? "?"); }
        }
        return new MarkSummary(count, byMe, byOthers, notUpToDate, byOthersNames, notUpToDateNames);
    }

    private static IEnumerable<PlcBlock> AllBlocks(PlcBlockGroup group)
    {
        foreach (var b in group.Blocks) yield return b;
        foreach (var g in group.Groups)
            foreach (var b in AllBlocks(g)) yield return b;
    }

    private static IEnumerable<PlcType> AllTypes(PlcTypeGroup group)
    {
        foreach (var t in group.Types) yield return t;
        foreach (var g in group.Groups)
            foreach (var t in AllTypes(g)) yield return t;
    }

    private static IEnumerable<PlcTagTable> AllTagTables(PlcTagTableGroup group)
    {
        foreach (var t in group.TagTables) yield return t;
        foreach (var g in group.Groups)
            foreach (var t in AllTagTables(g)) yield return t;
    }

    private static IEnumerable<HmiTagTable> AllHmiTagTables(HmiTagTableComposition tables, HmiTagTableGroupComposition groups)
    {
        foreach (var t in tables) yield return t;
        foreach (HmiTagTableGroup g in groups)
            foreach (var t in AllHmiTagTables(g.TagTables, g.Groups)) yield return t;
    }

    private static IEnumerable<Siemens.Engineering.HmiUnified.UI.Screens.HmiScreen> AllHmiScreens(Siemens.Engineering.HmiUnified.UI.Screens.HmiScreenComposition screens, HmiScreenGroupComposition groups)
    {
        foreach (var s in screens) yield return s;
        foreach (HmiScreenGroup g in groups)
            foreach (var s in AllHmiScreens(g.Screens, g.Groups)) yield return s;
    }

    // Openness has no refresh; it can only tell whether the server has newer changes.
    private static MultiuserStatus GetMultiuserStatus(LocalSession session)
    {
        var errors = new List<string>();
        bool? upToDate = null;
        try { upToDate = session.IsUptoDate(); }
        catch (Exception ex) { errors.Add($"server state: {ex.Message}"); }

        int? markedCount = null, conflicted = null;
        List<string>? marked = null;
        try
        {
            var markings = session.MarkingService.GetMarkings();
            var all = markings.AllMarkings.ToList();
            markedCount = all.Count;
            // Many marked objects (e.g. a network or a block's parts) have no name.
            marked = all.Select(m => NameOf(m.MarkedObject)).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToList();
            conflicted = markings.ConflictedMarkings.Count;
        }
        catch (Exception ex) { errors.Add($"marked objects: {ex.Message}"); }
        return new MultiuserStatus(upToDate, markedCount, marked, conflicted, errors.Count == 0 ? null : string.Join("; ", errors));
    }

    private static string? NameOf(IEngineeringObject? obj)
    {
        try { return obj?.GetAttribute("Name") as string; }
        catch { return null; }
    }
}
