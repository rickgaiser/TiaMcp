using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TiaMcp.Formats;

/// <summary>Checks and fixes on a block's .s7dcl text.</summary>
public static class S7dclSource
{
    // The header's attribute list up to its first attribute.
    private static readonly Regex HeaderStart = new(@"\A(﻿?\s*\{)(\s*)");

    // Adds S7_BlockNumber to the header, unless it has one. TIA Portal leaves it out for an
    // automatically numbered block, and an import without it numbers the block automatically.
    public static string WithBlockNumber(string s7dcl, int number)
    {
        if (Regex.IsMatch(s7dcl, @"\bS7_BlockNumber\s*:=")) return s7dcl;
        var m = HeaderStart.Match(s7dcl);
        if (!m.Success) return s7dcl;
        var nl = s7dcl.Contains("\r\n") ? "\r\n" : "\n";
        var rest = s7dcl.Substring(m.Length);
        var attribute = $"S7_BlockNumber := \"{number}\"";
        return rest.StartsWith("}")
            ? m.Groups[1].Value + nl + "    " + attribute + nl + rest
            : m.Groups[1].Value + nl + "    " + attribute + ";" + m.Groups[2].Value + rest;
    }

    // The AT overlays of a block's interface (member name, the member it overlays) in its raw XML
    // export: <Member Name="m"><AttributeList><StringAttribute Name="At">target</...>.
    public static IReadOnlyList<(string Member, string Target)> AtOverlays(string xml) =>
        XDocument.Parse(xml).Descendants().Where(e => e.Name.LocalName == "Member")
            .Select(m => (Member: (string?)m.Attribute("Name"), Target: m.Elements().Where(e => e.Name.LocalName == "AttributeList").Elements()
                .FirstOrDefault(a => a.Name.LocalName == "StringAttribute" && (string?)a.Attribute("Name") == "At")?.Value))
            .Where(o => o.Member != null && o.Target != null)
            .Select(o => (o.Member!, o.Target!))
            .ToList();

    private const string AtMarker = "// TiaMcp: AT ";
    private static readonly Regex AtMarked = new(@"^\s*(?<member>""[^""]*""|[A-Za-z_]\w*)\s*:[^\r\n]*?" + Regex.Escape(AtMarker) + @"(?<target>""[^""]*""|\S+)", RegexOptions.Multiline);

    // .s7dcl has no notation for AT, so an overlay reads as an ordinary member and the import makes
    // it one. Marks each overlay's declaration line with a comment (TIA Portal drops '//' comments
    // on import), so FindWriteProblems can refuse the .s7dcl. Only the declaration part, before the
    // first network or BEGIN, is searched.
    public static string WithAtMarkers(string s7dcl, IEnumerable<(string Member, string Target)> overlays)
    {
        var codeStart = Regex.Match(s7dcl, @"^\s*(\{[^{}]*\}\s*)?(NETWORK|BEGIN)\b", RegexOptions.Multiline);
        var end = codeStart.Success ? codeStart.Index : s7dcl.Length;
        foreach (var (member, target) in overlays)
        {
            var name = Regex.IsMatch(member, @"^[A-Za-z_]\w*$") ? $"(?:{Regex.Escape(member)}|\"{Regex.Escape(member)}\")" : $"\"{Regex.Escape(member)}\"";
            var line = new Regex(@"^[ \t]*" + name + @"[ \t]*:[^\r\n]*", RegexOptions.Multiline).Match(s7dcl, 0, end);
            if (!line.Success || line.Value.Contains(AtMarker)) continue;
            var marker = $" {AtMarker}\"{target}\" - an overlay; .s7dcl can't carry AT";
            s7dcl = s7dcl.Insert(line.Index + line.Length, marker);
            end += marker.Length;
        }
        return s7dcl;
    }

    // System FB types that TIA Portal's .s7dcl import (V21) takes a same-named project FB or UDT
    // for: _.TONR becomes TONR_TIME, _.CTU CTU_INT, _.R_TRIG the system R_TRIG - silently, a
    // declaration alone compiles.
    private static readonly string[] SystemTypeNames = { "TON", "TOF", "TP", "TONR", "CTU", "CTD", "CTUD", "R_TRIG", "F_TRIG" };

    private static readonly Regex ProjectTypeNamedLikeSystemType = new(
        @"^\s*(?<member>""[^""]*""|[A-Za-z_]\w*)\s*:\s*(?:Array\s*\[[^\]]*\]\s*of\s+)?(?<type>_\.""?(?<name>" + string.Join("|", SystemTypeNames) +
        @")""?|""(?<name>" + string.Join("|", SystemTypeNames) + @")"")(?=\s*[;:{])",
        RegexOptions.Multiline | RegexOptions.IgnoreCase);

    // A network: its attribute list (with S7_Language) and its body.
    private static readonly Regex Network = new(@"\{(?<attributes>[^{}]*)\}\s*NETWORK\b(?<body>.*?)\bEND_NETWORK\b", RegexOptions.Singleline);
    private static readonly Regex Language = new(@"S7_Language\s*:=\s*""(?<language>\w+)""");

    // What in a block's .s7dcl TIA Portal's .s7dcl import is known to get wrong, each with the
    // workaround: an empty list when nothing is. read_source_tree warns about these, and
    // write_source_tree refuses to write such a .s7dcl.
    public static IReadOnlyList<string> FindWriteProblems(string s7dcl)
    {
        var problems = new List<string>();

        foreach (Match m in ProjectTypeNamedLikeSystemType.Matches(s7dcl))
        {
            var name = m.Groups["name"].Value;
            problems.Add($"'{m.Groups["member"].Value}' is of the project type {m.Groups["type"].Value}, but TIA Portal's .s7dcl import silently takes the system type of that name instead " +
                         "(TONR becomes TONR_TIME, CTU CTU_INT). Workaround: write this block from its raw XML (read it in the xml format, edit the .xml and write that), or rename the project type.");
        }

        foreach (Match m in AtMarked.Matches(s7dcl))
            problems.Add($"'{m.Groups["member"].Value}' is an AT overlay of {m.Groups["target"].Value}, which .s7dcl can't carry: the import makes it an independent variable, " +
                         "and the block still compiles. Workaround: write this block from its raw XML (read it in the xml format, edit the .xml and write that), " +
                         "or change the code so it doesn't need the overlay (e.g. move the value with SCATTER/GATHER or a slice access such as #w.%X0) and remove the marker comment.");

        var number = 0;
        foreach (Match m in Network.Matches(s7dcl))
        {
            number++;
            var language = Language.Match(m.Groups["attributes"].Value).Groups["language"].Value;
            if (language is not ("LAD" or "FBD")) continue;
            var line = NestedIndexLine(m.Groups["body"].Value);
            if (line == null) continue;
            problems.Add($"network {number} ({language}) has an array access inside an index: '{line}'. TIA Portal's .s7dcl import makes the inner access one quoted name " +
                         "(\"DB_B\".\"arr[1]\".x), so the block no longer compiles. Workaround: copy the inner value to a temp variable first (e.g. an SCL network " +
                         "'#idx := \"DB_B\".structArr[1].idx;') and index with that ('\"DB_A\".arr[#idx]'), or write this block from its raw XML.");
        }
        return problems;
    }

    // The trimmed line of the first '[' inside another '[' (quoted names and strings skipped), or null.
    private static string? NestedIndexLine(string body)
    {
        var depth = 0;
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c is '"' or '\'')
            {
                var close = body.IndexOf(c, i + 1);
                if (close < 0) break;
                i = close;
            }
            else if (c == '[')
            {
                if (++depth == 2)
                {
                    var start = body.LastIndexOf('\n', i) + 1;
                    var end = body.IndexOf('\n', i);
                    return body.Substring(start, (end < 0 ? body.Length : end) - start).Trim();
                }
            }
            else if (c == ']' && depth > 0)
            {
                depth--;
            }
        }
        return null;
    }
}
