using System.Text.RegularExpressions;

namespace TiaMcp.Formats;

/// <summary>Fixes on a block's .s7dcl text.</summary>
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
}
