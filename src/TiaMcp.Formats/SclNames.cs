using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace TiaMcp.Formats;

/// <summary>Names as they appear in an SCL access path.</summary>
public static class SclNames
{
    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]*$");

    // SCL keywords and data type names: a member named like one needs quotes in an access path
    // (#Data."Date".Year), as in TIA Portal's own export.
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "AND", "ANY", "ARRAY", "AT", "BEGIN", "BOOL", "BY", "BYTE", "CASE", "CHAR", "CONST", "CONSTANT", "CONTINUE",
        "DATA_BLOCK", "DATE", "DATE_AND_TIME", "DINT", "DO", "DT", "DTL", "DWORD", "ELSE", "ELSIF", "END_CASE",
        "END_CONST", "END_DATA_BLOCK", "END_FOR", "END_FUNCTION", "END_FUNCTION_BLOCK", "END_IF",
        "END_ORGANIZATION_BLOCK", "END_REGION", "END_REPEAT", "END_STRUCT", "END_TYPE", "END_VAR", "END_WHILE",
        "EXIT", "FALSE", "FOR", "FUNCTION", "FUNCTION_BLOCK", "GOTO", "IF", "INT", "LDT", "LINT", "LREAL", "LTIME",
        "LTIME_OF_DAY", "LTOD", "LWORD", "MOD", "NOT", "NULL", "OF", "OR", "ORGANIZATION_BLOCK", "POINTER", "REAL",
        "REF_TO", "REGION", "REPEAT", "RETURN", "S5TIME", "SINT", "STRING", "STRUCT", "THEN", "TIME", "TIME_OF_DAY",
        "TO", "TOD", "TRUE", "TYPE", "UDINT", "UINT", "ULINT", "UNTIL", "USINT", "VAR", "VAR_IN_OUT", "VAR_INPUT",
        "VAR_OUTPUT", "VAR_TEMP", "VARIANT", "VOID", "WCHAR", "WHILE", "WORD", "WSTRING", "XOR",
    };

    // The name as one element of an access path: quoted unless it is a plain identifier that
    // isn't reserved ("M1-01" and "Date" are quoted, "Status" is not).
    public static string Element(string name) =>
        Identifier.IsMatch(name) && !Reserved.Contains(name) ? name : $"\"{name}\"";
}
