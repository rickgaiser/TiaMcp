using System.Xml.Linq;

namespace TiaMcp.Tests;

public class VbScriptXmlTests
{
    // Made-up script, shaped like an Openness export (DocumentInfo already removed).
    private const string Sample = @"<Document>
  <Engineering version=""V21"" />
  <Hmi.RuntimeScripting.VBScript ID=""0"">
    <AttributeList>
      <Code>
' Writes a value
Dim nValue
nValue = SmartTags(sTagName) + 1
If nValue &gt; 10 Then nValue = 0
SmartTags(sTagName) = nValue</Code>
      <Name>ExampleIncrement</Name>
      <PreCode></PreCode>
      <Type>Sub</Type>
    </AttributeList>
    <ObjectList>
      <Hmi.RuntimeScripting.ScriptParameter ID=""1"" CompositionName=""Parameters"">
        <AttributeList>
          <Name>sTagName</Name>
          <Type>ByVal</Type>
        </AttributeList>
      </Hmi.RuntimeScripting.ScriptParameter>
    </ObjectList>
  </Hmi.RuntimeScripting.VBScript>
</Document>";

    private static string Xml => XDocument.Parse(Sample, LoadOptions.PreserveWhitespace).ToString();

    [Fact]
    public void ReadCode_GivesTheCodeWithCrLf()
    {
        var code = VbScriptXml.ReadCode(Xml);
        Assert.StartsWith("\r\n' Writes a value\r\nDim nValue\r\n", code);
        Assert.Contains("If nValue > 10 Then", code);
        Assert.DoesNotContain("\r\r", code);
    }

    [Fact]
    public void UnchangedCode_GivesTheSameXml() =>
        Assert.Equal(Xml, VbScriptXml.WithCode(Xml, VbScriptXml.ReadCode(Xml)));

    [Fact]
    public void WithCode_ReplacesOnlyTheCode()
    {
        var edited = VbScriptXml.WithCode(Xml, "Dim a\r\na = 1 < 2");
        Assert.Equal("Dim a\r\na = 1 < 2", VbScriptXml.ReadCode(edited));
        Assert.Contains("<Name>ExampleIncrement</Name>", edited);
        Assert.Contains("<Name>sTagName</Name>", edited);
        Assert.Contains("a = 1 &lt; 2", edited);
    }

    [Fact]
    public void NotAScript_IsAFormatException() =>
        Assert.Throws<FormatException>(() => VbScriptXml.ReadCode("<Document><Hmi.Screen.Screen ID=\"0\" /></Document>"));

    [Fact]
    public void BrokenXml_IsAFormatException() =>
        Assert.Throws<FormatException>(() => VbScriptXml.ReadCode("<Document><Hmi"));
}
