using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Siemens.Engineering;
using Siemens.Engineering.HmiUnified.UI;
using Siemens.Engineering.HmiUnified.UI.Base;
using Siemens.Engineering.HmiUnified.UI.Controls;
using Siemens.Engineering.HmiUnified.UI.Dynamization;
using Siemens.Engineering.HmiUnified.UI.Dynamization.Flashing;
using Siemens.Engineering.HmiUnified.UI.Dynamization.Script;
using Siemens.Engineering.HmiUnified.UI.Dynamization.Tag;
using Siemens.Engineering.HmiUnified.UI.Events;
using Siemens.Engineering.HmiUnified.UI.Screens;

namespace TiaMcp.Openness;

/// <summary>
/// WinCC Unified screen &lt;-&gt; JSON. Openness (V21) has no export/import for Unified screens,
/// so this walks the typed object model by reflection: every public settable property of a
/// screen item becomes a JSON key under its exact Openness name, parts (Font, Padding, ...)
/// become nested objects, part compositions (Points, PressedStateTags, ...) become arrays.
/// That keeps every item type Openness knows (shapes, widgets, controls, faceplates) supported
/// without per-type code. Dynamizations, event scripts and faceplate interfaces get their own
/// reserved keys (lower case, so they can't collide with Openness property names).
/// </summary>
internal sealed class HmiScreenConverter
{
    public const string DynamizationsKey = "dynamizations";
    public const string EventsKey = "events";
    public const string PropertyEventsKey = "propertyEvents";
    public const string ItemsKey = ScreenJson.ItemsKey;
    public const string NameKey = ScreenJson.NameKey;
    public const string TypeKey = ScreenJson.TypeKey;

    // Always written on read even when equal to the defaults, so an item's identity and
    // position are readable at a glance.
    private static readonly HashSet<string> AlwaysKeep = new(StringComparer.Ordinal)
        { NameKey, TypeKey, "ContainedType", "Left", "Top", "Width", "Height" };

    private static readonly HashSet<string> SkippedProperties = new(StringComparer.Ordinal)
        { "Parent", "Dynamizations", "PropertyEventHandlers", "EventHandlers", "ScreenItems" };

    private static readonly Dictionary<string, Type> DynamizationTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Tag"] = typeof(TagDynamization),
        ["Script"] = typeof(ScriptDynamization),
        ["ResourceList"] = typeof(ResourceListDynamization),
        ["Flashing"] = typeof(FlashingDynamization),
        ["Expression"] = typeof(ExpressionDynamization),
        ["TagParameter"] = typeof(TagParameterDynamization),
    };

    // Reading these properties (V21) kills the whole TIA Portal process: the getter returns,
    // then TIA Portal exits shortly after, so the crash only surfaces on a later call. They
    // are never read or written.
    private static readonly HashSet<(Type, string)> CrashingProperties = new()
    {
        (typeof(HmiSystemDiagnosisControl), "ScriptDiagnosisOverviewText"),
    };

    private static bool IsCrashing(Type t, string property)
    {
        for (var type = t; type != null; type = type.BaseType)
            if (CrashingProperties.Contains((type, property))) return true;
        return false;
    }

    private static readonly Lazy<IReadOnlyDictionary<string, Type>> ItemTypeRegistry = new(BuildItemTypeRegistry);
    private static readonly Dictionary<Type, PropertyInfo[]> PropertyCache = new();

    private readonly Language? _language;

    public HmiScreenConverter(Language? editingLanguage)
    {
        _language = editingLanguage;
    }

    // ---- item types ----

    /// <summary>Short type name ("Button", "IOField", "FaceplateContainer", ...) to Openness type.</summary>
    public static IReadOnlyDictionary<string, Type> ItemTypes => ItemTypeRegistry.Value;

    private static IReadOnlyDictionary<string, Type> BuildItemTypeRegistry()
    {
        Type[] types;
        try { types = typeof(HmiScreenItemBase).Assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

        // The *Base classes are concrete in the API but are not creatable screen items.
        return types
            .Where(t => t.IsPublic && !t.IsAbstract && typeof(HmiScreenItemBase).IsAssignableFrom(t)
                        && t != typeof(HmiScreenItemBase) && !t.Name.EndsWith("Base", StringComparison.Ordinal))
            .ToDictionary(ShortTypeName, t => t, StringComparer.OrdinalIgnoreCase);
    }

    public static string ShortTypeName(Type t) => t.Name.StartsWith("Hmi", StringComparison.Ordinal) ? t.Name.Substring(3) : t.Name;

    public static Type? ResolveItemType(string name)
    {
        if (ItemTypes.TryGetValue(name, out var t)) return t;
        if (name.StartsWith("Hmi", StringComparison.OrdinalIgnoreCase) && ItemTypes.TryGetValue(name.Substring(3), out t)) return t;
        return null;
    }

    // Containers (faceplates, custom widgets, custom web controls) need their library type at
    // creation time: HmiScreenItemBaseComposition.Create<T>(name, containedType).
    public static bool NeedsContainedType(Type t) =>
        typeof(HmiContainerBase).IsAssignableFrom(t) || typeof(HmiCustomWidgetContainer).IsAssignableFrom(t) || typeof(HmiCustomWebControlContainer).IsAssignableFrom(t);

    public static HmiScreenItemBase CreateItem(HmiScreenItemBaseComposition items, Type type, string name, string? containedType)
    {
        var useContainedType = NeedsContainedType(type);
        if (useContainedType && string.IsNullOrEmpty(containedType))
            throw new InvalidOperationException($"'{ShortTypeName(type)}' needs a ContainedType (the library type and version, e.g. from list_library_types) to be created.");

        var create = items.GetType().GetMethods()
            .Single(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetParameters().Length == (useContainedType ? 2 : 1))
            .MakeGenericMethod(type);
        var args = useContainedType ? new object[] { name, containedType! } : new object[] { name };
        return (HmiScreenItemBase)Invoke(create, items, args)!;
    }

    // ---- read ----

    public JsonObject SerializeScreen(HmiScreen screen, Func<Type, string?, JsonObject?>? defaults)
    {
        var json = new JsonObject { [NameKey] = screen.Name };
        MoveInto(SerializeScreenRaw(screen), json);
        Prune(json, defaults?.Invoke(typeof(HmiScreen), null));

        var items = new JsonArray();
        foreach (HmiScreenItemBase item in screen.ScreenItems)
            items.Add(SerializeItem(item, defaults));
        json[ItemsKey] = items;
        return json;
    }

    public JsonObject SerializeItem(HmiScreenItemBase item, Func<Type, string?, JsonObject?>? defaults)
    {
        var json = SerializeItemRaw(item);
        var containedType = json["ContainedType"]?.GetValue<string>();
        Prune(json, defaults?.Invoke(item.GetType(), containedType));
        return json;
    }

    public JsonObject SerializeItemRaw(HmiScreenItemBase item)
    {
        var json = new JsonObject
        {
            [NameKey] = item.Name,
            [TypeKey] = ShortTypeName(item.GetType()),
        };
        MoveInto(SerializeObject(item, new HashSet<string> { "Name" }), json);
        return json;
    }

    public JsonObject SerializeScreenRaw(HmiScreen screen) => SerializeObject(screen, new HashSet<string> { "Name" });

    // A JsonNode can only have one parent, so entries are detached before re-adding.
    private static void MoveInto(JsonObject source, JsonObject target)
    {
        foreach (var key in source.Select(kv => kv.Key).ToList())
        {
            var value = source[key];
            source.Remove(key);
            target[key] = value;
        }
    }

    private JsonObject SerializeObject(object obj, ISet<string>? skip = null, int depth = 0)
    {
        var json = new JsonObject();
        if (depth > 8) return json;

        bool Included(PropertyInfo p) => !SkippedProperties.Contains(p.Name) && (skip == null || !skip.Contains(p.Name));
        var leafValues = GetValues(obj, GetProperties(obj.GetType()).Where(p => Included(p) && IsLeafType(p.PropertyType) && IsSettable(p)).ToList());

        foreach (var prop in GetProperties(obj.GetType()))
        {
            if (!Included(prop)) continue;
            var pt = prop.PropertyType;

            if (IsLeafType(pt))
            {
                if (!IsSettable(prop)) continue;
                var value = leafValues[prop.Name];
                var node = LeafToJson(value);
                if (node != null) json[prop.Name] = node;
            }
            else if (pt == typeof(MultilingualText))
            {
                var text = ReadText(TryGetValue(prop, obj) as MultilingualText);
                if (text != null) json[prop.Name] = text;
            }
            else if (IsComposition(pt))
            {
                if (TryGetValue(prop, obj) is not IEnumerable composition) continue;
                JsonNode? node = IsInterfaceComposition(pt) ? SerializeInterface(composition) : SerializeComposition(composition, depth);
                if (node != null) json[prop.Name] = node;
            }
            else if (TryGetValue(prop, obj) is IEngineeringObject nested)
            {
                var node = SerializeObject(nested, null, depth + 1);
                if (node.Count > 0) json[prop.Name] = node;
            }
        }

        if (obj is UIBase ui)
        {
            var dyn = SerializeDynamizations(ui.Dynamizations);
            if (dyn.Count > 0) json[DynamizationsKey] = dyn;
            var propEvents = SerializePropertyEvents(ui.PropertyEventHandlers);
            if (propEvents.Count > 0) json[PropertyEventsKey] = propEvents;
        }

        var eventsProp = Prop(obj.GetType(), "EventHandlers");
        if (eventsProp != null && TryGetValue(eventsProp, obj) is IEnumerable handlers)
        {
            var events = SerializeEvents(handlers);
            if (events.Count > 0) json[EventsKey] = events;
        }

        return json;
    }

    // Elements of a subtype (MappingTable entries: Range/Simple/Bitmask) get a "type", plus the
    // read-only enum a typed Create(...) needs (Bitmask's BitDynamizationType), so they can be
    // recreated on write.
    private JsonArray? SerializeComposition(IEnumerable composition, int depth)
    {
        var declared = IndexerType(composition.GetType());
        var createEnums = EnumCreateMethods(composition.GetType()).Select(m => m.GetParameters()[0].ParameterType).ToList();
        var array = new JsonArray();
        foreach (var element in composition)
        {
            if (element == null) continue;
            if (IsLeafType(element.GetType())) { array.Add(LeafToJson(element)); continue; }

            var json = new JsonObject();
            if (declared != null && element.GetType() != declared) json[TypeKey] = ShortTypeName(element.GetType());
            foreach (var prop in GetProperties(element.GetType()).Where(p => createEnums.Contains(p.PropertyType) && !IsSettable(p)))
                json[prop.Name] = LeafToJson(TryGetValue(prop, element));
            MoveInto(SerializeObject(element, null, depth + 1), json);
            array.Add(json);
        }
        return array.Count > 0 ? array : null;
    }

    private static IEnumerable<MethodInfo> EnumCreateMethods(Type compositionType) =>
        compositionType.GetMethods().Where(m => m.Name == "Create" && !m.IsGenericMethodDefinition
            && m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType.IsEnum);

    // Faceplate / custom control interface: { "<PropertyName>": <static value> | <dynamization> }.
    // A dynamization is always an object here, a tag binding too: a bare string is a static value
    // (a UDT-typed property's value is the HMI tag name).
    private JsonObject? SerializeInterface(IEnumerable composition)
    {
        var json = new JsonObject();
        foreach (var element in composition)
        {
            if (element == null) continue;
            var t = element.GetType();
            var name = Prop(t, "PropertyName")?.GetValue(element) as string;
            if (string.IsNullOrEmpty(name)) continue;

            if (element is UIBase ui && ui.Dynamizations.Count > 0)
            {
                var dyn = ui.Dynamizations[0];
                var node = SerializeDynamization(dyn) is JsonObject o ? o : new JsonObject { [TypeKey] = "Tag", ["Tag"] = ((TagDynamization)dyn).Tag };
                if (dyn.PropertyName != "Value") node["dynamizedProperty"] = dyn.PropertyName;
                json[name!] = node;
            }
            else
            {
                json[name!] = InterfaceValueToJson(TryGetValue(Prop(t, "Value")!, element));
            }
        }
        return json.Count > 0 ? json : null;
    }

    private JsonObject SerializeDynamizations(DynamizationBaseComposition dynamizations)
    {
        var json = new JsonObject();
        foreach (DynamizationBase dyn in dynamizations)
            json[dyn.PropertyName] = SerializeDynamization(dyn);
        return json;
    }

    // A plain tag binding is just the tag name; everything else is an object with a "type".
    private JsonNode SerializeDynamization(DynamizationBase dyn)
    {
        var body = SerializeObject(dyn);
        PruneEmpty(body);
        if (body["GlobalDefinitionAreaScriptCode"] is JsonValue g && IsCommentOnly(g.ToString())) body.Remove("GlobalDefinitionAreaScriptCode");
        if (body["Trigger"] is JsonObject trigger && trigger["Type"]?.ToString() != "CustomCycle") trigger.Remove("CustomDuration");
        if (dyn is TagDynamization tag && body.Count == 1 && body.ContainsKey("Tag"))
            return JsonValue.Create(tag.Tag)!;

        var json = new JsonObject { [TypeKey] = dyn.DynamizationType.ToString() };
        MoveInto(body, json);
        return json;
    }

    private JsonObject SerializeEvents(IEnumerable handlers)
    {
        var json = new JsonObject();
        foreach (var handler in handlers)
        {
            if (handler == null) continue;
            var eventType = Prop(handler.GetType(), "EventType")?.GetValue(handler)?.ToString();
            var script = Prop(handler.GetType(), "Script")?.GetValue(handler) as IHmiScript;
            if (eventType == null || script == null) continue;
            var node = SerializeScript(script);
            if (node != null) json[eventType] = node;
        }
        return json;
    }

    private JsonObject SerializePropertyEvents(PropertyEventHandlerComposition handlers)
    {
        var json = new JsonObject();
        foreach (PropertyEventHandler handler in handlers)
        {
            var node = SerializeScript(handler.Script);
            if (node != null) json[$"{handler.PropertyName}:{handler.EventType}"] = node;
        }
        return json;
    }

    // Just the code when that's all there is, otherwise { ScriptCode, GlobalDefinitionAreaScriptCode, Async }.
    // Global definition code that is only comments (TIA Portal's "//Insert definitions ..."
    // placeholder, "// [FunctionListModule]") is left out; writing a plain script string never
    // touches it, so it survives a round trip.
    private static JsonNode? SerializeScript(IHmiScript? script)
    {
        if (script == null || string.IsNullOrEmpty(script.ScriptCode)) return null;
        var global = IsCommentOnly(script.GlobalDefinitionAreaScriptCode) ? null : script.GlobalDefinitionAreaScriptCode;
        if (global == null && !script.Async)
            return JsonValue.Create(script.ScriptCode);
        var json = new JsonObject { ["ScriptCode"] = script.ScriptCode };
        if (global != null) json["GlobalDefinitionAreaScriptCode"] = global;
        if (script.Async) json["Async"] = true;
        return json;
    }

    private static bool IsCommentOnly(string? code) =>
        code == null || code.Split('\n').Select(l => l.Trim()).All(l => l.Length == 0 || l.StartsWith("//", StringComparison.Ordinal));

    // Drops everything equal to a freshly created object of the same type, so a read shows what
    // was actually configured. Only AlwaysKeep survives unconditionally.
    public static void Prune(JsonObject json, JsonObject? defaults)
    {
        if (defaults == null) return;
        foreach (var key in json.Select(kv => kv.Key).ToList())
        {
            if (AlwaysKeep.Contains(key) || !defaults.ContainsKey(key)) continue;
            var value = json[key];
            var def = defaults[key];
            if (JsonNode.DeepEquals(value, def))
            {
                json.Remove(key);
            }
            else if (value is JsonObject vo && def is JsonObject dobj)
            {
                Prune(vo, dobj);
                if (vo.Count == 0) json.Remove(key);
            }
        }
    }

    // Dynamization defaults can't be sampled from a fresh object (they only exist once bound),
    // so their subtrees drop the obvious "unset" values instead (false, "", "None", [""]).
    private static void PruneEmpty(JsonObject json)
    {
        foreach (var key in json.Select(kv => kv.Key).ToList())
        {
            var value = json[key];
            switch (value)
            {
                case null:
                    json.Remove(key);
                    break;
                case JsonObject o:
                    PruneEmpty(o);
                    if (o.Count == 0) json.Remove(key);
                    break;
                case JsonArray a when a.All(e => e == null || (e is JsonValue ev && ev.TryGetValue<string>(out var es) && es.Length == 0)):
                    json.Remove(key);
                    break;
                case JsonValue v when (v.TryGetValue<bool>(out var b) && !b) || (v.TryGetValue<string>(out var s) && (s.Length == 0 || s == "None")):
                    json.Remove(key);
                    break;
            }
        }
    }

    // ---- write ----

    public sealed class WriteStats
    {
        public int Created;
        public int Updated;
        public int Deleted;
        public List<string> Messages { get; } = new();
    }

    // baseJson: the screen as last read, known to still match the project. Then only what differs
    // from it is written - setting every property of every item takes minutes for a large screen.
    public WriteStats WriteScreen(HmiScreen screen, JsonObject json, bool deleteMissing, JsonObject? baseJson = null)
    {
        var stats = new WriteStats();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (json[ItemsKey] is JsonArray all)
        {
            foreach (var node in all)
                if (node is JsonObject o && o[NameKey] is JsonValue n && n.TryGetValue<string>(out var s)) keep.Add(s);
        }
        if (baseJson != null) json = ScreenJson.ChangedSince(json, baseJson);

        ApplyObject(screen, json, "screen", stats.Messages, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { NameKey, ItemsKey, "Name" });

        if (json[ItemsKey] is JsonArray items)
        {
            // One pass over the items instead of ScreenItems.Find per item: Find costs ~0.2 s per
            // call on a screen with a few hundred items, most of the time of a whole screen write.
            var existing = new Dictionary<string, HmiScreenItemBase>(StringComparer.Ordinal);
            foreach (HmiScreenItemBase item in screen.ScreenItems) existing[item.Name] = item;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in items)
            {
                if (node is not JsonObject itemJson) { stats.Messages.Add("FAILED items: every entry must be a JSON object."); continue; }
                var name = itemJson[NameKey]?.GetValue<string>();
                if (string.IsNullOrEmpty(name)) { stats.Messages.Add("FAILED items: an item has no \"name\"."); continue; }
                if (!seen.Add(name!)) { stats.Messages.Add($"FAILED '{name}': duplicate item name in the JSON, skipped."); continue; }
                WriteItem(screen.ScreenItems, existing, name!, itemJson, stats);
            }

            if (deleteMissing)
            {
                foreach (var item in screen.ScreenItems.Cast<HmiScreenItemBase>().Where(i => !keep.Contains(i.Name)).ToList())
                {
                    try { item.Delete(); stats.Deleted++; }
                    catch (Exception ex) { stats.Messages.Add($"FAILED deleting '{item.Name}': {ex.Message}"); }
                }
            }
        }
        else if (deleteMissing)
        {
            stats.Messages.Add("deleteMissing ignored: the JSON has no \"items\" array.");
        }

        return stats;
    }

    // existing: the screen's items by name, kept up to date here.
    private void WriteItem(HmiScreenItemBaseComposition items, Dictionary<string, HmiScreenItemBase> existing, string name, JsonObject itemJson, WriteStats stats)
    {
        var typeName = itemJson[TypeKey]?.GetValue<string>();
        Type? type = null;
        if (typeName != null)
        {
            type = ResolveItemType(typeName);
            if (type == null)
            {
                stats.Messages.Add($"FAILED '{name}': unknown item type '{typeName}'. Known types: {string.Join(", ", ItemTypes.Keys.OrderBy(k => k))}.");
                return;
            }
        }

        var containedType = itemJson["ContainedType"]?.GetValue<string>();
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { NameKey, TypeKey, "Name" };
        existing.TryGetValue(name, out var item);

        if (item != null && type != null && item.GetType() != type)
        {
            try { item.Delete(); existing.Remove(name); item = null; }
            catch (Exception ex) { stats.Messages.Add($"FAILED '{name}': type change {ShortTypeName(item!.GetType())} -> {typeName} needs a delete + create, delete failed: {ex.Message}"); return; }
        }

        if (item == null)
        {
            if (type == null) { stats.Messages.Add($"FAILED '{name}': item does not exist yet and has no \"type\"."); return; }
            try { item = existing[name] = CreateItem(items, type, name, containedType); }
            catch (Exception ex) { stats.Messages.Add($"FAILED creating '{name}' ({typeName}): {Unwrap(ex).Message}"); return; }
            skip.Add("ContainedType");
            stats.Created++;
        }
        else
        {
            stats.Updated++;
        }

        ApplyObject(item, itemJson, name, stats.Messages, skip);
    }

    public void ApplyObject(object target, JsonObject json, string path, List<string> messages, ISet<string>? skip = null)
    {
        // Leaf values: current values read in one call up front, changes set in one call per run of
        // leaf keys. The run is set before the next other key, so the order stays as in the JSON:
        // e.g. a mapping table's ConditionType decides which entry type Entries creates.
        var leafProps = json.Select(kv => kv.Key).Where(k => skip == null || !skip.Contains(k))
            .Select(k => FindProperty(target.GetType(), k)).OfType<PropertyInfo>().Where(p => IsLeafType(p.PropertyType)).Distinct().ToList();
        var leafKeys = new HashSet<string>(leafProps.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        var currentValues = GetValues(target, leafProps);
        var pending = new List<(PropertyInfo Prop, object? Value, string Path)>();

        foreach (var kv in json)
        {
            var key = kv.Key;
            var node = kv.Value;
            if (skip != null && skip.Contains(key)) continue;
            var keyPath = $"{path}.{key}";
            if (!leafKeys.Contains(key)) SetValues(target, pending, messages);

            try
            {
                if (key == DynamizationsKey)
                {
                    if (target is UIBase ui && node is JsonObject dyns) ApplyDynamizations(ui.Dynamizations, dyns, keyPath, messages);
                    else messages.Add($"FAILED '{keyPath}': not supported on this object (or not a JSON object).");
                    continue;
                }
                if (key == EventsKey)
                {
                    var handlers = Prop(target.GetType(), "EventHandlers")?.GetValue(target);
                    if (handlers != null && node is JsonObject events) ApplyEvents(handlers, events, keyPath, messages);
                    else messages.Add($"FAILED '{keyPath}': this object has no events (or not a JSON object).");
                    continue;
                }
                if (key == PropertyEventsKey)
                {
                    if (target is UIBase ui && node is JsonObject events) ApplyPropertyEvents(ui.PropertyEventHandlers, events, keyPath, messages);
                    else messages.Add($"FAILED '{keyPath}': not supported on this object (or not a JSON object).");
                    continue;
                }

                var prop = FindProperty(target.GetType(), key);
                if (prop == null)
                {
                    messages.Add($"FAILED '{keyPath}': '{target.GetType().Name}' has no property '{key}' (see describe_hmi_item_type).");
                    continue;
                }

                var pt = prop.PropertyType;
                if (pt == typeof(MultilingualText))
                {
                    WriteText(prop.GetValue(target) as MultilingualText, node is JsonValue v ? v.ToString() : node?.ToJsonString() ?? "");
                }
                else if (IsLeafType(pt))
                {
                    var current = currentValues.TryGetValue(prop.Name, out var c) ? c : TryGetValue(prop, target);
                    var value = JsonToValue(node, pt, current);
                    if (!IsSettable(prop))
                    {
                        // Read-only values that a screen read emits (e.g. a bitmask entry's
                        // BitDynamizationType) are fine as long as they already match.
                        if (!Equals(current, value)) messages.Add($"FAILED '{keyPath}': read-only.");
                        continue;
                    }
                    if (Equals(current, value)) continue;
                    if (target is ValueConverter converter && prop.Name == nameof(ValueConverter.Formula) && !converter.IsFormulaSelected)
                    {
                        SetValues(target, pending, messages);
                        // A formula can only be set while it's the selected conversion; a value
                        // converter that uses its mapping table can still carry one.
                        converter.IsFormulaSelected = true;
                        try { converter.Formula = (string)value!; }
                        finally { converter.IsFormulaSelected = false; }
                        continue;
                    }
                    pending.Add((prop, value, keyPath));
                }
                else if (IsComposition(pt))
                {
                    var composition = prop.GetValue(target);
                    if (IsInterfaceComposition(pt))
                    {
                        if (node is JsonObject iface) ApplyInterface(composition!, iface, keyPath, messages);
                        else messages.Add($"FAILED '{keyPath}': expected a JSON object of interface property name -> value.");
                    }
                    else if (node is JsonArray array) ApplyComposition(composition!, array, keyPath, messages);
                    else messages.Add($"FAILED '{keyPath}': expected a JSON array.");
                }
                else if (node is JsonObject nestedJson && prop.GetValue(target) is { } nested)
                {
                    ApplyObject(nested, nestedJson, keyPath, messages);
                }
                else
                {
                    messages.Add($"FAILED '{keyPath}': expected a JSON object for '{pt.Name}'.");
                }
            }
            catch (Exception ex)
            {
                messages.Add($"FAILED '{keyPath}': {Unwrap(ex).Message}");
            }
        }

        SetValues(target, pending, messages);
    }

    // One Openness call for the values of many properties instead of a getter call each (~5x
    // faster; most of the time of a screen read). A property that throws (Openness does for one
    // that doesn't apply in the object's current configuration) fails the whole call, so then
    // each is read on its own.
    private static Dictionary<string, object?> GetValues(object obj, IReadOnlyList<PropertyInfo> props)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (props.Count == 0) return result;
        if (obj is IEngineeringObject eo && props.Count > 1)
        {
            try
            {
                var values = eo.GetAttributes(props.Select(p => p.Name));
                if (values.Count == props.Count)
                {
                    for (var i = 0; i < props.Count; i++) result[props[i].Name] = values[i];
                    return result;
                }
            }
            catch
            {
                // read one by one below
            }
        }
        foreach (var p in props) result[p.Name] = TryGetValue(p, obj);
        return result;
    }

    // One SetAttributes call instead of a setter call each (~5x faster). It needs every value in
    // the property's exact type, and stops at the first value TIA Portal rejects (the earlier ones
    // are set), throwing for the whole call - then each value is set on its own, so the failure is
    // reported for its property and the rest still applies.
    // Sets and clears pending.
    private static void SetValues(object target, List<(PropertyInfo Prop, object? Value, string Path)> pending, List<string> messages)
    {
        if (pending.Count == 0) return;
        var batched = false;
        if (target is IEngineeringObject eo && pending.Count > 1 && pending.All(p => p.Value != null && p.Value.GetType() == p.Prop.PropertyType))
        {
            try
            {
                eo.SetAttributes(pending.Select(p => new KeyValuePair<string, object>(p.Prop.Name, p.Value!)));
                batched = true;
            }
            catch
            {
                // set one by one below
            }
        }
        if (!batched)
        {
            foreach (var (prop, value, path) in pending)
            {
                try { prop.SetValue(target, value); }
                catch (Exception ex) { messages.Add($"FAILED '{path}': {Unwrap(ex).Message}"); }
            }
        }
        pending.Clear();
    }

    // Elements are matched by position; extra JSON entries are created (Create(), Create<T>(),
    // Create(name) - e.g. trend axes -, Create(enum) - mapping table bitmask entries - or, for
    // Points, Create(x, y)), extra existing elements are deleted.
    private void ApplyComposition(object composition, JsonArray array, string path, List<string> messages)
    {
        var existing = ((IEnumerable)composition).Cast<object>().ToList();
        var compType = composition.GetType();
        // Create(BitDynamizationType) adds a whole set of entries at once (e.g. both bits of a
        // single bit table): the ones after the first serve the next JSON entries.
        var spare = new Queue<object>();

        for (var i = 0; i < array.Count; i++)
        {
            var elementPath = $"{path}[{i}]";
            if (array[i] is not JsonObject elementJson) { messages.Add($"FAILED '{elementPath}': expected a JSON object."); continue; }

            object? element = i < existing.Count ? existing[i] : null;
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { TypeKey };

            // A different subtype can't be converted in place: replace the element.
            var subtype = ResolveElementType(compType, elementJson[TypeKey]?.ToString(), elementPath, messages, out var typeError);
            if (typeError) continue;
            if (element != null && subtype != null && element.GetType() != subtype)
            {
                try { Invoke(element.GetType().GetMethod("Delete", Type.EmptyTypes)!, element, Array.Empty<object>()); element = null; }
                catch (Exception ex) { messages.Add($"FAILED '{elementPath}': replacing it by a {ShortTypeName(subtype)} failed: {Unwrap(ex).Message}"); continue; }
            }

            if (element == null && spare.Count > 0 && (subtype == null || spare.Peek().GetType() == subtype))
                element = spare.Dequeue();

            if (element == null)
            {
                try
                {
                    var createName = compType.GetMethod("Create", new[] { typeof(string) });
                    var createNoArgs = compType.GetMethods().FirstOrDefault(m => m.Name == "Create" && !m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                    var createGeneric = compType.GetMethods().FirstOrDefault(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                    var createEnum = subtype == null ? null : EnumCreateMethods(compType)
                        .FirstOrDefault(m => Prop(subtype, m.GetParameters()[0].ParameterType.Name) != null && elementJson[m.GetParameters()[0].ParameterType.Name] != null);
                    var createXY = compType.GetMethod("Create", new[] { typeof(int), typeof(int) });
                    if (createEnum != null)
                    {
                        var enumType = createEnum.GetParameters()[0].ParameterType;
                        var created = Invoke(createEnum, composition, new[] { Enum.Parse(enumType, elementJson[enumType.Name]!.ToString(), true) });
                        if (created is IEnumerable set && created is not IEngineeringObject)
                        {
                            var entries = set.Cast<object>().ToList();
                            element = entries.FirstOrDefault();
                            foreach (var extra in entries.Skip(1)) spare.Enqueue(extra);
                        }
                        else
                        {
                            element = created;
                        }
                        if (element == null) { messages.Add($"FAILED '{elementPath}': create created nothing."); continue; }
                        skip.Add(enumType.Name);
                    }
                    else if (createGeneric != null)
                    {
                        if (subtype == null)
                        {
                            messages.Add($"FAILED '{elementPath}': needs a \"type\" ({string.Join(", ", ElementSubtypes(compType).Select(ShortTypeName))}).");
                            continue;
                        }
                        element = Invoke(createGeneric.MakeGenericMethod(subtype), composition, Array.Empty<object>());
                    }
                    else if (createNoArgs != null)
                    {
                        element = Invoke(createNoArgs, composition, Array.Empty<object>());
                    }
                    else if (createXY != null && elementJson["X"] != null && elementJson["Y"] != null)
                    {
                        element = Invoke(createXY, composition, new object[] { elementJson["X"]!.GetValue<int>(), elementJson["Y"]!.GetValue<int>() });
                        skip.Add("X");
                        skip.Add("Y");
                    }
                    else if (createName != null)
                    {
                        var name = elementJson["Name"]?.ToString() ?? $"{ShortTypeName(IndexerType(compType) ?? compType)}_{i + 1}";
                        element = Invoke(createName, composition, new object[] { name });
                        skip.Add("Name");
                    }
                    else
                    {
                        messages.Add($"FAILED '{elementPath}': '{compType.Name}' cannot grow via Openness (it has {existing.Count} element(s)).");
                        continue;
                    }
                }
                catch (Exception ex) { messages.Add($"FAILED '{elementPath}': create failed: {Unwrap(ex).Message}"); continue; }
            }
            ApplyObject(element!, elementJson, elementPath, messages, skip);
        }

        foreach (var extra in spare)
        {
            try { Invoke(extra.GetType().GetMethod("Delete", Type.EmptyTypes)!, extra, Array.Empty<object>()); }
            catch (Exception ex) { messages.Add($"FAILED '{path}': deleting an entry created along with others failed: {Unwrap(ex).Message}"); }
        }

        for (var i = existing.Count - 1; i >= array.Count; i--)
        {
            var delete = existing[i].GetType().GetMethod("Delete", Type.EmptyTypes);
            if (delete == null) { messages.Add($"FAILED '{path}[{i}]': '{compType.Name}' elements cannot be deleted via Openness."); continue; }
            try { Invoke(delete, existing[i], Array.Empty<object>()); }
            catch (Exception ex) { messages.Add($"FAILED '{path}[{i}]': delete failed: {Unwrap(ex).Message}"); }
        }
    }

    private static IEnumerable<Type> ElementSubtypes(Type compositionType)
    {
        var declared = IndexerType(compositionType);
        if (declared == null) return Enumerable.Empty<Type>();
        return declared.Assembly.GetExportedTypes().Where(t => declared.IsAssignableFrom(t) && t != declared && !t.IsAbstract);
    }

    private static Type? ResolveElementType(Type compositionType, string? name, string path, List<string> messages, out bool error)
    {
        error = false;
        if (name == null) return null;
        var match = ElementSubtypes(compositionType).FirstOrDefault(t =>
            string.Equals(ShortTypeName(t), name, StringComparison.OrdinalIgnoreCase) || string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            error = true;
            messages.Add($"FAILED '{path}': unknown element type '{name}'. Use one of: {string.Join(", ", ElementSubtypes(compositionType).Select(ShortTypeName))}.");
        }
        return match;
    }

    private void ApplyInterface(object composition, JsonObject json, string path, List<string> messages)
    {
        var find = composition.GetType().GetMethod("Find", new[] { typeof(string) });
        var available = ((IEnumerable)composition).Cast<object>()
            .Select(e => Prop(e.GetType(), "PropertyName")?.GetValue(e) as string).Where(n => n != null).ToList();

        foreach (var kv in json)
        {
            var keyPath = $"{path}.{kv.Key}";
            var element = find?.Invoke(composition, new object[] { kv.Key });
            if (element == null)
            {
                messages.Add($"FAILED '{keyPath}': no interface property '{kv.Key}'. Available: {string.Join(", ", available)}.");
                continue;
            }

            try
            {
                var ui = element as UIBase;
                if (IsDynamizationJson(kv.Value))
                {
                    if (ui == null) { messages.Add($"FAILED '{keyPath}': this interface property cannot be dynamized."); continue; }
                    ApplyInterfaceDynamization(ui, kv.Key, kv.Value!, keyPath, messages);
                }
                else
                {
                    // A static value replaces any tag/script binding - once it converts, so a value
                    // that doesn't fit leaves the binding alone.
                    var valueProp = Prop(element.GetType(), "Value")!;
                    var current = TryGetValue(valueProp, element);
                    var value = JsonToValue(kv.Value, typeof(object), current);
                    var dyns = ui == null ? new List<DynamizationBase>() : ui.Dynamizations.Cast<DynamizationBase>().ToList();
                    foreach (var dyn in dyns) dyn.Delete();
                    if (current is string currentText && value is string newText && ProjectTextToPlain(currentText) is { } currentPlain
                        && !newText.StartsWith("<", StringComparison.Ordinal))
                    {
                        if (currentPlain == newText) continue;
                        value = ProjectTextHeader + PlainToHtml(newText) + "</ProjectText>";
                    }
                    if (dyns.Count > 0 || !Equals(current, value)) valueProp.SetValue(element, value);
                }
            }
            catch (Exception ex)
            {
                messages.Add($"FAILED '{keyPath}': {Unwrap(ex).Message}");
            }
        }
    }

    // Which property name Openness wants for an interface dynamization isn't documented: try the
    // explicit "dynamizedProperty" (as read back), then "Value", then the interface property's
    // own name, and report every attempt if none sticks.
    private void ApplyInterfaceDynamization(UIBase element, string interfaceName, JsonNode node, string path, List<string> messages)
    {
        var candidates = new List<string>();
        if (node is JsonObject o && o["dynamizedProperty"] is JsonValue dp) candidates.Add(dp.ToString());
        foreach (var existing in element.Dynamizations.Cast<DynamizationBase>()) candidates.Add(existing.PropertyName);
        candidates.Add("Value");
        candidates.Add(interfaceName);

        var errors = new List<string>();
        foreach (var candidate in candidates.Distinct(StringComparer.Ordinal))
        {
            var attemptMessages = new List<string>();
            try
            {
                ApplyDynamization(element.Dynamizations, candidate, node, path, attemptMessages);
            }
            catch (Exception ex)
            {
                attemptMessages.Add(Unwrap(ex).Message);
            }

            if (attemptMessages.Count == 0)
            {
                foreach (var other in element.Dynamizations.Cast<DynamizationBase>().Where(d => d.PropertyName != candidate).ToList()) other.Delete();
                return;
            }
            errors.Add($"as '{candidate}': {string.Join("; ", attemptMessages)}");
        }
        messages.Add($"FAILED '{path}': could not bind - {string.Join(" | ", errors)}");
    }

    private static bool IsDynamizationJson(JsonNode? node) => node is JsonObject;

    private void ApplyDynamizations(DynamizationBaseComposition dynamizations, JsonObject json, string path, List<string> messages)
    {
        foreach (var kv in json)
        {
            var keyPath = $"{path}.{kv.Key}";
            try { ApplyDynamization(dynamizations, kv.Key, kv.Value, keyPath, messages); }
            catch (Exception ex) { messages.Add($"FAILED '{keyPath}': {Unwrap(ex).Message}"); }
        }
    }

    // null removes the dynamization; a string is a tag binding; an object has a "type"
    // (Tag/Script/ResourceList/Flashing/Expression/TagParameter, default Tag) plus that
    // dynamization's own properties (Tag, ScriptCode, Trigger, ...).
    private void ApplyDynamization(DynamizationBaseComposition dynamizations, string propertyName, JsonNode? node, string path, List<string> messages)
    {
        var existing = dynamizations.Find(propertyName);
        if (node == null)
        {
            existing?.Delete();
            return;
        }

        JsonObject body;
        string typeName;
        if (node is JsonValue v)
        {
            typeName = "Tag";
            body = new JsonObject { ["Tag"] = v.ToString() };
        }
        else if (node is JsonObject o)
        {
            typeName = o[TypeKey]?.ToString() ?? "Tag";
            body = o;
        }
        else
        {
            messages.Add($"FAILED '{path}': expected a tag name, a dynamization object or null.");
            return;
        }

        if (!DynamizationTypes.TryGetValue(typeName, out var dynType))
        {
            messages.Add($"FAILED '{path}': unknown dynamization type '{typeName}'. Use one of: {string.Join(", ", DynamizationTypes.Keys)}.");
            return;
        }

        if (existing != null && existing.GetType() != dynType)
        {
            existing.Delete();
            existing = null;
        }
        if (existing == null)
        {
            var create = dynamizations.GetType().GetMethods()
                .Single(m => m.Name == "Create" && m.IsGenericMethodDefinition)
                .MakeGenericMethod(dynType);
            existing = (DynamizationBase)Invoke(create, dynamizations, new object[] { propertyName })!;
        }

        ApplyObject(existing, body, path, messages, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { TypeKey, "dynamizedProperty" });
    }

    private void ApplyEvents(object handlers, JsonObject json, string path, List<string> messages)
    {
        var compType = handlers.GetType();
        var find = compType.GetMethods().FirstOrDefault(m => m.Name == "Find" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsEnum);
        var create = compType.GetMethods().FirstOrDefault(m => m.Name == "Create" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsEnum);
        if (find == null || create == null) { messages.Add($"FAILED '{path}': events can't be configured on this object."); return; }
        var enumType = find.GetParameters()[0].ParameterType;

        foreach (var kv in json)
        {
            var keyPath = $"{path}.{kv.Key}";
            object eventType;
            try { eventType = Enum.Parse(enumType, kv.Key, true); }
            catch { messages.Add($"FAILED '{keyPath}': unknown event. Available: {string.Join(", ", Enum.GetNames(enumType).Where(n => n != "None"))}."); continue; }

            try
            {
                var handler = find.Invoke(handlers, new[] { eventType });
                if (IsEmptyScript(kv.Value))
                {
                    if (handler != null) Invoke(handler.GetType().GetMethod("Delete", Type.EmptyTypes)!, handler, Array.Empty<object>());
                    continue;
                }
                handler ??= Invoke(create, handlers, new[] { eventType });
                var script = (IHmiScript)Prop(handler!.GetType(), "Script")!.GetValue(handler)!;
                ApplyScript(script, kv.Value!);
            }
            catch (Exception ex)
            {
                messages.Add($"FAILED '{keyPath}': {Unwrap(ex).Message}");
            }
        }
    }

    // Keys are "<PropertyName>:<Change|QualityCodeChange>" (":Change" may be omitted).
    private void ApplyPropertyEvents(PropertyEventHandlerComposition handlers, JsonObject json, string path, List<string> messages)
    {
        foreach (var kv in json)
        {
            var keyPath = $"{path}.{kv.Key}";
            var parts = kv.Key.Split(':');
            var propertyName = parts[0];
            PropertyEventType eventType;
            try { eventType = parts.Length > 1 ? (PropertyEventType)Enum.Parse(typeof(PropertyEventType), parts[1], true) : PropertyEventType.Change; }
            catch { messages.Add($"FAILED '{keyPath}': unknown property event. Use '<Property>:Change' or '<Property>:QualityCodeChange'."); continue; }

            try
            {
                var handler = handlers.Find(propertyName, eventType);
                if (IsEmptyScript(kv.Value))
                {
                    handler?.Delete();
                    continue;
                }
                handler ??= handlers.Create(propertyName, eventType);
                ApplyScript(handler.Script, kv.Value!);
            }
            catch (Exception ex)
            {
                messages.Add($"FAILED '{keyPath}': {Unwrap(ex).Message}");
            }
        }
    }

    private static bool IsEmptyScript(JsonNode? node) => node == null || (node is JsonValue v && v.ToString().Length == 0);

    private static void ApplyScript(IHmiScript script, JsonNode node)
    {
        if (node is JsonObject o)
        {
            if (o["ScriptCode"] != null) script.ScriptCode = o["ScriptCode"]!.ToString();
            if (o["GlobalDefinitionAreaScriptCode"] != null) script.GlobalDefinitionAreaScriptCode = o["GlobalDefinitionAreaScriptCode"]!.ToString();
            if (o["Async"] != null) script.Async = o["Async"]!.GetValue<bool>();
        }
        else
        {
            var code = node.ToString();
            if (script.ScriptCode != code) script.ScriptCode = code;
        }
    }

    // ---- multilingual text ----

    private static readonly Regex SimpleHtml = new(@"^<body>((?:<p>.*?</p>)*)</body>$", RegexOptions.Singleline);
    private static readonly Regex Paragraph = new(@"<p>(.*?)</p>", RegexOptions.Singleline);
    private static readonly Regex LineBreak = new(@"<br\s*/?>");
    private static readonly Regex ProjectText = new(@"^<\?xml[^>]*\?>\s*<ProjectText[^>]*>(.*)</ProjectText>\s*$", RegexOptions.Singleline);
    private const string ProjectTextHeader = "<?xml version=\"1.0\" encoding=\"utf-8\"?><ProjectText xmlns=\"http://www.siemens.com/Industry/2009/10/01/Automation/FormattedText\">";

    // Unified screen texts are stored like alarm texts: an HTML fragment "<body><p>...</p></body>",
    // line breaks as <br />. Plain paragraphs are shown as plain text (lines joined with \n); text
    // with any other markup (formatting spans) is returned raw so a round trip keeps it.
    private string? ReadText(MultilingualText? text)
    {
        if (text == null) return null;
        var item = _language != null ? text.Items.Find(_language) : null;
        if (item == null) return null;
        // An empty text is "", not left out: a new item's default text (e.g. "Text") isn't empty.
        var raw = item.Text;
        return string.IsNullOrEmpty(raw) ? "" : HtmlToPlain(raw!);
    }

    private static string HtmlToPlain(string raw)
    {
        var match = SimpleHtml.Match(raw.Replace("<p/>", "<p></p>"));
        if (!match.Success) return raw;
        var paragraphs = Paragraph.Matches(match.Groups[1].Value).Cast<Match>().Select(m => LineBreak.Replace(m.Groups[1].Value, "\n")).ToList();
        if (paragraphs.Any(p => p.Contains('<'))) return raw;
        return string.Join("\n", paragraphs.Select(p => p.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&")));
    }

    private void WriteText(MultilingualText? text, string value)
    {
        if (text == null || _language == null) return;
        var item = text.Items.Find(_language);
        if (item == null) return;

        var current = item.Text ?? "";
        if (current == value || HtmlToPlain(current) == value) return;

        if (value.StartsWith("<body", StringComparison.OrdinalIgnoreCase))
        {
            item.Text = value;
            return;
        }

        var html = PlainToHtml(value);
        if (current.StartsWith("<body", StringComparison.OrdinalIgnoreCase))
        {
            item.Text = html;
            return;
        }
        try { item.Text = value; }
        catch { item.Text = html; }
    }

    private static string PlainToHtml(string value) =>
        "<body><p>" + string.Join("<br />", value.Replace("\r\n", "\n").Split('\n')
            .Select(line => line.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;"))) + "</p></body>";

    // Text-typed faceplate interface properties hold a whole ProjectText XML document around the
    // same <body> fragment; shown as plain text when it is plain, rewrapped on write.
    private static string? ProjectTextToPlain(string value)
    {
        var match = ProjectText.Match(value);
        if (!match.Success) return null;
        var plain = HtmlToPlain(match.Groups[1].Value);
        return plain.StartsWith("<", StringComparison.Ordinal) ? null : plain;
    }

    private static JsonNode? InterfaceValueToJson(object? value) =>
        value is string text && ProjectTextToPlain(text) is { } plain ? JsonValue.Create(plain) : LeafToJson(value);

    // ---- value conversion ----

    private static bool IsLeafType(Type t) =>
        t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(Color) || t == typeof(object)
        || (Nullable.GetUnderlyingType(t) is { } u && IsLeafType(u));

    private static bool IsComposition(Type t) =>
        t != typeof(string) && typeof(IEnumerable).IsAssignableFrom(t) && typeof(IEngineeringComposition).IsAssignableFrom(t);

    // Faceplate/custom-control interfaces: element type with PropertyName (read-only) + Value.
    private static bool IsInterfaceComposition(Type t)
    {
        var element = IndexerType(t);
        return element != null && Prop(element, "PropertyName") != null && Prop(element, "Value") != null;
    }

    private static bool IsSettable(PropertyInfo p) => p.SetMethod != null && p.SetMethod.IsPublic;

    // Derived Openness types redeclare base properties with a narrower type (e.g. EventHandlers),
    // so Type.GetProperty(name) throws AmbiguousMatchException and GetProperties() returns both;
    // keep only the most derived declaration per name.
    private static IEnumerable<PropertyInfo> GetProperties(Type t)
    {
        lock (PropertyCache)
        {
            if (!PropertyCache.TryGetValue(t, out var props))
            {
                props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.GetMethod != null && p.GetIndexParameters().Length == 0 && !IsCrashing(t, p.Name))
                    .GroupBy(p => p.Name, StringComparer.Ordinal)
                    .Select(g => g.OrderByDescending(p => InheritanceDepth(p.DeclaringType)).First())
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .ToArray();
                PropertyCache[t] = props;
            }
            return props;
        }
    }

    private static int InheritanceDepth(Type? t)
    {
        var depth = 0;
        for (; t != null; t = t.BaseType) depth++;
        return depth;
    }

    private static PropertyInfo? Prop(Type t, string name) => GetProperties(t).FirstOrDefault(p => p.Name == name);

    private static Type? IndexerType(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name == "Item" && p.GetIndexParameters() is { Length: 1 } ip && ip[0].ParameterType == typeof(int))
            .OrderByDescending(p => InheritanceDepth(p.DeclaringType))
            .FirstOrDefault()?.PropertyType;

    private static PropertyInfo? FindProperty(Type t, string name) =>
        GetProperties(t).FirstOrDefault(p => p.Name == name) ?? GetProperties(t).FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    // Openness throws for properties that don't apply in the object's current configuration.
    private static object? TryGetValue(PropertyInfo prop, object obj)
    {
        try { return prop.GetValue(obj); }
        catch { return null; }
    }

    public static JsonNode? LeafToJson(object? value)
    {
        switch (value)
        {
            case null: return null;
            case string s: return JsonValue.Create(s);
            case bool b: return JsonValue.Create(b);
            case Enum e: return JsonValue.Create(e.ToString());
            case Color c: return JsonValue.Create($"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}");
            // A JSON number as a decimal (no binary noise like 0.1 -> 0.10000000149); what a decimal
            // can't hold (NaN, infinity, huge values) as a round-trip string, which JsonToValue parses.
            case float f: return FitsDecimal(f) ? JsonValue.Create((decimal)f) : JsonValue.Create(f.ToString("R", CultureInfo.InvariantCulture));
            case double d: return FitsDecimal(d) ? JsonValue.Create((decimal)d) : JsonValue.Create(d.ToString("R", CultureInfo.InvariantCulture));
            case decimal m: return JsonValue.Create(m);
            case ulong ul: return JsonValue.Create(ul);
            case IConvertible when value.GetType().IsPrimitive: return JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            case IEnumerable list:
                var array = new JsonArray();
                foreach (var element in list) array.Add(LeafToJson(element));
                return array;
            default: return JsonValue.Create(value.ToString());
        }
    }

    private static bool FitsDecimal(double d) => !double.IsNaN(d) && !double.IsInfinity(d) && Math.Abs(d) <= 1e20;

    private static object? JsonToValue(JsonNode? node, Type target, object? current)
    {
        if (node == null) return null;
        var underlying = Nullable.GetUnderlyingType(target);
        if (underlying != null) target = underlying;

        if (target == typeof(object))
        {
            // Untyped (faceplate interface Value, trigger tag list): keep the current value's type
            // when there is one, otherwise take the natural JSON type.
            if (current != null && current is not string && IsLeafType(current.GetType()))
                return JsonToValue(node, current.GetType(), null);
            // Nothing to go by (a fresh mapping table entry): colors are written as '#AARRGGBB'.
            if (current == null && node is JsonValue cv && cv.TryGetValue<string>(out var cs) && Regex.IsMatch(cs, "^#[0-9A-Fa-f]{8}$"))
                return ParseColor(cs);
            return NaturalValue(node);
        }

        if (node is not JsonValue value) throw new FormatException($"expected a single value for '{target.Name}', got {node.ToJsonString()}");
        var text = value.TryGetValue<string>(out var s) ? s : value.ToJsonString();

        if (target == typeof(string)) return text;
        if (target == typeof(bool))
        {
            if (value.TryGetValue<bool>(out var b)) return b;
            return bool.Parse(text);
        }
        if (target.IsEnum) return Enum.Parse(target, text, true);
        if (target == typeof(Color)) return ParseColor(text);
        if (target == typeof(double) || target == typeof(float))
        {
            var d = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            return target == typeof(float) ? (object)(float)d : d;
        }
        return Convert.ChangeType(decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture), target, CultureInfo.InvariantCulture);
    }

    private static object? NaturalValue(JsonNode node)
    {
        switch (node)
        {
            case JsonArray array:
                return array.Select(e => e == null ? null : NaturalValue(e)?.ToString()).ToList();
            case JsonValue value:
                // A parsed value holds a JsonElement, a cloned one (see ScreenJson.ChangedSince) the CLR value.
                switch (value.GetValueKind())
                {
                    case JsonValueKind.True: return true;
                    case JsonValueKind.False: return false;
                    case JsonValueKind.Number:
                        if (value.TryGetValue<int>(out var i)) return i;
                        if (value.TryGetValue<long>(out var l)) return l;
                        return value.GetValue<double>();
                    case JsonValueKind.String: return value.GetValue<string>();
                    default: return value.ToJsonString();
                }
            default:
                return node.ToJsonString();
        }
    }

    private static Color ParseColor(string text)
    {
        var hex = text.Trim();
        if (hex.StartsWith("#")) hex = hex.Substring(1);
        if (Regex.IsMatch(hex, "^[0-9A-Fa-f]{6}$")) hex = "FF" + hex;
        if (Regex.IsMatch(hex, "^[0-9A-Fa-f]{8}$"))
            return Color.FromArgb(int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        var named = Color.FromName(text.Trim());
        if (named.IsKnownColor) return named;
        throw new FormatException($"'{text}' is not a color - use '#AARRGGBB', '#RRGGBB' or a .NET color name.");
    }

    private static object? Invoke(MethodInfo method, object target, object[] args)
    {
        try { return method.Invoke(target, args); }
        catch (TargetInvocationException ex) when (ex.InnerException != null) { throw ex.InnerException; }
    }

    private static Exception Unwrap(Exception ex) => ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;

    // ---- describe ----

    public static string Describe(Type type)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{ShortTypeName(type)} ({type.FullName})");
        if (NeedsContainedType(type)) sb.AppendLine("Needs ContainedType at creation (library type + version).");
        sb.AppendLine("Properties (JSON key: type; the item's own name is the \"name\" key):");
        DescribeProperties(type, "  ", sb, 0);

        var events = Prop(type, "EventHandlers")?.PropertyType.GetMethods()
            .FirstOrDefault(m => m.Name == "Create" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsEnum)?
            .GetParameters()[0].ParameterType;
        if (events != null)
            sb.AppendLine($"Events (\"events\" keys): {string.Join(", ", Enum.GetNames(events).Where(n => n != "None"))}");
        if (typeof(UIBase).IsAssignableFrom(type))
        {
            sb.AppendLine($"Dynamizations (\"dynamizations\": {{ \"<Property>\": ... }}): a tag name string, or {{ \"type\": {string.Join("|", DynamizationTypes.Keys)}, ... }}; null removes it.");
            sb.AppendLine("Property events (\"propertyEvents\"): \"<Property>:Change\" or \"<Property>:QualityCodeChange\" -> script code.");
        }
        return sb.ToString();
    }

    private static void DescribeProperties(Type type, string indent, System.Text.StringBuilder sb, int depth)
    {
        if (depth > 4) return;
        foreach (var prop in GetProperties(type))
        {
            if (SkippedProperties.Contains(prop.Name) || (depth == 0 && prop.Name == "Name")) continue;
            var pt = prop.PropertyType;
            if (IsLeafType(pt))
            {
                if (!IsSettable(prop)) continue;
                var desc = pt.IsEnum ? string.Join("|", Enum.GetNames(pt)) : pt == typeof(Color) ? "color '#AARRGGBB'" : pt == typeof(object) ? "any" : pt.Name;
                sb.AppendLine($"{indent}{prop.Name}: {desc}");
            }
            else if (pt == typeof(MultilingualText))
            {
                sb.AppendLine($"{indent}{prop.Name}: text (editing language)");
            }
            else if (IsComposition(pt))
            {
                var element = IndexerType(pt);
                if (IsInterfaceComposition(pt))
                {
                    sb.AppendLine($"{indent}{prop.Name}: {{ \"<interface property>\": static value | tag name object {{ \"type\": \"Tag\", \"Tag\": \"...\" }} }}");
                }
                else if (element != null)
                {
                    sb.AppendLine($"{indent}{prop.Name}: [ {ShortTypeName(element)} ]");
                    DescribeProperties(element, indent + "    ", sb, depth + 1);
                }
            }
            else if (typeof(IEngineeringObject).IsAssignableFrom(pt) && pt != typeof(IEngineeringObject))
            {
                sb.AppendLine($"{indent}{prop.Name}: {{ {ShortTypeName(pt)} }}");
                DescribeProperties(pt, indent + "    ", sb, depth + 1);
            }
        }
    }
}
