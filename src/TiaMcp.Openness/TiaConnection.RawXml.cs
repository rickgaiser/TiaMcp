using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Siemens.Engineering;

namespace TiaMcp.Openness;

/// <summary>
/// Writing blocks, UDTs and PLC tag tables from their raw SimaticML export (the "xml" format), as
/// read_source_tree reads it. An import with ImportOptions.Override replaces the whole object, or
/// creates it when it doesn't exist yet.
/// </summary>
public sealed partial class TiaConnection
{
    public ImportResult ImportBlockXml(string plcName, string groupPath, string blockName, string xml)
    {
        EnsureConnected();
        var group = ResolveGroup(GetSoftware(plcName), groupPath);
        return group == null
            ? NotFound(groupPath)
            : Imported(plcName, blockName, ImportXml(xml, "SW.Blocks.", "a block", blockName, file => group.Value.Blocks.Import(file, ImportOptions.Override)));
    }

    public ImportResult ImportUdtXml(string plcName, string groupPath, string typeName, string xml)
    {
        EnsureConnected();
        var group = ResolveTypeGroup(GetSoftware(plcName), groupPath);
        return group == null
            ? NotFound(groupPath)
            : ImportXml(xml, "SW.Types.PlcStruct", "a UDT", typeName, file => group.Value.Types.Import(file, ImportOptions.Override));
    }

    public ImportResult ImportTagTableXml(string plcName, string groupPath, string tableName, string xml)
    {
        EnsureConnected();
        var group = ResolveTagTableGroup(GetSoftware(plcName), groupPath);
        return group == null
            ? NotFound(groupPath)
            : ImportXml(xml, "SW.Tags.PlcTagTable", "a PLC tag table", tableName, file => group.Value.TagTables.Import(file, ImportOptions.Override));
    }

    private static ImportResult NotFound(string groupPath) =>
        new(false, new[] { $"Group '{groupPath}' not found." });

    // Checked before the import: the XML must be one object of the expected kind (elementPrefix,
    // e.g. "SW.Blocks." for any block type) named like its file, so a copied file that still
    // carries the original's name can't overwrite that original.
    private static ImportResult ImportXml(string xml, string elementPrefix, string expected, string name, Action<FileInfo> import)
    {
        XElement? element;
        try
        {
            element = XDocument.Parse(xml).Root?.Elements().FirstOrDefault(e => e.Name.LocalName.StartsWith(elementPrefix, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            return new ImportResult(false, new[] { $"Nothing was written: the XML can't be parsed: {ex.Message}" });
        }
        if (element == null)
            return new ImportResult(false, new[] { $"Nothing was written: the XML is not {expected} export (no {elementPrefix}* element under Document)." });

        var xmlName = (string?)element.Element("AttributeList")?.Element("Name");
        if (!string.Equals(xmlName, name, StringComparison.Ordinal))
            return new ImportResult(false, new[] { $"Nothing was written: the XML's <Name> is '{xmlName}', but the file is named '{name}'. Rename both together." });

        var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "TiaMcp", "import-xml", Guid.NewGuid().ToString("N")));
        dir.Create();
        try
        {
            var file = new FileInfo(Path.Combine(dir.FullName, "import.xml"));
            File.WriteAllText(file.FullName, xml);
            import(file);
            return new ImportResult(true, new[] { $"Imported '{name}' from XML." });
        }
        catch (Exception ex)
        {
            return new ImportResult(false, new[] { ex.Message });
        }
        finally
        {
            try { dir.Delete(true); } catch { /* best effort cleanup */ }
        }
    }
}
