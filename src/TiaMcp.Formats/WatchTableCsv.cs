using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace TiaMcp.Formats;

// One row of a PLC watch or force table. A comment row (IsComment) has only its Comments: text
// rows ("// text" in TIA Portal) and blank separator rows are both comment rows. A tag row has the
// symbol or address as typed (Name), and Value is the modify value of a watch table, the force
// value of a force table. Null optional fields: TIA Portal's default.
public sealed record WatchTableRow(bool IsComment, string Name, string? DisplayFormat, string? Value,
    string? MonitorTrigger, string? ModifyTrigger, IReadOnlyList<string> Comments);

// Languages: the cultures of the Comment columns, in column order; each row has one comment per language.
public sealed record WatchTable(string Name, bool IsForceTable, IReadOnlyList<string> Languages, IReadOnlyList<WatchTableRow> Rows);

/// <summary>
/// A PLC watch table (or the force table) as CSV, made from and turned back into its Openness XML
/// export. Columns: Name, DisplayFormat, ModifyValue (ForceValue in a force table),
/// MonitorTrigger, ModifyTrigger (watch tables only), and 'Comment [culture]' per language.
/// A comment row has '//' as Name and its text in the Comment columns; a blank row is all empty.
/// An empty trigger is 'Permanent'; an empty DisplayFormat lets TIA Portal choose one by data type.
/// The modify checkbox isn't part of the export, and Openness can't set it.
/// </summary>
public static class WatchTableCsv
{
    private const string Ns = "SW.WatchAndForceTables.";
    private const string WatchTableElement = Ns + "PlcWatchTable";
    private const string ForceTableElement = Ns + "PlcForceTable";
    private const string CommentEntryElement = Ns + "PlcTableCommentEntry";
    private const string WatchEntryElement = Ns + "PlcWatchTableEntry";
    private const string ForceEntryElement = Ns + "PlcForceTableEntry";

    public const string CommentMark = "//";
    public const string DefaultTrigger = "Permanent";

    // PlcWatchAndForceTableDisplayFormat and PlcWatchAndForceTablePreDefinedTrigger (V21), so a bad
    // value is refused with its CSV line before anything is imported.
    public static readonly IReadOnlyList<string> DisplayFormats = new[]
    {
        "Any_pointer", "BCD", "Bin", "Bool", "Character", "Character_sequence", "Date", "DATE_AND_TIME",
        "DEC_sequence", "DEC_signed", "DEC_unsigned", "Hex", "Block_number", "Octal", "Pointer", "Float",
        "Counter", "SIMATIC_Time", "String", "Time", "TIME_OF_DAY", "Unicode_character",
        "Unicode_character_sequence", "Unicode_string", "Symbolic",
    };

    public static readonly IReadOnlyList<string> Triggers = new[]
    {
        "Permanent", "PermanentAtStart", "OnceOnlyAtStart", "PermanentAtEnd", "OnceOnlyAtEnd", "PermanentAtStop", "OnceOnlyAtStop",
    };

    private static string ValueColumn(bool force) => force ? "ForceValue" : "ModifyValue";

    private static string[] Headers(bool force, IReadOnlyList<string> languages) =>
        (force
            ? new[] { "Name", "DisplayFormat", "ForceValue", "MonitorTrigger" }
            : new[] { "Name", "DisplayFormat", "ModifyValue", "MonitorTrigger", "ModifyTrigger" })
        .Concat(languages.Select(l => $"Comment [{l}]")).ToArray();

    public static string Format(WatchTable table) =>
        Csv.Format(Headers(table.IsForceTable, table.Languages), table.Rows.Select(r =>
        {
            var cells = new List<string>();
            if (r.IsComment)
            {
                cells.Add(r.Comments.Any(c => c.Length > 0) ? CommentMark : "");
                cells.AddRange(Enumerable.Repeat("", table.IsForceTable ? 3 : 4));
            }
            else
            {
                cells.Add(r.Name);
                cells.Add(r.DisplayFormat ?? "");
                cells.Add(r.Value ?? "");
                cells.Add(ShownTrigger(r.MonitorTrigger));
                if (!table.IsForceTable) cells.Add(ShownTrigger(r.ModifyTrigger));
            }
            cells.AddRange(table.Languages.Select((_, i) => i < r.Comments.Count ? r.Comments[i] : ""));
            return cells.ToArray();
        }));

    private static string ShownTrigger(string? trigger) => trigger == null || trigger == DefaultTrigger ? "" : trigger;

    // Inverse of Format. A Name of '// text' with empty Comment columns is a comment row with that
    // text in the first language. Throws FormatException (with the CSV line) on bad input.
    public static WatchTable Parse(string name, string csv, bool isForceTable = false)
    {
        var rows = Csv.ParseRows(csv);
        if (rows.Count == 0) throw new FormatException("empty file");
        var header = rows[0];
        int Column(string h) => header.FindIndex(c => string.Equals(c.Trim(), h, StringComparison.OrdinalIgnoreCase));
        int cName = Column("Name"), cFormat = Column("DisplayFormat"), cValue = Column(ValueColumn(isForceTable)),
            cMonitor = Column("MonitorTrigger"), cModify = isForceTable ? -1 : Column("ModifyTrigger");
        if (cName < 0) throw new FormatException("column 'Name' missing");
        var languageColumns = header.Select((h, i) => (h: h.Trim(), i))
            .Where(x => x.h.StartsWith("Comment [", StringComparison.Ordinal) && x.h.EndsWith("]", StringComparison.Ordinal))
            .Select(x => (Language: x.h.Substring("Comment [".Length, x.h.Length - "Comment [".Length - 1), Index: x.i)).ToList();
        var languages = languageColumns.Select(c => c.Language).ToList();

        var result = new List<WatchTableRow>();
        for (var r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            var line = r + 1;
            string Cell(int i) => i >= 0 && i < row.Count ? row[i].Trim() : "";
            string? Optional(int i) => Cell(i).Length == 0 ? null : Cell(i);
            var comments = languageColumns.Select(c => Cell(c.Index)).ToList();
            var rowName = Cell(cName);

            if (rowName.Length == 0 || rowName.StartsWith(CommentMark, StringComparison.Ordinal))
            {
                if (Optional(cFormat) != null || Optional(cValue) != null || Optional(cMonitor) != null || Optional(cModify) != null)
                    throw new FormatException($"CSV line {line}: a comment row (Name empty or '{CommentMark}...') has no DisplayFormat, value or trigger.");
                var inline = rowName.Length > CommentMark.Length ? rowName.Substring(CommentMark.Length).Trim() : "";
                if (inline.Length > 0)
                {
                    if (comments.Any(c => c.Length > 0))
                        throw new FormatException($"CSV line {line}: comment text both after '{CommentMark}' and in a Comment column - use one of them.");
                    if (comments.Count == 0)
                        throw new FormatException($"CSV line {line}: comment text needs a 'Comment [culture]' column, e.g. 'Comment [en-US]'.");
                    comments[0] = inline;
                }
                result.Add(new WatchTableRow(true, "", null, null, null, null, comments));
                continue;
            }

            var format = Optional(cFormat);
            if (format != null)
            {
                var known = DisplayFormats.FirstOrDefault(f => string.Equals(f, format, StringComparison.OrdinalIgnoreCase));
                format = known ?? throw new FormatException($"CSV line {line}: DisplayFormat '{format}' is unknown; use one of {string.Join(", ", DisplayFormats)}, or leave it empty.");
            }
            result.Add(new WatchTableRow(false, rowName, format, Optional(cValue),
                Trigger(Optional(cMonitor), "MonitorTrigger", line), isForceTable ? null : Trigger(Optional(cModify), "ModifyTrigger", line), comments));
        }
        return new WatchTable(name, isForceTable, languages, result);
    }

    private static string? Trigger(string? value, string column, int line)
    {
        if (value == null) return null;
        return Triggers.FirstOrDefault(t => string.Equals(t, value, StringComparison.OrdinalIgnoreCase))
            ?? throw new FormatException($"CSV line {line}: {column} '{value}' is unknown; use one of {string.Join(", ", Triggers)}, or leave it empty (Permanent).");
    }

    // The table of an Openness export (with or without DocumentInfo). Languages are the cultures
    // in the order they first appear; rows without a text in a language get "".
    public static WatchTable FromXml(string xml)
    {
        var root = XDocument.Parse(xml).Root ?? throw new FormatException("empty XML");
        var table = root.Elements().FirstOrDefault(e => e.Name.LocalName is WatchTableElement or ForceTableElement)
            ?? throw new FormatException($"no {WatchTableElement} or {ForceTableElement} element under Document");
        var force = table.Name.LocalName == ForceTableElement;
        var name = (string?)table.Element("AttributeList")?.Element("Name") ?? throw new FormatException("table without Name");

        var entries = table.Elements("ObjectList").Elements().ToList();
        var languages = new List<string>();
        foreach (var culture in entries.SelectMany(CommentItems).Select(i => i.Culture))
            if (!languages.Contains(culture)) languages.Add(culture);

        var rows = entries.Select(e =>
        {
            var texts = CommentItems(e).GroupBy(i => i.Culture).ToDictionary(g => g.Key, g => g.First().Text);
            var comments = languages.Select(l => texts.TryGetValue(l, out var t) ? t : "").ToList();
            if (e.Name.LocalName == CommentEntryElement)
                return new WatchTableRow(true, "", null, null, null, null, comments);
            var attributes = e.Element("AttributeList");
            string? Attribute(string a) => (string?)attributes?.Element(a) is { Length: > 0 } v ? v : null;
            // A row without a symbol is exported with its Address ('%M10.3') and no Name.
            var rowName = Attribute("Name") ?? Attribute("Address")
                ?? throw new FormatException("a row has neither Name nor Address");
            return new WatchTableRow(false, rowName, Attribute("DisplayFormat"), Attribute(ValueColumn(force)),
                Attribute("MonitorTrigger"), force ? null : Attribute("ModifyTrigger"), comments);
        }).ToList();
        return new WatchTable(name, force, languages, rows);
    }

    private static IEnumerable<(string Culture, string Text)> CommentItems(XElement entry) =>
        entry.Elements("ObjectList").Elements("MultilingualText")
            .Where(t => (string?)t.Attribute("CompositionName") == "Comment")
            .Descendants("MultilingualTextItem")
            .Select(i => i.Element("AttributeList"))
            .Where(a => a != null)
            .Select(a => ((string?)a!.Element("Culture") ?? "", (string?)a.Element("Text") ?? ""))
            .Where(x => x.Item1.Length > 0);

    // The import XML of a table, as Openness exports it. withDisplayFormats false leaves every
    // DisplayFormat out: TIA Portal then imports a row with an unknown symbol without an error
    // (the table becomes inconsistent), where a DisplayFormat makes the whole import fail.
    public static string ToXml(WatchTable table, bool withDisplayFormats = true)
    {
        var id = 0;
        string NextId() => (id++).ToString("X", CultureInfo.InvariantCulture);

        XElement Comment(IReadOnlyList<string> comments) =>
            new("MultilingualText", new XAttribute("ID", NextId()), new XAttribute("CompositionName", "Comment"),
                new XElement("ObjectList", table.Languages.Select((l, i) =>
                    new XElement("MultilingualTextItem", new XAttribute("ID", NextId()), new XAttribute("CompositionName", "Items"),
                        new XElement("AttributeList",
                            new XElement("Culture", l),
                            new XElement("Text", i < comments.Count ? comments[i] : ""))))));

        var tableElement = new XElement(table.IsForceTable ? ForceTableElement : WatchTableElement, new XAttribute("ID", NextId()),
            new XElement("AttributeList", new XElement("Name", table.Name)));
        var objectList = new XElement("ObjectList");
        foreach (var row in table.Rows)
        {
            if (row.IsComment)
            {
                var entry = new XElement(CommentEntryElement, new XAttribute("ID", NextId()), new XAttribute("CompositionName", "Entries"));
                if (table.Languages.Count > 0) entry.Add(new XElement("ObjectList", Comment(row.Comments)));
                objectList.Add(entry);
                continue;
            }

            // In the export's (alphabetical) order.
            var attributes = new XElement("AttributeList");
            var isAddress = row.Name.StartsWith("%", StringComparison.Ordinal);
            if (isAddress) attributes.Add(new XElement("Address", row.Name));
            if (withDisplayFormats && row.DisplayFormat != null) attributes.Add(new XElement("DisplayFormat", row.DisplayFormat));
            if (table.IsForceTable) attributes.Add(new XElement("ForceValue", row.Value ?? ""));
            else attributes.Add(new XElement("ModifyTrigger", row.ModifyTrigger ?? DefaultTrigger), new XElement("ModifyValue", row.Value ?? ""));
            attributes.Add(new XElement("MonitorTrigger", row.MonitorTrigger ?? DefaultTrigger));
            if (!isAddress) attributes.Add(new XElement("Name", row.Name));

            var tagEntry = new XElement(table.IsForceTable ? ForceEntryElement : WatchEntryElement,
                new XAttribute("ID", NextId()), new XAttribute("CompositionName", "Entries"), attributes);
            if (table.Languages.Count > 0) tagEntry.Add(new XElement("ObjectList", Comment(row.Comments)));
            objectList.Add(tagEntry);
        }
        if (objectList.HasElements) tableElement.Add(objectList);

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement("Document", new XElement("Engineering", new XAttribute("version", "V21")), tableElement));
        return doc.Declaration + Environment.NewLine + doc.Root;
    }

    // The same table with only the given tag rows (by index into Rows), and no comment rows -
    // for checking which rows TIA Portal can't resolve.
    public static WatchTable WithRows(WatchTable table, string name, IEnumerable<int> tagRows) =>
        table with { Name = name, Rows = tagRows.Select(i => table.Rows[i]).ToList() };
}
