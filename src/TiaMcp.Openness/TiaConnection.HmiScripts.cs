using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HmiUnified.Scripts;

namespace TiaMcp.Openness;

public sealed partial class TiaConnection
{
    public IReadOnlyList<HmiScriptModuleSummary> ListHmiScriptModules(string hmiName)
    {
        EnsureConnected();
        return GetHmiSoftware(hmiName).Scripts
            .Cast<HmiScriptModule>()
            .Select(module => new HmiScriptModuleSummary(hmiName, module.Name))
            .ToList();
    }

    public IReadOnlyList<(HmiScriptModuleSummary Module, ExportResult Result)> ReadHmiScriptModules(string hmiName, Func<string, bool>? include = null, ExportingItem? exporting = null)
    {
        EnsureConnected();
        var result = new List<(HmiScriptModuleSummary, ExportResult)>();
        var modules = GetHmiSoftware(hmiName).Scripts.Cast<HmiScriptModule>()
            .Where(module => include == null || include(module.Name))
            .ToList();
        foreach (var (module, index) in modules.Select((module, index) => (module, index)))
        {
            exporting?.Invoke(index + 1, modules.Count, "", module.Name);
            result.Add((new HmiScriptModuleSummary(hmiName, module.Name), ExportHmiScriptModule(module)));
        }
        return result;
    }

    private static ExportResult ExportHmiScriptModule(HmiScriptModule module)
    {
        var directory = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "TiaMcp", "hmi-scripts", Guid.NewGuid().ToString("N")));
        try
        {
            Directory.CreateDirectory(directory.FullName);
            var source = module.Export(directory)
                .FirstOrDefault(file => file.Exists && file.Name.EndsWith(".hmi.js", StringComparison.OrdinalIgnoreCase));
            if (source == null)
                return new ExportResult(false, Array.Empty<BlockDocument>(), "Script module export produced no .hmi.js source file.");

            var document = new BlockDocument(module.Name + SourceFormat.Extension(SourceFormat.Js), File.ReadAllText(source.FullName));
            return new ExportResult(true, new[] { document }, null);
        }
        catch (Exception ex)
        {
            return new ExportResult(false, Array.Empty<BlockDocument>(), DescribeException(ex));
        }
        finally
        {
            try { if (directory.Exists) Directory.Delete(directory.FullName, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
