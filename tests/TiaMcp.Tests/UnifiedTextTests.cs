namespace TiaMcp.Tests;

public class UnifiedTextTests
{
    [Theory]
    [InlineData("Pump running")]
    [InlineData("A < B & C > D")]
    [InlineData("two\nlines")]
    [InlineData("")]
    public void Plain_RoundTrips(string text) =>
        Assert.Equal(text, UnifiedText.HtmlToPlain(UnifiedText.PlainToHtml(text)));

    [Fact]
    public void Formatted_StaysRaw()
    {
        const string formatted = "<body><p><span style=\"color:#ff0000\">Red</span></p></body>";
        Assert.Equal(formatted, UnifiedText.HtmlToPlain(formatted));
        Assert.Equal(formatted, UnifiedText.Repair(formatted));
    }

    [Fact]
    public void Repair_UnwrapsTextWrappedOncePerRoundTrip()
    {
        var text = "Motor fault & reset";
        for (var i = 0; i < 4; i++) text = UnifiedText.PlainToHtml(text);
        Assert.Equal("Motor fault & reset", UnifiedText.Repair(text));
    }

    [Fact]
    public void Repair_KeepsTheFirstNonEmptyOfJoinedLanguages()
    {
        // An old read joined the languages with " | "; a write wrapped that as one text.
        var nested = UnifiedText.PlainToHtml("<body><p/></body> | <body><p>Valve open</p></body>");
        Assert.Equal("Valve open", UnifiedText.Repair(UnifiedText.PlainToHtml(nested)));
    }

    [Fact]
    public void Repair_LeavesPlainTextWithBarAlone() =>
        Assert.Equal("Left | Right", UnifiedText.Repair(UnifiedText.PlainToHtml("Left | Right")));

    [Fact]
    public void AlarmRow_UnchangedWhenEveryCellMatches()
    {
        var current = new HmiAlarmInfo("Discrete", "Alarm_1", "Alarm", "Fault", null, "Tag_1:0", null, null, "Tag_1", null, "Area1", null);
        var same = new HmiAlarmSpec("Discrete", "Alarm_1", "Alarm", "Fault", null, null, null, null, "Tag_1", null, "Area1", null);
        Assert.True(HmiAlarmsCsv.Unchanged(same, current));
        Assert.False(HmiAlarmsCsv.Unchanged(same with { EventText = "Fault 2" }, current));
        Assert.False(HmiAlarmsCsv.Unchanged(same with { InfoText = "Call service" }, current));
    }
}
