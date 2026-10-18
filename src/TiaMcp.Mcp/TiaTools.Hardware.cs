using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using TiaMcp.Openness;

namespace TiaMcp.Mcp;

/// <summary>Read-only hardware tools: device/module list, I/O map with symbols, AML export.</summary>
public sealed partial class TiaTools
{
    private const int HardwareInlineLines = 400;

    [McpServerTool(Name = "list_hardware")]
    [Description("List the hardware configuration: every device (stations, ET 200 / GSD IO devices, HMIs, drives) with its modules - slot, name, order number, firmware and I/O addresses (I/Q start..end byte) - and its network interfaces (IP address, PROFINET device name, subnet, IO system, device number, IO controller PLC). Read-only. A long list is cut off; pass outputFile to get all of it in a file.")]
    public Task<string> ListHardware(
        [Description("Optional: only devices whose name contains this text (case-insensitive)")] string? deviceFilter = null,
        [Description("Optional: only devices whose I/O belongs to this PLC (PLC software name)")] string? plcName = null,
        [Description("Optional: write the full list to this file and return only a summary")] string? outputFile = null) => Safe(async () =>
    {
        var devices = (await _session.ReadHardwareAsync(deviceFilter))
            .Where(d => string.IsNullOrEmpty(plcName) || string.Equals(d.OwnerPlc, plcName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (devices.Count == 0) return "No devices found" + (deviceFilter != null || plcName != null ? " for this filter." : ".");

        var sb = new StringBuilder();
        foreach (var device in devices) FormatDevice(sb, device);
        var summary = $"{devices.Count} device(s), {devices.Sum(d => d.Modules.Count)} module(s), {devices.Sum(d => d.Modules.Sum(m => m.Addresses.Count))} I/O address range(s).";
        return Deliver(summary, sb.ToString(), outputFile);
    });

    private static void FormatDevice(StringBuilder sb, HwDevice device)
    {
        var kind = device.PlcSoftware != null ? $"PLC software: {device.PlcSoftware}"
            : device.OwnerPlc != null ? $"I/O of PLC {device.OwnerPlc}" : null;
        sb.AppendLine($"Device \"{device.Name}\"{(device.TypeName != null ? $" [{device.TypeName}]" : "")}{(device.IsGsd ? " GSD" : "")}{(kind != null ? "  " + kind : "")}");
        foreach (var node in device.Network)
        {
            var parts = new List<string> { $"{node.Interface} ({node.NodeType})" };
            if (!string.IsNullOrEmpty(node.Address)) parts.Add($"address {node.Address}");
            if (!string.IsNullOrEmpty(node.PnDeviceName)) parts.Add($"PN name '{node.PnDeviceName}'");
            if (!string.IsNullOrEmpty(node.Subnet)) parts.Add($"subnet {node.Subnet}");
            if (node.IoSystem != null) parts.Add($"{node.Role} on {node.IoSystem}");
            if (node.DeviceNumber != null) parts.Add($"device number {node.DeviceNumber}");
            if (node.ControllerPlc != null && node.ControllerPlc != device.PlcSoftware) parts.Add($"controller {node.ControllerPlc}");
            sb.AppendLine("  Net: " + string.Join(", ", parts));
        }
        var minDepth = device.Modules.Count == 0 ? 0 : device.Modules.Min(m => m.Depth);
        foreach (var module in device.Modules)
        {
            var indent = new string(' ', 2 + 2 * Math.Min(module.Depth - minDepth, 4));
            var slot = module.Position >= 0 ? $"[{module.Position}] " : "";
            var details = new List<string>();
            if (!string.IsNullOrEmpty(module.OrderNumber)) details.Add(module.OrderNumber!);
            if (!string.IsNullOrEmpty(module.Firmware)) details.Add(module.Firmware!);
            if (module.Failsafe) details.Add("F");
            details.AddRange(module.Addresses.Select(FormatAddress));
            sb.AppendLine($"{indent}{slot}{module.Name}{(details.Count > 0 ? "  " + string.Join("  ", details) : "")}");
        }
        sb.AppendLine();
    }

    private static string FormatAddress(HwAddress a)
    {
        if (a.Length % 8 != 0) return $"{a.Area} {a.Start}.0 ({a.Length} bit)";
        var bytes = a.Length / 8;
        return bytes == 1 ? $"{a.Area} {a.Start}" : $"{a.Area} {a.Start}..{a.Start + bytes - 1}";
    }

    [McpServerTool(Name = "read_io_map")]
    [Description("I/O map of a PLC: every module address range (I/Q, from the hardware configuration) with the PLC tags whose address falls into it (name, address, data type, comment, tag table). Also lists I/Q tags that lie outside every configured module (wrong address or module missing), module address ranges that overlap, and modules without any tag. Basis for later address changes. Read-only. A long map is cut off; pass outputFile to get all of it in a file.")]
    public Task<string> ReadIoMap(
        [Description("PLC software name, from list_project")] string plcName,
        [Description("Optional: only modules of devices whose name contains this text; the outside-tags check is skipped then")] string? deviceFilter = null,
        [Description("Optional: write the full map to this file and return only the summary and the problems")] string? outputFile = null) => Safe(async () =>
    {
        var devices = await _session.ReadHardwareAsync(deviceFilter);
        var ranges = new List<(HwDevice Device, HwModule Module, HwAddress Address)>();
        foreach (var device in devices)
            foreach (var module in device.Modules)
                foreach (var address in module.Addresses)
                    if (string.Equals(address.Plc ?? device.OwnerPlc, plcName, StringComparison.OrdinalIgnoreCase))
                        ranges.Add((device, module, address));
        ranges = ranges.OrderBy(r => r.Address.Area).ThenBy(r => r.Address.Start).ToList();

        var tags = new List<(IoTagAddress Address, TagInfo Tag, string Table)>();
        var otherTags = 0;
        foreach (var (table, result) in await _session.ReadAllTagTablesAsync(plcName))
        {
            if (!result.Success) continue;
            foreach (var tag in result.Tags)
            {
                var parsed = IoTagAddress.Parse(tag.LogicalAddress);
                if (parsed != null) tags.Add((parsed, tag, Label(table.GroupPath, table.Name)));
                else otherTags++;
            }
        }

        var map = new StringBuilder();
        var matched = new HashSet<TagInfo>();
        var emptyModules = new List<string>();
        foreach (var (device, module, address) in ranges)
        {
            var from = address.Start * 8;
            var to = from + address.Length;
            var inRange = tags.Where(t => t.Address.Area == address.Area && t.Address.FirstBit < to && t.Address.EndBit > from)
                .OrderBy(t => t.Address.FirstBit).ThenBy(t => t.Tag.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var where = $"{device.Name} / {module.Path}";
            map.AppendLine($"{FormatAddress(address)}  {where}{(module.OrderNumber != null ? $"  [{module.OrderNumber}]" : "")}{(module.Failsafe ? "  F" : "")}  - {inRange.Count} tag(s)");
            foreach (var t in inRange)
            {
                matched.Add(t.Tag);
                map.AppendLine($"    {t.Tag.LogicalAddress,-10} {t.Tag.Name}  ({t.Tag.DataType}){(string.IsNullOrEmpty(t.Tag.Comment) ? "" : "  // " + t.Tag.Comment)}  [{t.Table}]");
            }
            if (inRange.Count == 0) emptyModules.Add($"{FormatAddress(address)}  {where}");
        }

        var problems = new StringBuilder();
        if (string.IsNullOrEmpty(deviceFilter))
        {
            var outside = tags.Where(t => !matched.Contains(t.Tag)).OrderBy(t => t.Address.Area).ThenBy(t => t.Address.FirstBit).ToList();
            if (outside.Count > 0)
            {
                problems.AppendLine($"I/Q tags outside every configured module ({outside.Count}):");
                foreach (var t in outside) problems.AppendLine($"    {t.Tag.LogicalAddress,-10} {t.Tag.Name}{(string.IsNullOrEmpty(t.Tag.Comment) ? "" : "  // " + t.Tag.Comment)}  [{t.Table}]");
            }
        }
        var overlaps = new List<string>();
        for (var i = 0; i < ranges.Count; i++)
            for (var j = i + 1; j < ranges.Count && ranges[j].Address.Area == ranges[i].Address.Area; j++)
            {
                var a = ranges[i].Address;
                var b = ranges[j].Address;
                if (b.Start * 8 < a.Start * 8 + a.Length)
                    overlaps.Add($"    {FormatAddress(a)} {ranges[i].Device.Name}/{ranges[i].Module.Name}  <->  {FormatAddress(b)} {ranges[j].Device.Name}/{ranges[j].Module.Name}");
            }
        if (overlaps.Count > 0)
        {
            problems.AppendLine($"Overlapping module address ranges ({overlaps.Count}):");
            foreach (var o in overlaps) problems.AppendLine(o);
        }
        if (emptyModules.Count > 0)
        {
            problems.AppendLine($"Module address ranges without any tag ({emptyModules.Count}):");
            foreach (var e in emptyModules) problems.AppendLine("    " + e);
        }

        var summary = $"I/O map of {plcName}: {ranges.Count} module address range(s), {tags.Count} I/Q tag(s), {matched.Count} of them inside a module" +
            (otherTags > 0 ? $" ({otherTags} other tags, e.g. M/DB, not considered)" : "") + ".";
        if (ranges.Count == 0)
            summary += $"\nNo module addresses found for {plcName}. Check the PLC name, or use list_hardware to see which PLC the devices belong to.";
        var full = map + (problems.Length > 0 ? "\n" + problems : "");
        if (!string.IsNullOrEmpty(outputFile)) return Deliver(summary + (problems.Length > 0 ? "\n\n" + problems.ToString().TrimEnd() : ""), full, outputFile, includeBodyInline: false);
        return Deliver(summary, full, null);
    });

    // Writes the full text to outputFile (if given) and returns the summary; without a file the
    // text is returned inline, cut off after HardwareInlineLines lines.
    private static string Deliver(string summary, string body, string? outputFile, bool includeBodyInline = false)
    {
        if (!string.IsNullOrEmpty(outputFile))
        {
            var path = Path.GetFullPath(outputFile!);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, summary + "\n\n" + body, new UTF8Encoding(false));
            return summary + (includeBodyInline ? "\n\n" + body.TrimEnd() : "") + $"\nWritten to {outputFile}.";
        }
        var lines = body.TrimEnd().Split('\n');
        if (lines.Length <= HardwareInlineLines) return summary + "\n\n" + body.TrimEnd();
        return summary + "\n\n" + string.Join("\n", lines.Take(HardwareInlineLines)).TrimEnd() +
            $"\n... {lines.Length - HardwareInlineLines} more line(s). Pass outputFile (or a filter) to get all of it.";
    }

    [McpServerTool(Name = "set_io_address")]
    [Description("Move the I or Q address range of one module (or drive telegram / transfer area) to a new start byte, and move the PLC tags inside the old range along - the program uses the tags by name, so it follows. Refuses when the new range overlaps another range of the PLC, when tags lie only partly in the range or already use the new range, for fail-safe modules and F-tags (those are changed by hand), and while the PLC is online. dryRun=true (default) shows the old and new tag addresses; dryRun=false makes the change. Not compiled, not saved.")]
    public Task<string> SetIoAddress(
        [Description("PLC software name, from list_project")] string plcName,
        [Description("Device name, as list_hardware / read_io_map show it")] string deviceName,
        [Description("Module path inside the device, as read_io_map shows it after 'device / ' (e.g. 'DI_1/DI_1')")] string modulePath,
        [Description("I (or E) for the input range, Q (or A) for the output range")] string area,
        [Description("New start byte of the range")] int newStart,
        [Description("true (default) = preview only")] bool dryRun = true) => Safe(async () =>
    {
        var result = await _session.SetIoAddressAsync(plcName, deviceName, modulePath, area, newStart, dryRun);
        return result.Report;
    });

    [McpServerTool(Name = "export_hardware_aml")]
    [Description("Export the hardware configuration as an AutomationML (.aml) file with TIA Portal's CAx export: one device, or the whole project. Contains devices, modules, addresses, network and IO-system settings in full detail - useful for archiving or comparing two states. Read-only for the project. The whole project can take minutes.")]
    public Task<string> ExportHardwareAml(
        [Description("Target .aml file path; a .log file with all messages is written next to it")] string targetFile,
        [Description("Optional: only this device (exact name from list_hardware). Omit for the whole project.")] string? deviceName = null) => Safe(async () =>
    {
        var result = await _session.ExportHardwareAmlAsync(Path.GetFullPath(targetFile), deviceName);
        var head = result.FileWritten
            ? $"AML export written: {targetFile} ({result.Size / 1024} KB). TIA result: {result.State}, {result.ErrorCount} error(s), {result.WarningCount} warning(s)."
            : $"AML export FAILED - no file written. TIA result: {result.State}, {result.ErrorCount} error(s), {result.WarningCount} warning(s).";
        var problems = result.Problems.Take(30).ToList();
        return head +
            (problems.Count > 0 ? "\n" + string.Join("\n", problems.Select(p => "  " + p)) + (result.Problems.Count > problems.Count ? $"\n  ... {result.Problems.Count - problems.Count} more" : "") : "") +
            (result.FileWritten && result.ErrorCount > 0 ? "\nTIA wrote the file despite the errors - the items named above may be incomplete in it." : "") +
            $"\nAll messages: {result.LogFile}";
    });

    /// <summary>An I/Q tag address such as %I10.3, %EW64, %QD8 or %AB2 (German or English mnemonics).</summary>
    // A class, not a record: this project has no IsExternalInit for net48.
    private sealed class IoTagAddress
    {
        private IoTagAddress(string area, int firstBit, int endBit) { Area = area; FirstBit = firstBit; EndBit = endBit; }
        public string Area { get; }
        public int FirstBit { get; }
        public int EndBit { get; }

        private static readonly Regex Pattern = new(@"^%?(?<area>[IEQA])(?<size>[XBWDL])?(?<byte>\d+)(?:\.(?<bit>[0-7]))?(?::P)?$", RegexOptions.IgnoreCase);

        public static IoTagAddress? Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var m = Pattern.Match(text!.Trim());
            if (!m.Success) return null;
            var area = m.Groups["area"].Value.ToUpperInvariant() is "I" or "E" ? "I" : "Q";
            var start = int.Parse(m.Groups["byte"].Value) * 8;
            var size = m.Groups["size"].Success ? m.Groups["size"].Value.ToUpperInvariant() : "";
            if (m.Groups["bit"].Success)
            {
                if (size != "" && size != "X") return null;
                var bit = start + int.Parse(m.Groups["bit"].Value);
                return new IoTagAddress(area, bit, bit + 1);
            }
            var width = size switch { "B" => 8, "W" => 16, "D" => 32, "L" => 64, _ => 0 };
            return width == 0 ? null : new IoTagAddress(area, start, start + width);
        }
    }
}
