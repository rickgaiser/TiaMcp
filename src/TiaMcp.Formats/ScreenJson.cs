using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace TiaMcp.Formats;

/// <summary>
/// The JSON of a WinCC Unified screen (built from the Openness object model by
/// HmiScreenConverter): the screen's properties plus "items", one object per screen item with its
/// "name", "type" and properties. Writing a screen goes property by property through Openness, so
/// a write sends only what differs from the screen as last read (<see cref="ChangedSince"/>).
/// </summary>
public static class ScreenJson
{
    public const string ItemsKey = "items";
    public const string NameKey = "name";
    public const string TypeKey = "type";
    public const string ContainedTypeKey = "ContainedType";

    // The part of json that differs from baseJson: changed screen properties, and per item the
    // changed properties (whole values - a nested part or list is written as a whole). A new item,
    // or one whose type or ContainedType changed (it gets recreated), is kept whole. Keys missing
    // from json are left out, as a write never resets them anyway. Always has an "items" array
    // when json has one, so the caller can still delete items that json no longer has.
    //
    // defaultsOf: a fresh object's values for the screen (no "type") or an item, if known. A read
    // leaves out what equals them, so a value the read left out that json sets to its default
    // (e.g. a generated faceplate's "Alarm": false) is no change.
    public static JsonObject ChangedSince(JsonObject json, JsonObject baseJson, Func<JsonObject, JsonObject?>? defaultsOf = null)
    {
        var patch = new JsonObject();
        var screenDefaults = defaultsOf?.Invoke(json);
        foreach (var kv in json)
        {
            if (kv.Key == ItemsKey) continue;
            if (!Same(kv.Value, baseJson.TryGetPropertyValue(kv.Key, out var old), old, screenDefaults?[kv.Key]))
                patch[kv.Key] = kv.Value?.DeepClone();
        }
        if (json[ItemsKey] is not JsonArray items) return patch;

        var baseItems = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        if (baseJson[ItemsKey] is JsonArray oldItems)
        {
            foreach (var node in oldItems)
                if (node is JsonObject o && o[NameKey]?.ToString() is { } n && !baseItems.ContainsKey(n)) baseItems[n] = o;
        }

        var changed = new JsonArray();
        foreach (var node in items)
        {
            if (node is not JsonObject item || item[NameKey]?.ToString() is not { } name || !baseItems.TryGetValue(name, out var old))
            {
                changed.Add(node?.DeepClone());
                continue;
            }
            if (JsonNode.DeepEquals(item, old)) continue;
            var defaults = defaultsOf?.Invoke(item);
            if (!JsonNode.DeepEquals(item[TypeKey], old[TypeKey]) || !JsonNode.DeepEquals(item[ContainedTypeKey], old[ContainedTypeKey]))
            {
                changed.Add(item.DeepClone());
                continue;
            }

            var itemPatch = new JsonObject { [NameKey] = name };
            foreach (var kv in item)
            {
                if (kv.Key == NameKey) continue;
                if (!Same(kv.Value, old.TryGetPropertyValue(kv.Key, out var oldValue), oldValue, defaults?[kv.Key]))
                    itemPatch[kv.Key] = kv.Value?.DeepClone();
            }
            // Only keys removed: nothing to write.
            if (itemPatch.Count > 1) changed.Add(itemPatch);
        }
        patch[ItemsKey] = changed;
        return patch;
    }

    // Whether value is what was read: equal to old, or - where the read left a key out (inRead
    // false) - equal to the default it was left out for, also within nested objects.
    private static bool Same(JsonNode? value, bool inRead, JsonNode? old, JsonNode? def)
    {
        if (!inRead) return IsDefault(value, def);
        if (JsonNode.DeepEquals(value, old)) return true;
        if (value is not JsonObject vo || old is not JsonObject oo) return false;
        foreach (var kv in oo)
            if (!vo.ContainsKey(kv.Key)) return false;
        var defaults = def as JsonObject;
        foreach (var kv in vo)
            if (!Same(kv.Value, oo.TryGetPropertyValue(kv.Key, out var o), o, defaults?[kv.Key])) return false;
        return true;
    }

    // As HmiScreenConverter.Prune leaves a value out: equal to its default, or an object whose
    // every key is.
    private static bool IsDefault(JsonNode? value, JsonNode? def)
    {
        if (def == null) return false;
        if (JsonNode.DeepEquals(value, def)) return true;
        if (value is not JsonObject vo || def is not JsonObject d) return false;
        foreach (var kv in vo)
            if (!d.ContainsKey(kv.Key) || !IsDefault(kv.Value, d[kv.Key])) return false;
        return true;
    }
}
