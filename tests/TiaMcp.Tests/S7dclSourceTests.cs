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
}
