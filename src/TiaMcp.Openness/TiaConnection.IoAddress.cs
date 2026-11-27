using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Tags;

namespace TiaMcp.Openness;

/// <summary>
/// Moves the I or Q address range of one module (or drive telegram / transfer area) to a new
/// start byte and moves the PLC tags inside the old range along, so the program, which refers to
/// the tags symbolically, keeps working. Fail-safe modules are refused - they belong to the
/// safety program and are changed by hand.
/// </summary>
public sealed partial class TiaConnection
{
    private static readonly Regex TagAddressPattern = new(@"^(?<pre>%?)(?<area>[IEQA])(?<size>[XBWDL]?)(?<byte>\d+)(?<rest>(\.[0-7])?(:P)?)$", RegexOptions.IgnoreCase);

    public IoAddressChangeResult SetIoAddress(string plcName, string deviceName, string modulePath, string area, int newStart, bool dryRun)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);
        area = area.Trim().ToUpperInvariant() switch { "I" or "E" => "I", "Q" or "A" => "Q", _ => "" };
        if (area == "") return Fail("Area must be I (E) or Q (A).");
        if (newStart < 0 || newStart > 32767) return Fail("The new start address must be between 0 and 32767.");

        if (IsPlcOnline(plcName))
            return Fail($"PLC {plcName} is ONLINE - go offline in TIA Portal first. Nothing was changed.");

        var device = AllDevices().FirstOrDefault(d => string.Equals(d.Name, deviceName, StringComparison.OrdinalIgnoreCase));
        if (device == null) return Fail($"Device '{deviceName}' not found. Use list_hardware for the device names.");

        var slots = new List<AddressSlot>();
        var seenTarget = new List<Address>();
        void Collect(DeviceItem item, string parentPath)
        {
            var path = string.IsNullOrEmpty(parentPath) ? item.Name : $"{parentPath}/{item.Name}";
            slots.AddRange(AddressSlotsOf(item, path, seenTarget));
            foreach (var child in ByPosition(item.DeviceItems)) Collect(child, path);
        }
        foreach (var item in ByPosition(device.DeviceItems)) Collect(item, "");

        var matches = slots.Where(x => x.Area == area && string.Equals(x.Path, modulePath.Trim().Trim('/'), StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            var known = string.Join("\n", slots.Select(x => $"  {x.Path}  {x.Area} {x.Address.StartAddress}").Distinct().Take(60));
            return Fail($"No {area} address at '{modulePath}' in device '{deviceName}'. Address ranges of this device:\n{known}");
        }
        if (matches.Count > 1) return Fail($"'{modulePath}' has {matches.Count} {area} address ranges - ambiguous, not supported.");
        var target = matches[0];
        if (target.Failsafe)
            return Fail($"'{modulePath}' is a fail-safe module/telegram. Its addresses belong to the safety program - change them by hand in TIA Portal (with the safety login), then check with read_io_map.");

        var oldStart = target.Address.StartAddress;
        var lengthBits = target.Address.Length;
        var lengthBytes = (lengthBits + 7) / 8;
        var delta = newStart - oldStart;
        var sb = new StringBuilder();
        sb.AppendLine($"{(dryRun ? "PREVIEW - nothing changed" : "CHANGE")}: {deviceName} / {target.Path}  {area} {Range(oldStart, lengthBytes)}  ->  {area} {Range(newStart, lengthBytes)}");
        if (delta == 0) return new IoAddressChangeResult(false, sb + "The range already starts there - nothing to do.", "");

        // Other ranges of the same PLC in the same area must not overlap the new one.
        var plcByCpu = new Dictionary<object, string>();
        foreach (var pair in PlcCpuItems())
        {
            plcByCpu[pair.Value] = pair.Key;
            foreach (var controller in AddressControllersIn(pair.Value)) plcByCpu[controller] = pair.Key;
        }
        var targetPlc = OwnerOf(target.Address, plcByCpu);
        if (targetPlc != null && !string.Equals(targetPlc, plcName, StringComparison.OrdinalIgnoreCase))
            return Fail($"'{target.Path}' belongs to PLC '{targetPlc}', not '{plcName}'.");
        var collisions = new List<string>();
        foreach (var other in AllDevices())
        {
            var seenOther = new List<Address>();
            void WalkOther(DeviceItem item, string parentPath)
            {
                var path = string.IsNullOrEmpty(parentPath) ? item.Name : $"{parentPath}/{item.Name}";
                foreach (var slot in AddressSlotsOf(item, path, seenOther))
                {
                    if (slot.Area != area || slot.Address.Equals(target.Address)) continue;
                    var owner = OwnerOf(slot.Address, plcByCpu);
                    if (owner != null && !string.Equals(owner, plcName, StringComparison.OrdinalIgnoreCase)) continue;
                    var s = slot.Address.StartAddress;
                    var e = s + (slot.Address.Length + 7) / 8;
                    if (s < newStart + lengthBytes && newStart < e) collisions.Add($"  {other.Name} / {slot.Path}  {area} {Range(s, e - s)}");
                }
                foreach (var child in ByPosition(item.DeviceItems)) WalkOther(child, path);
            }
            foreach (var item in ByPosition(other.DeviceItems)) WalkOther(item, "");
        }
        if (collisions.Count > 0)
            return Fail(sb + $"REFUSED: the new range overlaps\n{string.Join("\n", collisions)}");

        // Tags: inside the old range -> move; partly inside -> refuse; already in the new range -> refuse.
        var tables = new List<PlcTagTable>();
        WalkTagTables(plcName, software.TagTableGroup.TagTables, software.TagTableGroup.Groups, "", new List<TagTableSummary>(), tables);
        var moves = new List<(PlcTag Tag, string Table, string NewAddress)>();
        var problems = new List<string>();
        var oldFrom = oldStart * 8;
        var oldTo = oldFrom + lengthBits;
        var newFrom = newStart * 8;
        var newTo = newFrom + lengthBytes * 8;
        foreach (var table in tables)
        {
            foreach (PlcTag tag in table.Tags)
            {
                var address = tag.LogicalAddress ?? "";
                if (!TryParseTagAddress(address, out var tagArea, out var first, out var end)) continue;
                if (tagArea != area) continue;
                var inOld = first < oldTo && end > oldFrom;
                var inNew = first < newTo && end > newFrom;
                if (inOld)
                {
                    if (first < oldFrom || end > oldTo) { problems.Add($"  {address,-10} {tag.Name}  [{table.Name}] lies only partly in the range"); continue; }
                    moves.Add((tag, table.Name, ShiftTagAddress(address, delta)));
                }
                else if (inNew)
                {
                    problems.Add($"  {address,-10} {tag.Name}  [{table.Name}] already uses the new range");
                }
            }
        }
        if (problems.Count > 0)
            return Fail(sb + $"REFUSED: tags in the way:\n{string.Join("\n", problems)}");

        sb.AppendLine($"{moves.Count} tag(s) move along (the program refers to them by name, so it follows):");
        foreach (var m in moves.OrderBy(m => m.Tag.LogicalAddress, StringComparer.Ordinal))
            sb.AppendLine($"  {m.Tag.LogicalAddress,-10} -> {m.NewAddress,-10} {m.Tag.Name}  [{m.Table}]{(m.Tag.IsSafety ? "  (F-tag)" : "")}");
        if (moves.Any(m => m.Tag.IsSafety))
            return Fail(sb + "REFUSED: F-tags (safety program) are involved - change this by hand with the safety login.");

        string hash;
        using (var sha = SHA256.Create())
        {
            var basis = $"{deviceName}|{target.Path}|{area}|{oldStart}|{lengthBits}|{newStart}|" + string.Join(";", moves.Select(m => $"{m.Tag.Name}={m.Tag.LogicalAddress}"));
            hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(basis))).Replace("-", "").Substring(0, 16);
        }
        if (dryRun) return new IoAddressChangeResult(true, sb.ToString().TrimEnd(), hash);

        try
        {
            target.Address.SetAttribute("StartAddress", newStart);
        }
        catch (Exception ex)
        {
            return Fail(sb + $"FAILED - TIA Portal refused the new start address: {ex.Message}\nNothing was changed.");
        }
        if (target.Address.StartAddress != newStart)
            return Fail(sb + $"FAILED - the start address reads back as {target.Address.StartAddress}, not {newStart}. Tags were not changed.");

        var failed = new List<string>();
        foreach (var m in moves)
        {
            try
            {
                m.Tag.LogicalAddress = m.NewAddress;
                if (!string.Equals(m.Tag.LogicalAddress?.TrimStart('%'), m.NewAddress.TrimStart('%'), StringComparison.OrdinalIgnoreCase))
                    failed.Add($"  {m.Tag.Name}: reads back as {m.Tag.LogicalAddress}");
            }
            catch (Exception ex)
            {
                failed.Add($"  {m.Tag.Name}: {ex.Message}");
            }
        }
        sb.AppendLine($"Start address set to {newStart}; {moves.Count - failed.Count} of {moves.Count} tag(s) moved.");
        if (failed.Count > 0) sb.AppendLine("Tags NOT moved:\n" + string.Join("\n", failed));
        sb.Append("Compile the PLC (compile_plc) and check with read_io_map. Not saved.");
        return new IoAddressChangeResult(failed.Count == 0, sb.ToString(), hash);

        IoAddressChangeResult Fail(string text) => new(false, text, "");
    }

    private static string Range(int start, int bytes) => bytes <= 1 ? $"{start}" : $"{start}..{start + bytes - 1}";

    private static bool TryParseTagAddress(string text, out string area, out int firstBit, out int endBit)
    {
        area = ""; firstBit = endBit = 0;
        var m = TagAddressPattern.Match(text.Trim());
        if (!m.Success) return false;
        area = m.Groups["area"].Value.ToUpperInvariant() is "I" or "E" ? "I" : "Q";
        var start = int.Parse(m.Groups["byte"].Value, CultureInfo.InvariantCulture) * 8;
        var size = m.Groups["size"].Value.ToUpperInvariant();
        var rest = m.Groups["rest"].Value;
        if (rest.StartsWith(".", StringComparison.Ordinal))
        {
            if (size != "" && size != "X") return false;
            firstBit = start + (rest[1] - '0');
            endBit = firstBit + 1;
            return true;
        }
        var width = size switch { "B" => 8, "W" => 16, "D" => 32, "L" => 64, _ => 0 };
        if (width == 0) return false;
        firstBit = start;
        endBit = start + width;
        return true;
    }

    // Same notation as before (mnemonic, size letter, bit, :P), only the byte number moves.
    private static string ShiftTagAddress(string text, int deltaBytes)
    {
        var m = TagAddressPattern.Match(text.Trim());
        var newByte = int.Parse(m.Groups["byte"].Value, CultureInfo.InvariantCulture) + deltaBytes;
        return $"{m.Groups["pre"].Value}{m.Groups["area"].Value}{m.Groups["size"].Value}{newByte}{m.Groups["rest"].Value}";
    }
}
