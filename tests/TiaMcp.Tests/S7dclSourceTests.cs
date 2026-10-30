namespace TiaMcp.Tests;

public class S7dclSourceTests
{
    private const string Fb = """
        {
            S7_Optimized := "TRUE";
            S7_PreferredLanguage := "LAD"
        }
        FUNCTION_BLOCK "Caller"
            VAR
                inst : _.Callee;
                {S7_TIMER}
            END_VAR

            { S7_Language := "LAD" }
            NETWORK
                RUNG wire#powerrail
                    #inst(
                        io := "Data_A".arr["Data_B".plain],
                        io2 := "Data_B".structArr[1].idx
                    )
                END_RUNG
            END_NETWORK
            { S7_Language := "SCL" }
            NETWORK
                #inst(io := "Data_A".arr["Data_B".structArr[1].idx], io2 := "Data_B".plain);
            END_NETWORK
        END_FUNCTION_BLOCK
        """;

    private static string With(string timer) => Fb.Replace("{S7_TIMER}", timer);

    [Fact]
    public void PlainBlock_HasNoProblems()
    {
        Assert.Empty(S7dclSource.FindWriteProblems(With("tmr : TON_TIME;")));
    }

    [Theory]
    [InlineData("tmr : _.TONR;")]
    [InlineData("tmr : _.TON;")]
    [InlineData("edge : _.R_TRIG;")]
    [InlineData("tmr : \"TOF\";")]
    [InlineData("timers : Array[1..4] of _.TP;")]
    [InlineData("\"tmr 1\" : _.CTU;")]
    public void ProjectTypeNamedLikeSystemType_IsAProblem(string declaration)
    {
        var problem = Assert.Single(S7dclSource.FindWriteProblems(With(declaration)));
        Assert.Contains("system type", problem);
    }

    [Theory]
    [InlineData("tmr : _.TONR_Own;")]
    [InlineData("tmr : _.MyTON;")]
    [InlineData("tmr : TONR_TIME;")]
    public void OtherTypes_AreNoProblem(string declaration)
    {
        Assert.Empty(S7dclSource.FindWriteProblems(With(declaration)));
    }

    [Theory]
    [InlineData("LAD", "io := \"Data_A\".arr[\"Data_B\".structArr[1].idx],")]
    [InlineData("FBD", "io := #locArr[#locS[1].idx],")]
    [InlineData("LAD", "io := \"Data_A\".arr[#locS[1].idx],")]
    public void NestedIndexInLadOrFbd_IsAProblem(string language, string parameter)
    {
        var dcl = With("").Replace("{ S7_Language := \"LAD\" }", $"{{ S7_Language := \"{language}\" }}")
            .Replace("io := \"Data_A\".arr[\"Data_B\".plain],", parameter);

        var problem = Assert.Single(S7dclSource.FindWriteProblems(dcl));
        Assert.Contains($"network 1 ({language})", problem);
        Assert.Contains(parameter, problem);
    }

    [Fact]
    public void BracketsInAQuotedName_AreNoNesting()
    {
        var dcl = With("").Replace("io := \"Data_A\".arr[\"Data_B\".plain],", "io := \"Data_A\".arr[#\"locS[1]\".idx],");
        Assert.Empty(S7dclSource.FindWriteProblems(dcl));
    }

    [Fact]
    public void WithBlockNumber_AddsItFirst()
    {
        var dcl = S7dclSource.WithBlockNumber(With("").Replace("\r\n", "\n").Replace("\n", "\r\n"), 9001);

        Assert.StartsWith("{\r\n    S7_BlockNumber := \"9001\";\r\n    S7_Optimized := \"TRUE\";", dcl);
    }

    [Fact]
    public void WithBlockNumber_KeepsAnExistingNumber()
    {
        var dcl = "{\n    S7_BlockNumber := \"12\";\n    S7_Optimized := \"TRUE\"\n}\nFUNCTION_BLOCK \"X\"\nEND_FUNCTION_BLOCK\n";
        Assert.Equal(dcl, S7dclSource.WithBlockNumber(dcl, 9001));
    }

    private const string OverlayXml = """
        <Document><SW.Blocks.FB ID="0"><AttributeList><Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5">
          <Section Name="Static">
            <Member Name="flags" Datatype="Array[1..16] of Bool" />
            <Member Name="flagsAt" Datatype="Word"><AttributeList><StringAttribute Name="At" SystemDefined="true">flags</StringAttribute></AttributeList></Member>
          </Section>
        </Sections></Interface></AttributeList></SW.Blocks.FB></Document>
        """;

    private const string OverlayFb = """
        {
            S7_Optimized := "TRUE"
        }
        FUNCTION_BLOCK "Overlay"
            VAR
                flags : Array[1..16] of Bool;
                {
                    S7_Access := "ReadOnly := External";
                    S7_Visibility := "Hidden := External"
                }
                flagsAt : Word;
            END_VAR

            { S7_Language := "SCL" }
            NETWORK
                #flagsAt := 16#0001;
            END_NETWORK
        END_FUNCTION_BLOCK
        """;

    [Fact]
    public void AtOverlays_ReadsTheAtAttribute()
    {
        Assert.Equal(new[] { ("flagsAt", "flags") }, S7dclSource.AtOverlays(OverlayXml));
    }

    [Fact]
    public void WithAtMarkers_MarksTheDeclarationOnly_AndIsRefused()
    {
        var dcl = S7dclSource.WithAtMarkers(OverlayFb, S7dclSource.AtOverlays(OverlayXml));

        Assert.Contains("flagsAt : Word; // TiaMcp: AT \"flags\"", dcl);
        Assert.DoesNotContain("16#0001; //", dcl);
        Assert.Equal(dcl, S7dclSource.WithAtMarkers(dcl, S7dclSource.AtOverlays(OverlayXml)));
        var problem = Assert.Single(S7dclSource.FindWriteProblems(dcl));
        Assert.Contains("'flagsAt' is an AT overlay of \"flags\"", problem);
        Assert.Empty(S7dclSource.FindWriteProblems(OverlayFb));
    }
}
