using System;
using System.Linq;

namespace TiaMcp.Tests;

public class CsvTests
{
    [Theory]
    [InlineData("plain")]
    [InlineData("with, comma")]
    [InlineData("with \"quotes\"")]
    [InlineData("multi\r\nline")]
    [InlineData("Ünïcödé € 温度")]
    [InlineData("")]
    public void Field_RoundTrips(string value)
    {
        var csv = Csv.Format(new[] { "A", "B" }, new[] { new[] { value, "x" } });

        var rows = Csv.ParseRecords(csv);

        Assert.Single(rows);
        Assert.Equal(value.Replace("\r", ""), rows[0]["A"].Replace("\r", ""));
        Assert.Equal("x", rows[0]["B"]);
    }

    [Fact]
    public void Format_UsesCrlfAndQuotesOnlyWhenNeeded()
    {
        var csv = Csv.Format(new[] { "A", "B" }, new[] { new[] { "1", "a,b" } });

        Assert.Equal("A,B\r\n1,\"a,b\"\r\n", csv);
    }

    [Fact]
    public void Parse_AcceptsLfBomBlankLinesAndShortRows()
    {
        var rows = Csv.ParseRecords("﻿A,B\n1,2\n\n3\n");

        Assert.Equal(2, rows.Count);
        Assert.Equal("1", rows[0]["A"]);
        Assert.Equal("3", rows[1]["A"]);
        Assert.Equal("", rows[1]["B"]);
    }

    [Fact]
    public void TagTable_RoundTrips()
    {
        var tags = new[]
        {
            new TagInfo("Motor_Run", "Bool", "%Q0.0", "Runs the \"main\" motor, fast", true, true, false),
            new TagInfo("Speed", "Int", "%MW10", null, false, false, false),
        };

        var specs = TagTableCsv.Parse(TagTableCsv.Format(tags));

        Assert.Equal(new[]
        {
            new TagSpec("Motor_Run", "Bool", "%Q0.0", "Runs the \"main\" motor, fast", true, true, false),
            new TagSpec("Speed", "Int", "%MW10", null, false, false, false),
        }, specs);
    }

    [Fact]
    public void TagTable_MissingColumnsMeanLeaveUnchanged()
    {
        var specs = TagTableCsv.Parse("Name,DataType\r\nStart,Bool\r\n");

        Assert.Equal(new[] { new TagSpec("Start", "Bool", null) }, specs);
    }

    [Theory]
    [InlineData("Name,DataType\r\nStart,\r\n", "line 2")]
    [InlineData("Name,DataType,ExternalVisible\r\nStart,Bool,maybe\r\n", "ExternalVisible")]
    public void TagTable_BadInput_ThrowsFormatException(string csv, string expectedInMessage)
    {
        var ex = Assert.Throws<FormatException>(() => TagTableCsv.Parse(csv));
        Assert.Contains(expectedInMessage, ex.Message);
    }

    [Fact]
    public void HmiTagTable_RoundTrips()
    {
        var tags = new[]
        {
            new HmiTagInfo("Tank_Level", "Real", null, "HMI_Connection_1", "PLC_1", "Tank.Level", "Level in %", "1 s"),
            new HmiTagInfo("Local", "Bool", null, null, null, null, null, null),
            new HmiTagInfo("Caption", "WString", null, null, null, null, null, null, 12),
        };

        var specs = HmiTagTableCsv.Parse(HmiTagTableCsv.Format(tags, length: true));

        Assert.Equal(new[]
        {
            new HmiTagSpec("Tank_Level", "Real", null, "HMI_Connection_1", "PLC_1", "Tank.Level", "Level in %", "1 s"),
            new HmiTagSpec("Local", "Bool", null, null, null, null, null, null),
            new HmiTagSpec("Caption", "WString", null, null, null, null, null, null, 12),
        }, specs);
    }

    [Fact]
    public void HmiTagTable_WithoutLengthColumn_LeavesLengthUnchanged()
    {
        var csv = HmiTagTableCsv.Format(new[] { new HmiTagInfo("Caption", "WString", null, null, null, null, null, null, 12) });

        Assert.DoesNotContain("Length", csv);
        Assert.Null(HmiTagTableCsv.Parse(csv).Single().Length);
    }

    [Fact]
    public void HmiAlarms_RoundTripWithoutTriggerAddress()
    {
        var alarms = new[]
        {
            new HmiAlarmInfo("Discrete", "Overheat", "Errors", "Motor overheated, check fan", null, "%M1.0", null, null, "Alarm_Word", null, null, null),
        };

        var specs = HmiAlarmsCsv.ParseAlarms(HmiAlarmsCsv.FormatAlarms(alarms), "Discrete");

        Assert.Equal(new[]
        {
            new HmiAlarmSpec("Discrete", "Overheat", "Errors", "Motor overheated, check fan", null, null, null, null, "Alarm_Word", null, null, null),
        }, specs);
    }

    [Fact]
    public void HmiAlarmClasses_RoundTrip()
    {
        var classes = new[] { new HmiAlarmClassInfo("Errors", 16, "AlarmLog", 1, false), new HmiAlarmClassInfo("Info", 0, null, 2, false) };

        var specs = HmiAlarmsCsv.ParseClasses(HmiAlarmsCsv.FormatClasses(classes));

        Assert.Equal(new[] { new HmiAlarmClassSpec("Errors", 16, "AlarmLog"), new HmiAlarmClassSpec("Info", 0, null) }, specs);
    }

    [Theory]
    [InlineData("Name,Priority\r\n,1\r\n")]
    [InlineData("Name,Priority\r\nErrors,high\r\n")]
    public void HmiAlarmClasses_BadInput_ThrowsFormatException(string csv)
    {
        Assert.Throws<FormatException>(() => HmiAlarmsCsv.ParseClasses(csv));
    }

    [Fact]
    public void AlarmTextList_RoundTrips()
    {
        var list = new PlcAlarmTextList("States", new[] { "en-US", "de-DE" }, new[]
        {
            new PlcAlarmTextEntry("0", "0", new[] { "Off", "Aus" }),
            new PlcAlarmTextEntry("1", "9", new[] { "Running, \"normal\"", "Läuft" }),
        });

        var parsed = AlarmTextListCsv.Parse("States", AlarmTextListCsv.Format(list));

        Assert.Equal(list.Languages, parsed.Languages);
        Assert.Equal(list.Entries.Select(e => (e.From, e.To, string.Join("|", e.Texts))),
            parsed.Entries.Select(e => (e.From, e.To, string.Join("|", e.Texts))));
    }

    [Fact]
    public void AlarmTextList_EmptyToMeansSingleValue()
    {
        var parsed = AlarmTextListCsv.Parse("States", "From,To,Text [en-US]\r\n5,,Five\r\n");

        Assert.Equal("5", parsed.Entries[0].To);
    }

    [Theory]
    [InlineData("To,Text [en-US]\r\n1,x\r\n")]
    [InlineData("From,To\r\n1,1\r\n")]
    [InlineData("From,To,Text [en-US]\r\nabc,,x\r\n")]
    [InlineData("From,To,Text [en-US]\r\n1,1,x\r\n1,1,y\r\n")]
    public void AlarmTextList_BadInput_ThrowsFormatException(string csv)
    {
        Assert.Throws<FormatException>(() => AlarmTextListCsv.Parse("States", csv));
    }
}
