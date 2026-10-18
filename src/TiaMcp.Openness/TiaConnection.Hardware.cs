using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Cax;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.MC.Drives;
using Siemens.Engineering.Online;
using Siemens.Engineering.SW;

namespace TiaMcp.Openness;

/// <summary>
/// Read-only access to the hardware configuration: devices, modules with order number, firmware
/// and I/O addresses, network interfaces (IP, PROFINET name, IO system), and AML export.
/// </summary>
public sealed partial class TiaConnection
{
    public IReadOnlyList<HwDevice> ReadHardware(string? deviceFilter = null)
    {
        EnsureConnected();
        // Which PLC an address belongs to: the address controller (CPU) it is registered with.
        var plcByCpu = new Dictionary<object, string>();
        foreach (var pair in PlcCpuItems())
        {
            plcByCpu[pair.Value] = pair.Key;
            foreach (var controller in AddressControllersIn(pair.Value)) plcByCpu[controller] = pair.Key;
        }

        var result = new List<HwDevice>();
        foreach (var device in AllDevices())
        {
            if (!string.IsNullOrEmpty(deviceFilter) && device.Name.IndexOf(deviceFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            result.Add(ReadDevice(device, plcByCpu));
        }
        return result;
    }

    // The device item that holds each PLC software (the CPU), by PLC name.
    private Dictionary<string, DeviceItem> PlcCpuItems()
    {
        var result = new Dictionary<string, DeviceItem>(StringComparer.OrdinalIgnoreCase);
        void Walk(DeviceItemComposition items)
        {
            foreach (DeviceItem item in items)
            {
                if (item.GetService<SoftwareContainer>()?.Software is PlcSoftware plc && !result.ContainsKey(plc.Name)) result[plc.Name] = item;
                Walk(item.DeviceItems);
            }
        }
        foreach (var device in AllDevices()) Walk(device.DeviceItems);
        return result;
    }

    private bool IsPlcOnline(string plcName)
    {
        if (!PlcCpuItems().TryGetValue(plcName, out var cpu)) return false;
        try
        {
            var state = cpu.GetService<OnlineProvider>()?.State;
            return state == OnlineState.Online || state == OnlineState.Connecting;
        }
        catch
        {
            return false;
        }
    }

    // Stations are in project.Devices, IO devices not yet assigned to a station (typical for
    // ET 200 stations of an IO system) in the ungrouped devices group, others in user folders.
    private IEnumerable<Device> AllDevices()
    {
        var seen = new List<Device>();
        IEnumerable<Device> FromGroups(DeviceUserGroupComposition groups)
        {
            foreach (DeviceUserGroup group in groups)
            {
                foreach (Device d in group.Devices) yield return d;
                foreach (var d in FromGroups(group.Groups)) yield return d;
            }
        }
        var all = _project!.Devices.Cast<Device>()
            .Concat(SafeEnum(() => _project.UngroupedDevicesGroup.Devices.Cast<Device>()))
            .Concat(SafeEnum(() => FromGroups(_project.DeviceGroups)));
        foreach (var device in all)
        {
            if (seen.Any(s => s.Equals(device))) continue;
            seen.Add(device);
            yield return device;
        }
    }

    private static IEnumerable<AddressController> AddressControllersIn(DeviceItem item)
    {
        AddressController? own;
        try { own = item.GetService<AddressController>(); } catch { own = null; }
        if (own != null) yield return own;
        foreach (DeviceItem child in item.DeviceItems)
            foreach (var controller in AddressControllersIn(child)) yield return controller;
    }

    private static IEnumerable<DeviceItem> ByPosition(DeviceItemComposition items) =>
        items.Cast<DeviceItem>().Select(i => (Item: i, Position: SafePosition(i))).OrderBy(x => x.Position).Select(x => x.Item).ToList();

    private static int SafePosition(DeviceItem item)
    {
        try { return item.PositionNumber; }
        catch { return int.MaxValue; }
    }

    private static IEnumerable<T> SafeEnum<T>(Func<IEnumerable<T>> get)
    {
        try { return get().ToList(); }
        catch { return Array.Empty<T>(); }
    }

    private HwDevice ReadDevice(Device device, Dictionary<object, string> plcByCpu)
    {
        var modules = new List<HwModule>();
        var network = new List<HwNode>();
        string? plcSoftware = null;
        var seen = new List<Address>();
        foreach (var item in ByPosition(device.DeviceItems))
            WalkDeviceItem(item, "", 0, modules, network, plcByCpu, seen, ref plcSoftware);

        var owner = plcSoftware
            ?? modules.SelectMany(m => m.Addresses).Select(a => a.Plc).FirstOrDefault(p => p != null)
            ?? network.Select(n => n.ControllerPlc).FirstOrDefault(p => p != null);
        return new HwDevice(device.Name, HwAttr(device.GetAttribute, "TypeName"), device.TypeIdentifier,
            SafeBool(() => device.IsGsd), owner, plcSoftware, network, modules);
    }

    private void WalkDeviceItem(DeviceItem item, string parentPath, int depth, List<HwModule> modules, List<HwNode> network,
        Dictionary<object, string> plcByCpu, List<Address> seen, ref string? plcSoftware)
    {
        var path = string.IsNullOrEmpty(parentPath) ? item.Name : $"{parentPath}/{item.Name}";
        if (plcSoftware == null && item.GetService<SoftwareContainer>()?.Software is PlcSoftware plc) plcSoftware = plc.Name;

        var slots = AddressSlotsOf(item, path, seen);
        var addresses = slots.Where(x => x.Path == path)
            .Select(x => new HwAddress(x.Area, x.Address.StartAddress, x.Address.Length, OwnerOf(x.Address, plcByCpu))).ToList();

        var nodes = ReadNetworkInterface(item, plcByCpu);
        network.AddRange(nodes);

        var orderNumber = HwAttr(item.GetAttribute, "OrderNumber");
        if (addresses.Count > 0 || !string.IsNullOrEmpty(orderNumber) || nodes.Count > 0)
        {
            var position = SafePosition(item);
            if (position == int.MaxValue) position = -1;
            modules.Add(new HwModule(path, depth, position, item.Name, orderNumber,
                HwAttr(item.GetAttribute, "FirmwareVersion"), HwAttr(item.GetAttribute, "TypeName"), addresses,
                slots.Any(x => x.Path == path && x.Failsafe)));
        }
        // Drive telegrams and transfer areas (I-device, PN/PN coupler) carry their own addresses.
        foreach (var group in slots.Where(x => x.Path != path).GroupBy(x => x.Path))
        {
            modules.Add(new HwModule(group.Key, depth + 1, -1, group.Key.Substring(path.Length + 1), null, null, null,
                group.Select(x => new HwAddress(x.Area, x.Address.StartAddress, x.Address.Length, OwnerOf(x.Address, plcByCpu))).ToList(),
                group.Any(x => x.Failsafe)));
        }

        foreach (var child in ByPosition(item.DeviceItems))
            WalkDeviceItem(child, path, depth + 1, modules, network, plcByCpu, seen, ref plcSoftware);
    }

    // One I/Q address range of a hardware item: its own addresses (path = the item's path), a drive
    // telegram ('<path>/Telegram <n>') or a transfer area ('<path>/Transfer area <name>').
    private sealed class AddressSlot
    {
        public AddressSlot(string path, string area, Address address, bool failsafe) { Path = path; Area = area; Address = address; Failsafe = failsafe; }
        public string Path { get; }
        public string Area { get; }
        public Address Address { get; }
        public bool Failsafe { get; }
    }

    // seen: addresses already listed for this device - the drive objects of a Startdrive device are
    // reachable from several of its items, and each would list the same telegrams again.
    private static List<AddressSlot> AddressSlotsOf(DeviceItem item, string path, List<Address> seen)
    {
        var result = new List<AddressSlot>();
        void Add(string slotPath, AddressComposition? addresses, bool failsafe)
        {
            if (addresses == null) return;
            foreach (Address address in addresses)
            {
                if (address.IoType != AddressIoType.Input && address.IoType != AddressIoType.Output) continue;
                if (address.Length <= 0) continue;
                if (seen.Any(a => a.Equals(address))) continue;
                seen.Add(address);
                result.Add(new AddressSlot(slotPath, address.IoType == AddressIoType.Input ? "I" : "Q", address, failsafe));
            }
        }

        // An F-module exposes its PROFIsafe addresses as attributes.
        var itemFailsafe = HwAttr(item.GetAttribute, "Failsafe_FSourceAddress") != null;
        try { Add(path, item.Addresses, itemFailsafe); } catch { /* racks, ports, ... */ }

        try
        {
            foreach (var (name, addresses, safety) in DriveTelegrams(item))
                Add($"{path}/{name}", addresses, safety);
        }
        catch
        {
            // no Startdrive object, or Startdrive's Openness assembly not installed
        }

        try
        {
            var ni = item.GetService<NetworkInterface>();
            if (ni != null)
            {
                foreach (TransferArea area in ni.TransferAreas)
                {
                    var safety = area.Type.ToString().IndexOf("F_", StringComparison.Ordinal) >= 0 || area.Type.ToString().IndexOf("PROFISAFE", StringComparison.Ordinal) >= 0;
                    Add($"{path}/Transfer area {area.Name}", area.LocalAddresses, safety);
                }
            }
        }
        catch
        {
            // no transfer areas
        }
        return result;
    }

    // Separate method: it is the only one touching Startdrive types, so a missing
    // Siemens.Engineering.Startdrive assembly fails here (caught) instead of in the caller's JIT.
    private static List<(string Name, AddressComposition Addresses, bool Safety)> DriveTelegrams(DeviceItem item)
    {
        var result = new List<(string, AddressComposition, bool)>();
        var drives = item.GetService<DriveObjectContainer>();
        if (drives == null) return result;
        foreach (DriveObject drive in drives.DriveObjects)
            foreach (Telegram telegram in drive.Telegrams)
                result.Add(($"Telegram {telegram.TelegramNumber}", telegram.Addresses,
                    telegram.Type.ToString().IndexOf("Safety", StringComparison.OrdinalIgnoreCase) >= 0));
        return result;
    }

    private static string? OwnerOf(Address address, Dictionary<object, string> plcByCpu)
    {
        try
        {
            foreach (AddressController controller in address.AddressControllers)
                if (plcByCpu.TryGetValue(controller, out var name)) return name;
        }
        catch
        {
            // no controller association
        }
        return null;
    }

    private List<HwNode> ReadNetworkInterface(DeviceItem item, Dictionary<object, string> plcByCpu)
    {
        var result = new List<HwNode>();
        NetworkInterface? ni;
        try { ni = item.GetService<NetworkInterface>(); } catch { ni = null; }
        if (ni == null) return result;

        string? ioSystem = null, role = null, controllerPlc = null;
        int? deviceNumber = null;
        try
        {
            foreach (IoController controller in ni.IoControllers)
            {
                if (controller.IoSystem == null) continue;
                ioSystem = $"{controller.IoSystem.Name} ({controller.IoSystem.Number})";
                role = "IO controller";
            }
            foreach (IoConnector connector in ni.IoConnectors)
            {
                var system = connector.ConnectedToIoSystem;
                if (system == null) continue;
                ioSystem = $"{system.Name} ({system.Number})";
                role = role == null ? "IO device" : role + " + IO device";
                deviceNumber = HwAttr(connector.GetAttribute, "PnDeviceNumber") is { } n && int.TryParse(n, out var number) ? number : null;
                controllerPlc = ControllerPlcOf(connector, plcByCpu);
            }
        }
        catch
        {
            // interface without IO system support
        }

        foreach (Node node in ni.Nodes)
        {
            string? subnet;
            try { subnet = node.ConnectedSubnet?.Name; } catch { subnet = null; }
            result.Add(new HwNode(item.Name, node.NodeType.ToString(), subnet,
                HwAttr(node.GetAttribute, "Address"), HwAttr(node.GetAttribute, "PnDeviceName"),
                ioSystem, role, deviceNumber, controllerPlc));
        }
        return result;
    }

    private static string? ControllerPlcOf(IoConnector connector, Dictionary<object, string> plcByCpu)
    {
        try
        {
            var controller = connector.GetIoController();
            for (IEngineeringObject? parent = controller?.Parent; parent != null; parent = parent.Parent)
            {
                if (plcByCpu.TryGetValue(parent, out var name)) return name;
                if (parent is Device) break;
            }
        }
        catch
        {
            // not connected
        }
        return null;
    }

    private static string? HwAttr(Func<string, object> get, string name)
    {
        try
        {
            var value = get(name);
            return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static bool SafeBool(Func<bool> get)
    {
        try { return get(); }
        catch { return false; }
    }

    /// <summary>Exports the hardware (one device, or the whole project) as AutomationML via TIA Portal's CAx export.</summary>
    public AmlExportResult ExportHardwareAml(string targetFile, string? deviceName)
    {
        EnsureConnected();
        var cax = _project!.GetService<CaxProvider>()
            ?? throw new InvalidOperationException("TIA Portal offers no CAx export for this project.");
        var target = new FileInfo(targetFile);
        target.Directory?.Create();
        if (target.Exists) target.Delete();

        // The TransferResult overloads: the bool overloads with a log file returned before TIA had
        // finished (and the export then ran a second time).
        TransferResult result;
        if (string.IsNullOrEmpty(deviceName))
        {
            result = cax.Export(_project, target);
        }
        else
        {
            var device = AllDevices().FirstOrDefault(d => string.Equals(d.Name, deviceName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Device '{deviceName}' not found. Use list_hardware for the device names.");
            result = cax.Export(device, target);
        }

        var all = new List<string>();
        var problems = new List<string>();
        void Collect(TransferResultMessageComposition messages, int depth)
        {
            foreach (TransferResultMessage message in messages)
            {
                var line = $"{new string(' ', 2 * depth)}{message.State}: {message.Message}";
                all.Add(line);
                // A parent message takes the worst state of its children; only the leaves say what went wrong.
                if ((message.State == TransferResultState.Error || message.State == TransferResultState.Warning) && message.Messages.Count == 0)
                    problems.Add(line.Trim());
                Collect(message.Messages, depth + 1);
            }
        }
        Collect(result.Messages, 0);
        var log = Path.ChangeExtension(target.FullName, ".log");
        File.WriteAllLines(log, all);

        target.Refresh();
        return new AmlExportResult(target.Exists && target.Length > 0, target.FullName, target.Exists ? target.Length : 0,
            result.State.ToString(), result.ErrorCount, result.WarningCount, problems, log);
    }
}
