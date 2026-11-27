using System;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcp.Formats;

/// <summary>
/// The code of a classic WinCC VB script (Openness element Hmi.RuntimeScripting.VBScript) as a
/// .vbs file. Only the code is in the file: name, Sub/Function type and parameters stay in the
/// XML, so a write patches the code into a fresh export of the script.
/// </summary>
public static class VbScriptXml
{
    private const string ScriptElement = "Hmi.RuntimeScripting.VBScript";

    // The script's code with CRLF line breaks, as the .vbs file holds it.
    public static string ReadCode(string xml) => ToLineBreak(CodeElement(Parse(xml)).Value, "\r\n");

    // The XML with its code replaced by code. The XML's own line breaks are kept (Openness
    // exports them as LF), so an unchanged code gives the same XML.
    public static string WithCode(string xml, string code)
    {
        var doc = Parse(xml);
        var element = CodeElement(doc);
        var old = element.Value;
        element.Value = ToLineBreak(code, old.Contains("\r\n") ? "\r\n" : "\n");
        return doc.ToString();
    }

    private static XDocument Parse(string xml)
    {
        try { return XDocument.Parse(xml, LoadOptions.PreserveWhitespace); }
        catch (XmlException ex) { throw new FormatException($"the VB script XML can't be parsed: {ex.Message}", ex); }
    }

    private static XElement CodeElement(XDocument doc)
    {
        var script = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == ScriptElement)
            ?? throw new FormatException($"not a VB script export (no {ScriptElement} element under Document)");
        return script.Element("AttributeList")?.Element("Code")
            ?? throw new FormatException("the VB script XML has no AttributeList/Code element");
    }

    private static string ToLineBreak(string text, string lineBreak) =>
        text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", lineBreak);
}
