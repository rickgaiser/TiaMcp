using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TiaMcp.Mcp;
using HostingHost = Microsoft.Extensions.Hosting.Host;

namespace TiaMcp.Host;

internal static class Bootstrap
{
    public static void Run(string[] args)
    {
        var builder = HostingHost.CreateApplicationBuilder(args);

        // stdout is the MCP JSON-RPC channel - logs must go to stderr only.
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddSingleton<TiaSession>();
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly(typeof(TiaTools).Assembly);

        var app = builder.Build();
        app.Run();
    }
}
