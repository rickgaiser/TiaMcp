using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TiaMcp.Formats;

// What a write changes in one WinCC Unified text list: entries as "From[..To]: texts".
public sealed class TextListChange
{
    public TextListChange(string list, bool isNew) { List = list; IsNew = isNew; }
    public string List { get; }
    public bool IsNew { get; }
    public List<string> Added { get; } = new();
    public List<string> Changed { get; } = new();
    public List<string> Deleted { get; } = new();
    public bool Any => IsNew || Added.Count + Changed.Count + Deleted.Count > 0;
}

public sealed record HmiTextListPatch(string ListsYaml, string LibraryYaml, IReadOnlyList<TextListChange> Changes);

/// <summary>
/// WinCC Unified text lists as TIA Portal exports them (HmiTextListComposition.Export): two YAML
/// files per HMI. "&lt;name&gt;.hmi.yml" holds the lists (TextListContainers / &lt;container&gt; /
/// ResourceLists / &lt;list&gt; / Entries / &lt;entry&gt;: Value, FromValue, ToValue, Text), where Text
/// is a reference "&lt;library&gt;.&lt;key&gt;" into "&lt;name&gt;.TextLibrary.hmi.yml" (TextLibraries /
/// &lt;library&gt;: Languages and Entries / &lt;key&gt; / Text: one text per language). In the source tree
/// a list is the same CSV as a PLC alarm text list (see AlarmTextListCsv), so the shape
/// PlcAlarmTextList is reused.
/// </summary>
public static class HmiTextListYaml
{
    private sealed class Entry
    {
        public Entry(YMap node, string from, string to, YMap? text) { Node = node; From = from; To = to; Text = text; }
        public YMap Node { get; }
        public string From { get; }
        public string To { get; }
        // The library entry the Text reference points at, null without one.
        public YMap? Text { get; }
    }

    private sealed class Model
    {
        public Model(SimpleYaml.Document lists, SimpleYaml.Document library, YMap libraryNode, string libraryName, IReadOnlyList<string> languages)
        {
            Lists = lists; Library = library; LibraryNode = libraryNode; LibraryName = libraryName; Languages = languages;
        }
        public SimpleYaml.Document Lists { get; }
        public SimpleYaml.Document Library { get; }
        public YMap LibraryNode { get; }
        public string LibraryName { get; }
        public IReadOnlyList<string> Languages { get; }

        public YMap LibraryEntries => LibraryNode.Get("Entries") as YMap ?? throw new FormatException("the text library has no Entries");

        // Every text list container, with its lists by name.
        public IEnumerable<YMap> Containers =>
            (Lists.Root.Get("TextListContainers") as YMap)?.Entries.Select(e => e.Value).OfType<YMap>()
                .Where(c => (c.Get("ResourceListType") as YScalar)?.Value is null or "TextList") ?? Enumerable.Empty<YMap>();

        public IEnumerable<KeyValuePair<YScalar, YNode>> AllLists =>
            Containers.SelectMany(c => (c.Get("ResourceLists") as YMap)?.Entries ?? Enumerable.Empty<KeyValuePair<YScalar, YNode>>());

        public YMap? FindList(string name) => AllLists.FirstOrDefault(l => l.Key.Value == name).Value as YMap;

        public List<Entry> EntriesOf(YMap list)
        {
            var result = new List<Entry>();
            if (list.Get("Entries") is not YMap entries) return result;
            foreach (var e in entries.Entries)
            {
                if (e.Value is not YMap node) throw new FormatException($"entry '{e.Key.Value}' is not a mapping");
                // TIA Portal leaves a value of 0 out.
                var from = Scalar(node, "FromValue") ?? "0";
                var to = Scalar(node, "ToValue") ?? "0";
                result.Add(new Entry(node, from, to, ResolveText(Scalar(node, "Text"))));
            }
            return result;
        }

        private YMap? ResolveText(string? reference)
        {
            if (string.IsNullOrEmpty(reference)) return null;
            var dot = reference!.LastIndexOf('.');
            if (dot <= 0 || reference.Substring(0, dot) != LibraryName) throw new FormatException($"text reference '{reference}' is not in library '{LibraryName}'");
            return LibraryEntries.Get(reference.Substring(dot + 1)) as YMap ?? throw new FormatException($"text '{reference}' is not in the text library");
        }

        public string[] TextsOf(YMap? text)
        {
            var texts = new string[Languages.Count];
            var seq = text?.Get("Text") as YSeq;
            for (var i = 0; i < texts.Length; i++)
                texts[i] = seq != null && i < seq.Items.Count && seq.Items[i] is YScalar s ? s.Value : "";
            return texts;
        }
    }

    private static string? Scalar(YMap map, string key) => (map.Get(key) as YScalar)?.Value;

    private static Model Load(string listsYaml, string libraryYaml)
    {
        var lists = SimpleYaml.Parse(listsYaml);
        var library = SimpleYaml.Parse(libraryYaml);
        var libraries = (library.Root.Get("TextLibraries") as YMap)?.Entries ?? new List<KeyValuePair<YScalar, YNode>>();
        if (libraries.Count != 1) throw new FormatException($"expected one text library in the export, found {libraries.Count}");
        var libraryNode = libraries[0].Value as YMap ?? throw new FormatException("the text library is not a mapping");
        var languages = (libraryNode.Get("Languages") as YSeq)?.Items.OfType<YScalar>().Select(s => s.Value).ToList()
            ?? throw new FormatException("the text library has no Languages");
        return new Model(lists, library, libraryNode, libraries[0].Key.Value, languages);
    }

    /// <summary>Every list of an export, with one text per project language. FormatException on input it doesn't know.</summary>
    public static IReadOnlyList<PlcAlarmTextList> Read(string listsYaml, string libraryYaml)
    {
        var model = Load(listsYaml, libraryYaml);
        return model.AllLists.Where(l => l.Value is YMap).Select(l => new PlcAlarmTextList(l.Key.Value, model.Languages,
            model.EntriesOf((YMap)l.Value).Select(e => new PlcAlarmTextEntry(e.From, e.To, model.TextsOf(e.Text))).ToList())).ToList();
    }

    /// <summary>
    /// The export with the given lists written into it, to import: an existing list's entries are
    /// matched by From/To and get the given texts (a language the list doesn't give keeps its text);
    /// with deleteMissing, entries not given are removed. A list not in the export is added. Every
    /// other list and text stays as exported. Throws FormatException when the export isn't in the
    /// form this converter writes back unchanged, or a list doesn't fit (unknown language, no
    /// container to add a list to) - so nothing is imported that TIA Portal didn't write itself.
    /// </summary>
    public static HmiTextListPatch Patch(string listsYaml, string libraryYaml, IReadOnlyList<PlcAlarmTextList> lists, bool deleteMissing)
    {
        CheckRoundTrip(listsYaml, "text list file");
        CheckRoundTrip(libraryYaml, "text library file");
        var model = Load(listsYaml, libraryYaml);
        foreach (var list in lists)
        {
            var unknown = list.Languages.FirstOrDefault(l => !model.Languages.Contains(l));
            if (unknown != null) throw new FormatException($"list '{list.Name}': language '{unknown}' is not a project language ({string.Join(", ", model.Languages)})");
        }

        var nextKey = model.LibraryEntries.Entries.Select(e => Regex.Match(e.Key.Value, @"^Text_(\d+)$"))
            .Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(-1).Max() + 1;
        var changes = new List<TextListChange>();
        foreach (var list in lists)
        {
            var node = model.FindList(list.Name);
            var change = new TextListChange(list.Name, node == null);
            if (node == null)
            {
                var container = model.Containers.FirstOrDefault(c => c.Get("ResourceLists") is YMap)
                    ?? throw new FormatException($"list '{list.Name}': the HMI has no text list yet to add it next to - create one in TIA Portal first");
                node = new YMap();
                ((YMap)container.Get("ResourceLists")!).Entries.Add(new KeyValuePair<YScalar, YNode>(new YScalar(list.Name), node));
            }

            var existing = model.EntriesOf(node);
            var result = new List<YMap>();
            var used = new HashSet<Entry>();
            foreach (var given in list.Entries)
            {
                var match = existing.FirstOrDefault(e => e.From == given.From && e.To == given.To && !used.Contains(e));
                if (match != null)
                {
                    used.Add(match);
                    var current = model.TextsOf(match.Text);
                    var texts = (string[])current.Clone();
                    for (var i = 0; i < list.Languages.Count; i++)
                        texts[IndexOf(model.Languages, list.Languages[i])] = i < given.Texts.Count ? given.Texts[i] : "";
                    var diffs = model.Languages.Select((l, i) => (l, i)).Where(x => current[x.i] != texts[x.i])
                        .Select(x => $"[{x.l}] '{current[x.i]}' -> '{texts[x.i]}'").ToList();
                    if (diffs.Count > 0)
                    {
                        var text = match.Text ?? NewText(model, match.Node, ref nextKey);
                        SetTexts(text, texts);
                        change.Changed.Add($"{Range(given.From, given.To)}: {string.Join("; ", diffs)}");
                    }
                    result.Add(match.Node);
                }
                else
                {
                    // As TIA Portal writes an entry: a range is "Type: Range", Value is From, and a
                    // value of 0 is left out. Without the Type, TIA Portal drops a range entry.
                    var entry = new YMap();
                    if (given.From != given.To) entry.Set("Type", new YScalar("Range"));
                    if (given.From != "0") entry.Set("Value", new YScalar(given.From));
                    if (given.From != "0") entry.Set("FromValue", new YScalar(given.From));
                    if (given.To != "0") entry.Set("ToValue", new YScalar(given.To));
                    var texts = new string[model.Languages.Count];
                    for (var i = 0; i < texts.Length; i++) texts[i] = "";
                    for (var i = 0; i < list.Languages.Count; i++)
                        texts[IndexOf(model.Languages, list.Languages[i])] = i < given.Texts.Count ? given.Texts[i] : "";
                    SetTexts(NewText(model, entry, ref nextKey), texts);
                    change.Added.Add($"{Range(given.From, given.To)}: {string.Join(" | ", model.Languages.Select((l, i) => $"[{l}] {texts[i]}"))}");
                    result.Add(entry);
                }
            }
            foreach (var old in existing.Where(e => !used.Contains(e)))
            {
                if (!deleteMissing) { result.Add(old.Node); continue; }
                change.Deleted.Add($"{Range(old.From, old.To)}: {model.TextsOf(old.Text).FirstOrDefault()}");
                RemoveText(model, old);
            }

            // Unchanged lists keep their entry names; a written one is numbered as TIA Portal numbers them.
            if (change.Any)
            {
                var entries = new YMap();
                for (var i = 0; i < result.Count; i++) entries.Entries.Add(new KeyValuePair<YScalar, YNode>(new YScalar($"Text_list_entry_{i}"), result[i]));
                node.Set("Entries", entries);
            }
            changes.Add(change);
        }
        return new HmiTextListPatch(SimpleYaml.Emit(model.Lists), SimpleYaml.Emit(model.Library), changes);
    }

    private static int IndexOf(IReadOnlyList<string> languages, string language)
    {
        for (var i = 0; i < languages.Count; i++) if (languages[i] == language) return i;
        return -1;
    }

    // A new library entry for a list entry, referenced from its Text.
    private static YMap NewText(Model model, YMap entry, ref int nextKey)
    {
        var key = $"Text_{nextKey++}";
        var text = new YMap();
        model.LibraryEntries.Entries.Add(new KeyValuePair<YScalar, YNode>(new YScalar(key), text));
        entry.Set("Text", new YScalar($"{model.LibraryName}.{key}"));
        return text;
    }

    private static void SetTexts(YMap text, string[] texts)
    {
        var seq = text.Get("Text") as YSeq;
        var old = seq?.Items.OfType<YScalar>().ToList() ?? new List<YScalar>();
        var updated = new YSeq();
        for (var i = 0; i < texts.Length; i++)
            updated.Items.Add(i < old.Count && old[i].Value == texts[i] ? old[i] : new YScalar(texts[i]));
        text.Set("Text", updated);
    }

    // Drops a deleted entry's library text, unless another entry still refers to it.
    private static void RemoveText(Model model, Entry entry)
    {
        if (entry.Text == null) return;
        var reference = Scalar(entry.Node, "Text");
        var stillUsed = model.AllLists.Where(l => l.Value is YMap).SelectMany(l => model.EntriesOf((YMap)l.Value))
            .Count(e => Scalar(e.Node, "Text") == reference) > 1;
        if (!stillUsed) model.LibraryEntries.Entries.RemoveAll(e => ReferenceEquals(e.Value, entry.Text));
    }

    private static string Range(string from, string to) => from == to ? from : $"{from}..{to}";

    private static void CheckRoundTrip(string yaml, string what)
    {
        var text = yaml.Length > 0 && yaml[0] == '﻿' ? yaml.Substring(1) : yaml;
        var emitted = SimpleYaml.Emit(SimpleYaml.Parse(text));
        if (emitted == text) return;
        var a = text.Replace("\r\n", "\n").Split('\n');
        var b = emitted.Replace("\r\n", "\n").Split('\n');
        var line = Enumerable.Range(0, Math.Min(a.Length, b.Length)).FirstOrDefault(i => a[i] != b[i]);
        throw new FormatException($"TIA Portal's {what} has a form this server can't write back unchanged (line {line + 1}: '{(line < a.Length ? a[line] : "")}'); nothing was written");
    }
}
