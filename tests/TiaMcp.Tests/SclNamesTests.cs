namespace TiaMcp.Tests;

public class SclNamesTests
{
    [Theory]
    [InlineData("Status", "Status")]
    [InlineData("s_Data_1", "s_Data_1")]
    [InlineData("Date", "\"Date\"")]
    [InlineData("TIME", "\"TIME\"")]
    [InlineData("end_struct", "\"end_struct\"")]
    [InlineData("M1-01", "\"M1-01\"")]
    [InlineData("1st", "\"1st\"")]
    public void Element_QuotesWhatAnAccessPathNeedsQuoted(string name, string expected)
    {
        Assert.Equal(expected, SclNames.Element(name));
    }
}
