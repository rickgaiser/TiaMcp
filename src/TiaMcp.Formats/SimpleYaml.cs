using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TiaMcp.Formats;

// A node of the YAML subset that TIA Portal's own exports use (see SimpleYaml).
public abstract class YNode { }

// Style: how the scalar is written - ' ' plain, '\'' single-quoted, '"' double-quoted. A scalar
// read from a file keeps its style, so an unchanged file is written back byte for byte.
public sealed class YScalar : YNode
{
    public YScalar(string value, char style) { Value = value; Style = style; }

    // A new scalar, in the style TIA Portal's exporter would pick for it.
    public YScalar(string value) : this(value, SimpleYaml.StyleFor(value)) { }

    public string Value { get; }
    public char Style { get; }
}

public sealed class YMap : YNode
{
    public List<KeyValuePair<YScalar, YNode>> Entries { get; } = new();

    // Written as "{}": only an empty map read that way.
    public bool Flow { get; set; }

    public YNode? Get(string key) => Entries.FirstOrDefault(e => e.Key.Value == key).Value;

    public void Set(string key, YNode value)
    {
        var index = Entries.FindIndex(e => e.Key.Value == key);
        if (index >= 0) Entries[index] = new KeyValuePair<YScalar, YNode>(Entries[index].Key, value);
        else Entries.Add(new KeyValuePair<YScalar, YNode>(new YScalar(key), value));
    }
}

public sealed class YSeq : YNode
{
    public List<YNode> Items { get; } = new();
    public bool Flow { get; set; }
}

/// <summary>
/// The block-style YAML subset of TIA Portal's own exports (WinCC Unified text lists): nested
/// mappings, sequences of scalars, plain/single/double-quoted one-line scalars, comment lines
/// before the first key. Anything else (block scalars, anchors, flow collections that aren't
/// empty, mappings inside sequences) is a FormatException rather than a guess. Emit(Parse(x))
/// gives x back for what TIA writes, which callers check before they import a changed file.
/// </summary>
public static class SimpleYaml
{
    public sealed class Document
    {
        public Document(string header, YMap root, string newLine) { Header = header; Root = root; NewLine = newLine; }

        // The comment and blank lines before the first key, newlines included.
        public string Header { get; }
        public YMap Root { get; }
        public string NewLine { get; }
    }

    private sealed class Line
    {
        public Line(int number, int indent, string text) { Number = number; Indent = indent; Text = text; }
        public int Number { get; }
        public int Indent { get; }
        public string Text { get; }
    }

    public static Document Parse(string text)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);
        var newLine = text.Contains("\r\n") ? "\r\n" : "\n";
        var raw = text.Replace("\r\n", "\n").Split('\n');
        var header = new StringBuilder();
        var i = 0;
        for (; i < raw.Length; i++)
        {
            var t = raw[i].Trim();
            if (t.Length > 0 && !t.StartsWith("#")) break;
            if (i == raw.Length - 1 && raw[i].Length == 0) break;
            header.Append(raw[i]).Append(newLine);
        }

        var lines = new List<Line>();
        for (; i < raw.Length; i++)
        {
            var line = raw[i];
            if (line.Trim().Length == 0) continue;
            if (line.Contains('\t') && line.TrimStart(' ').StartsWith("\t")) throw new FormatException($"line {i + 1}: tab in indentation");
            var indent = line.Length - line.TrimStart(' ').Length;
            var content = line.Substring(indent).TrimEnd();
            if (content.StartsWith("#")) throw new FormatException($"line {i + 1}: comment lines are only expected at the top");
            lines.Add(new Line(i + 1, indent, content));
        }

        var pos = 0;
        var root = new YMap();
        if (lines.Count > 0)
        {
            if (lines[0].Indent != 0) throw new FormatException($"line {lines[0].Number}: the document must start at column 1");
            if (IsSeqItem(lines[0].Text)) throw new FormatException($"line {lines[0].Number}: a sequence at the top level is not supported");
            root = ParseMap(lines, ref pos, 0);
            if (pos < lines.Count) throw new FormatException($"line {lines[pos].Number}: unexpected indentation");
        }
        return new Document(header.ToString(), root, newLine);
    }

    private static bool IsSeqItem(string text) => text == "-" || text.StartsWith("- ");

    private static YMap ParseMap(List<Line> lines, ref int pos, int indent)
    {
        var map = new YMap();
        while (pos < lines.Count && lines[pos].Indent == indent && !IsSeqItem(lines[pos].Text))
        {
            var line = lines[pos];
            var (key, rest) = SplitKey(line);
            pos++;
            YNode value;
            if (rest.Length > 0)
            {
                value = rest == "{}" ? new YMap { Flow = true } : rest == "[]" ? new YSeq { Flow = true } : ParseScalar(rest, line.Number);
            }
            else if (pos < lines.Count && lines[pos].Indent > indent)
            {
                value = IsSeqItem(lines[pos].Text) ? ParseSeq(lines, ref pos, lines[pos].Indent) : ParseMap(lines, ref pos, lines[pos].Indent);
            }
            else if (pos < lines.Count && lines[pos].Indent == indent && IsSeqItem(lines[pos].Text))
            {
                // "key:" with its items at the key's own indentation, as TIA Portal writes them.
                value = ParseSeq(lines, ref pos, indent);
            }
            else
            {
                value = new YScalar("", ' ');
            }
            if (map.Entries.Any(e => e.Key.Value == key.Value)) throw new FormatException($"line {line.Number}: key '{key.Value}' appears twice");
            map.Entries.Add(new KeyValuePair<YScalar, YNode>(key, value));
        }
        if (pos < lines.Count && lines[pos].Indent > indent) throw new FormatException($"line {lines[pos].Number}: unexpected indentation");
        return map;
    }

    private static YSeq ParseSeq(List<Line> lines, ref int pos, int indent)
    {
        var seq = new YSeq();
        while (pos < lines.Count && lines[pos].Indent == indent && IsSeqItem(lines[pos].Text))
        {
            var line = lines[pos];
            var rest = line.Text.Length > 1 ? line.Text.Substring(2).Trim() : "";
            pos++;
            if (rest.Length == 0) throw new FormatException($"line {line.Number}: a nested block in a sequence is not supported");
            if (rest[0] != '\'' && rest[0] != '"' && (rest.Contains(": ") || rest.EndsWith(":")))
                throw new FormatException($"line {line.Number}: a mapping in a sequence is not supported");
            seq.Items.Add(ParseScalar(rest, line.Number));
        }
        return seq;
    }

    private static (YScalar Key, string Value) SplitKey(Line line)
    {
        var text = line.Text;
        if (text[0] == '\'' || text[0] == '"')
        {
            var (key, end) = ParseQuoted(text, 0, line.Number);
            var after = text.Substring(end).TrimStart();
            if (!after.StartsWith(":")) throw new FormatException($"line {line.Number}: ':' expected after the key");
            return (key, after.Substring(1).Trim());
        }
        var colon = text.IndexOf(": ", StringComparison.Ordinal);
        if (colon < 0 && text.EndsWith(":")) colon = text.Length - 1;
        if (colon <= 0) throw new FormatException($"line {line.Number}: 'key: value' expected, got '{text}'");
        return (new YScalar(text.Substring(0, colon), ' '), text.Substring(colon + 1).Trim());
    }

    private static YScalar ParseScalar(string text, int lineNumber)
    {
        if (text[0] == '\'' || text[0] == '"')
        {
            var (scalar, end) = ParseQuoted(text, 0, lineNumber);
            if (text.Substring(end).Trim().Length > 0) throw new FormatException($"line {lineNumber}: unexpected text after the quoted value");
            return scalar;
        }
        if ("|>&*!{[%@`".IndexOf(text[0]) >= 0) throw new FormatException($"line {lineNumber}: '{text}' uses YAML this server doesn't read");
        if (text.Contains(" #")) throw new FormatException($"line {lineNumber}: a comment after a value is not supported");
        return new YScalar(text, ' ');
    }

    private static (YScalar Scalar, int End) ParseQuoted(string text, int start, int lineNumber)
    {
        var quote = text[start];
        var sb = new StringBuilder();
        for (var i = start + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (quote == '\'')
            {
                if (c != '\'') { sb.Append(c); continue; }
                if (i + 1 < text.Length && text[i + 1] == '\'') { sb.Append('\''); i++; continue; }
                return (new YScalar(sb.ToString(), '\''), i + 1);
            }
            if (c == '"') return (new YScalar(sb.ToString(), '"'), i + 1);
            if (c != '\\') { sb.Append(c); continue; }
            if (++i >= text.Length) break;
            switch (text[i])
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case '0': sb.Append('\0'); break;
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case ' ': sb.Append(' '); break;
                case 'x': sb.Append(Hex(text, ref i, 2, lineNumber)); break;
                case 'u': sb.Append(Hex(text, ref i, 4, lineNumber)); break;
                case 'U': sb.Append(Hex(text, ref i, 8, lineNumber)); break;
                default: throw new FormatException($"line {lineNumber}: unknown escape '\\{text[i]}'");
            }
        }
        throw new FormatException($"line {lineNumber}: quoted value not closed on its line (multi-line values are not supported)");
    }

    private static string Hex(string text, ref int i, int digits, int lineNumber)
    {
        if (i + digits >= text.Length) throw new FormatException($"line {lineNumber}: short escape");
        var code = int.Parse(text.Substring(i + 1, digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        i += digits;
        return char.ConvertFromUtf32(code);
    }

    public static string Emit(Document document)
    {
        var sb = new StringBuilder(document.Header);
        EmitMap(sb, document.Root, 0, document.NewLine);
        return sb.ToString();
    }

    private static void EmitMap(StringBuilder sb, YMap map, int indent, string nl)
    {
        foreach (var entry in map.Entries)
        {
            sb.Append(' ', indent).Append(Format(entry.Key)).Append(':');
            switch (entry.Value)
            {
                case YScalar s when s.Style == ' ' && s.Value.Length == 0:
                    sb.Append(nl);
                    break;
                case YScalar s:
                    sb.Append(' ').Append(Format(s)).Append(nl);
                    break;
                case YMap m when m.Entries.Count == 0 && m.Flow:
                    sb.Append(" {}").Append(nl);
                    break;
                case YSeq q when q.Items.Count == 0 && q.Flow:
                    sb.Append(" []").Append(nl);
                    break;
                case YMap m:
                    sb.Append(nl);
                    EmitMap(sb, m, indent + 2, nl);
                    break;
                case YSeq q:
                    sb.Append(nl);
                    foreach (var item in q.Items)
                    {
                        if (item is not YScalar scalar) throw new InvalidOperationException("only scalars in a sequence");
                        sb.Append(' ', indent).Append("- ").Append(Format(scalar)).Append(nl);
                    }
                    break;
            }
        }
    }

    private static string Format(YScalar scalar) => scalar.Style switch
    {
        '\'' => "'" + scalar.Value.Replace("'", "''") + "'",
        '"' => "\"" + EscapeDouble(scalar.Value) + "\"",
        _ => scalar.Value,
    };

    private static string EscapeDouble(string value)
    {
        var sb = new StringBuilder();
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ') sb.Append("\\x").Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private static readonly Regex Reserved = new(@"^(?:~|null|true|false|yes|no|on|off|y|n)$", RegexOptions.IgnoreCase);

    // Plain where that reads back as the same string, double-quoted for control characters,
    // single-quoted otherwise (as for an empty text, which TIA Portal writes as '').
    public static char StyleFor(string value)
    {
        if (value.Any(c => c < ' ')) return '"';
        if (value.Length == 0 || value.Trim() != value || Reserved.IsMatch(value)) return '\'';
        if ("-?:,[]{}#&*!|>'\"%@`".IndexOf(value[0]) >= 0) return '\'';
        if (value.Contains(": ") || value.Contains(" #") || value.EndsWith(":")) return '\'';
        return ' ';
    }
}
