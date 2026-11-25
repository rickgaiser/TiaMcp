using System;
using System.Linq;
using System.Xml.Linq;

namespace TiaMcp.Tests;

public class WatchTableCsvTests
{
    // The shape of a V21 watch table export: a comment row, two tag rows, a blank row.
    private const string Export = """
        <?xml version="1.0" encoding="utf-8"?>
        <Document>
          <Engineering version="V21" />
          <SW.WatchAndForceTables.PlcWatchTable ID="0">
            <AttributeList>
              <Name>Conveyor</Name>
            </AttributeList>
            <ObjectList>
              <SW.WatchAndForceTables.PlcTableCommentEntry ID="1" CompositionName="Entries">
                <ObjectList>
                  <MultilingualText ID="2" CompositionName="Comment">
                    <ObjectList>
                      <MultilingualTextItem ID="3" CompositionName="Items">
                        <AttributeList><Culture>en-US</Culture><Text>Sequence state</Text></AttributeList>
                      </MultilingualTextItem>
                      <MultilingualTextItem ID="4" CompositionName="Items">
                        <AttributeList><Culture>de-DE</Culture><Text /></AttributeList>
                      </MultilingualTextItem>
                    </ObjectList>
                  </MultilingualText>
                </ObjectList>
              </SW.WatchAndForceTables.PlcTableCommentEntry>
              <SW.WatchAndForceTables.PlcWatchTableEntry ID="5" CompositionName="Entries">
                <AttributeList>
                  <DisplayFormat>Bool</DisplayFormat>
                  <ModifyTrigger>Permanent</ModifyTrigger>
                  <ModifyValue />
                  <MonitorTrigger>Permanent</MonitorTrigger>
                  <Name>"Conveyor_DB".State.Running</Name>
                </AttributeList>
                <ObjectList>
                  <MultilingualText ID="6" CompositionName="Comment">
                    <ObjectList>
                      <MultilingualTextItem ID="7" CompositionName="Items">
                        <AttributeList><Culture>en-US</Culture><Text /></AttributeList>
                      </MultilingualTextItem>
                      <MultilingualTextItem ID="8" CompositionName="Items">
                        <AttributeList><Culture>de-DE</Culture><Text>läuft</Text></AttributeList>
                      </MultilingualTextItem>
                    </ObjectList>
                  </MultilingualText>
                </ObjectList>
              </SW.WatchAndForceTables.PlcWatchTableEntry>
              <SW.WatchAndForceTables.PlcWatchTableEntry ID="9" CompositionName="Entries">
                <AttributeList>
                  <DisplayFormat>DEC_signed</DisplayFormat>
                  <ModifyTrigger>OnceOnlyAtStart</ModifyTrigger>
                  <ModifyValue>12</ModifyValue>
                  <MonitorTrigger>Permanent</MonitorTrigger>
                  <Name>"m_Speed"</Name>
                </AttributeList>
              </SW.WatchAndForceTables.PlcWatchTableEntry>
              <SW.WatchAndForceTables.PlcTableCommentEntry ID="A" CompositionName="Entries">
                <ObjectList>
                  <MultilingualText ID="B" CompositionName="Comment">
                    <ObjectList>
                      <MultilingualTextItem ID="C" CompositionName="Items">
                        <AttributeList><Culture>en-US</Culture><Text /></AttributeList>
                      </MultilingualTextItem>
                    </ObjectList>
                  </MultilingualText>
                </ObjectList>
              </SW.WatchAndForceTables.PlcTableCommentEntry>
            </ObjectList>
          </SW.WatchAndForceTables.PlcWatchTable>
        </Document>
        """;

    [Fact]
    public void FromXml_ReadsRowsInOrder()
    {
        var table = WatchTableCsv.FromXml(Export);

        Assert.Equal("Conveyor", table.Name);
        Assert.False(table.IsForceTable);
        Assert.Equal(new[] { "en-US", "de-DE" }, table.Languages);
        Assert.Equal(4, table.Rows.Count);
        Assert.True(table.Rows[0].IsComment);
        Assert.Equal(new[] { "Sequence state", "" }, table.Rows[0].Comments);
        Assert.Equal("\"Conveyor_DB\".State.Running", table.Rows[1].Name);
        Assert.Equal("Bool", table.Rows[1].DisplayFormat);
        Assert.Null(table.Rows[1].Value);
        Assert.Equal(new[] { "", "läuft" }, table.Rows[1].Comments);
        Assert.Equal("12", table.Rows[2].Value);
        Assert.Equal("OnceOnlyAtStart", table.Rows[2].ModifyTrigger);
        Assert.Equal(new[] { "", "" }, table.Rows[2].Comments);
        Assert.True(table.Rows[3].IsComment);
        Assert.All(table.Rows[3].Comments, c => Assert.Equal("", c));
    }

    [Fact]
    public void Format_WritesCommentMarkAndHidesDefaultTriggers()
    {
        var csv = WatchTableCsv.Format(WatchTableCsv.FromXml(Export));
        var lines = csv.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Name,DisplayFormat,ModifyValue,MonitorTrigger,ModifyTrigger,Comment [en-US],Comment [de-DE]", lines[0]);
        Assert.Equal("//,,,,,Sequence state,", lines[1]);
        Assert.Equal("\"\"\"Conveyor_DB\"\".State.Running\",Bool,,,,,läuft", lines[2]);
        Assert.Equal("\"\"\"m_Speed\"\"\",DEC_signed,12,,OnceOnlyAtStart,,", lines[3]);
        Assert.Equal(",,,,,,", lines[4]);
    }

    [Fact]
    public void Csv_RoundTrips()
    {
        var table = WatchTableCsv.FromXml(Export);

        var parsed = WatchTableCsv.Parse(table.Name, WatchTableCsv.Format(table));

        Assert.Equal(table.Languages, parsed.Languages);
        Assert.Equal(table.Rows.Count, parsed.Rows.Count);
        for (var i = 0; i < table.Rows.Count; i++)
        {
            var (a, b) = (table.Rows[i], parsed.Rows[i]);
            Assert.Equal(a.IsComment, b.IsComment);
            Assert.Equal(a.Name, b.Name);
            Assert.Equal(a.DisplayFormat, b.DisplayFormat);
            Assert.Equal(a.Value, b.Value);
            Assert.Equal(a.Comments, b.Comments);
            // A Permanent trigger reads back as the default (null).
            Assert.Equal(a.ModifyTrigger == WatchTableCsv.DefaultTrigger ? null : a.ModifyTrigger, b.ModifyTrigger);
        }
    }

    [Fact]
    public void ToXml_ReadsBackTheSame()
    {
        var table = WatchTableCsv.Parse("Conveyor", WatchTableCsv.Format(WatchTableCsv.FromXml(Export)));

        var again = WatchTableCsv.FromXml(WatchTableCsv.ToXml(table));

        Assert.Equal(WatchTableCsv.Format(table), WatchTableCsv.Format(again));
    }

    [Fact]
    public void ToXml_WithoutDisplayFormats_LeavesThemOut()
    {
        var table = WatchTableCsv.FromXml(Export);

        var xml = WatchTableCsv.ToXml(table, withDisplayFormats: false);

        Assert.DoesNotContain("<DisplayFormat>", xml);
        Assert.Contains("<Name>\"m_Speed\"</Name>", xml);
    }

    [Fact]
    public void ToXml_GivesEveryElementAUniqueId()
    {
        var xml = WatchTableCsv.ToXml(WatchTableCsv.FromXml(Export));

        var ids = XDocument.Parse(xml).Descendants().Select(e => (string?)e.Attribute("ID")).Where(i => i != null).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Parse_InlineCommentGoesToFirstLanguage()
    {
        var table = WatchTableCsv.Parse("T", "Name,DisplayFormat,ModifyValue,Comment [en-US],Comment [de-DE]\r\n// Start conditions,,,,\r\n\"m_A\",,,,\r\n");

        Assert.True(table.Rows[0].IsComment);
        Assert.Equal(new[] { "Start conditions", "" }, table.Rows[0].Comments);
        Assert.False(table.Rows[1].IsComment);
        Assert.Null(table.Rows[1].DisplayFormat);
    }

    [Fact]
    public void Parse_NormalizesCaseOfDisplayFormat()
    {
        var table = WatchTableCsv.Parse("T", "Name,DisplayFormat\r\n\"m_A\",dec_signed\r\n");

        Assert.Equal("DEC_signed", table.Rows[0].DisplayFormat);
    }

    [Theory]
    [InlineData("Name,DisplayFormat\r\n\"m_A\",Decimal\r\n", "line 2: DisplayFormat 'Decimal'")]
    [InlineData("Name,MonitorTrigger\r\n\"m_A\",Always\r\n", "line 2: MonitorTrigger 'Always'")]
    [InlineData("Name,DisplayFormat\r\n//,Bool\r\n", "line 2: a comment row")]
    [InlineData("Name,Comment [en-US]\r\n// a,b\r\n", "line 2: comment text both")]
    [InlineData("Name\r\n// a\r\n", "line 2: comment text needs")]
    [InlineData("Symbol\r\nx\r\n", "column 'Name' missing")]
    public void Parse_RefusesBadInput(string csv, string message)
    {
        var ex = Assert.Throws<FormatException>(() => WatchTableCsv.Parse("T", csv));

        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void ForceTable_UsesForceValue()
    {
        const string xml = """
            <Document>
              <SW.WatchAndForceTables.PlcForceTable ID="0">
                <AttributeList><Name>Force table</Name></AttributeList>
                <ObjectList>
                  <SW.WatchAndForceTables.PlcForceTableEntry ID="1" CompositionName="Entries">
                    <AttributeList>
                      <Address>%Q0.0</Address>
                      <DisplayFormat>Bool</DisplayFormat>
                      <ForceValue>TRUE</ForceValue>
                      <MonitorTrigger>Permanent</MonitorTrigger>
                    </AttributeList>
                  </SW.WatchAndForceTables.PlcForceTableEntry>
                </ObjectList>
              </SW.WatchAndForceTables.PlcForceTable>
            </Document>
            """;

        var table = WatchTableCsv.FromXml(xml);
        var csv = WatchTableCsv.Format(table);

        Assert.True(table.IsForceTable);
        Assert.Equal("TRUE", table.Rows[0].Value);
        Assert.StartsWith("Name,DisplayFormat,ForceValue,MonitorTrigger\r\n%Q0.0,Bool,TRUE,\r\n", csv);
    }

    [Fact]
    public void AddressOnlyRow_RoundTrips()
    {
        const string xml = "<Document><SW.WatchAndForceTables.PlcWatchTable ID=\"0\"><AttributeList><Name>T</Name></AttributeList><ObjectList>"
            + "<SW.WatchAndForceTables.PlcWatchTableEntry ID=\"1\" CompositionName=\"Entries\"><AttributeList><Address>%M10.3</Address><DisplayFormat>Bool</DisplayFormat>"
            + "<ModifyTrigger>Permanent</ModifyTrigger><ModifyValue>FALSE</ModifyValue><MonitorTrigger>Permanent</MonitorTrigger></AttributeList>"
            + "</SW.WatchAndForceTables.PlcWatchTableEntry></ObjectList></SW.WatchAndForceTables.PlcWatchTable></Document>";

        var table = WatchTableCsv.Parse("T", WatchTableCsv.Format(WatchTableCsv.FromXml(xml)));
        var entry = XDocument.Parse(WatchTableCsv.ToXml(table)).Descendants("SW.WatchAndForceTables.PlcWatchTableEntry").Single().Element("AttributeList")!;

        Assert.Equal("%M10.3", table.Rows.Single().Name);
        Assert.False(table.Rows.Single().IsComment);
        Assert.Equal("%M10.3", (string?)entry.Element("Address"));
        Assert.Null(entry.Element("Name"));
    }

    [Fact]
    public void RowWithoutNameOrAddress_IsRefused()
    {
        const string xml = "<Document><SW.WatchAndForceTables.PlcWatchTable ID=\"0\"><AttributeList><Name>T</Name></AttributeList><ObjectList>"
            + "<SW.WatchAndForceTables.PlcWatchTableEntry ID=\"1\" CompositionName=\"Entries\"><AttributeList><DisplayFormat>Bool</DisplayFormat></AttributeList>"
            + "</SW.WatchAndForceTables.PlcWatchTableEntry></ObjectList></SW.WatchAndForceTables.PlcWatchTable></Document>";

        Assert.Throws<FormatException>(() => WatchTableCsv.FromXml(xml));
    }

    [Fact]
    public void EmptyTable_HasOnlyTheHeader()
    {
        const string xml = "<Document><SW.WatchAndForceTables.PlcWatchTable ID=\"0\"><AttributeList><Name>Empty</Name></AttributeList></SW.WatchAndForceTables.PlcWatchTable></Document>";

        var csv = WatchTableCsv.Format(WatchTableCsv.FromXml(xml));

        Assert.Equal("Name,DisplayFormat,ModifyValue,MonitorTrigger,ModifyTrigger\r\n", csv);
    }
}
