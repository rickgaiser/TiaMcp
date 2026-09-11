using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Library.MasterCopies;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Blocks.Interface;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HmiUnified.HmiTags;
using Siemens.Engineering.HmiUnified.HmiAlarm;
using Siemens.Engineering.HmiUnified.HmiAlarm.HmiAlarmCommon;

namespace TiaMcp.Openness;

/// <summary>
/// Wraps a single attached TIA Portal session. All methods here must be called on the
/// dedicated STA thread (see <see cref="StaDispatcher"/>) - this class does not manage
/// threading itself.
/// </summary>
public sealed class TiaConnection : IDisposable
{
    private TiaPortal? _tiaPortal;
    private Project? _project;
    private readonly Dictionary<string, PlcSoftware> _softwareByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HmiSoftware> _hmiSoftwareByName = new(StringComparer.OrdinalIgnoreCase);

    public bool IsConnected => _project != null;

    // Lists running TIA Portal GUI processes WITHOUT calling TiaPortalProcess.Attach() on any of
    // them - Attach() is what triggers Openness's interactive "allow external connection" dialog
    // in the TIA Portal GUI, so scanning every running instance just to disambiguate (as Connect()
    // briefly did) meant that dialog popped up on every single one of them every time, even though
    // only one instance was ever actually wanted. This uses plain OS process enumeration instead -
    // matching by executable name only, no Openness/COM involved - so it never prompts anything.
    // MainWindowTitle is the only identifying detail available this way; it's blank for a headless
    // orphan instance or one not currently showing a project window, in which case the caller has
    // to attach (via Connect's processId) to find out what's actually open in it.
    public static IReadOnlyList<TiaInstanceInfo> ListRunningInstances()
    {
        var result = new List<TiaInstanceInfo>();
        foreach (var proc in System.Diagnostics.Process.GetProcessesByName("Siemens.Automation.Portal"))
        {
            try
            {
                string? title;
                try { title = string.IsNullOrEmpty(proc.MainWindowTitle) ? null : proc.MainWindowTitle; }
                catch { title = null; }
                result.Add(new TiaInstanceInfo(proc.Id, title));
            }
            finally
            {
                proc.Dispose();
            }
        }
        return result;
    }

    public ConnectResult Connect(string? projectPath = null, int? processId = null)
    {
        var processes = TiaPortal.GetProcesses().ToList();
        if (processes.Count == 0)
        {
            return new ConnectResult(false, null, null, "No running TIA Portal instance found. Open TIA Portal first (with or without a project loaded).");
        }

        if (processId.HasValue)
        {
            return ConnectToSpecificProcess(processes, processId.Value, projectPath);
        }

        // TiaPortal.GetProcesses() has no notion of which running instance the caller means - its
        // order reflects OS process enumeration, not launch order or anything project-aware.
        // Picking .First() blindly could silently attach to the wrong instance; attaching to
        // every instance to figure out which one is right is correct but triggers Openness's
        // "allow connection" dialog on every single running instance, every time. With more than
        // one instance running, refuse to guess or scan: send the caller to ListRunningInstances
        // (zero-prompt) to pick a processId first.
        if (processes.Count > 1)
        {
            var pids = string.Join(", ", processes.Select(p => p.Id));
            return new ConnectResult(false, null, null,
                $"{processes.Count} TIA Portal instances are running (PIDs: {pids}). Call list_tia_instances first, then call tia_connect again with processId set to the one you want - connecting without it would otherwise prompt every running instance for Openness access.");
        }

        return ConnectToSpecificProcess(processes, processes[0].Id, projectPath);
    }

    // Attaches to exactly one, caller-chosen running instance - the only Attach() call this makes,
    // so this is also the only path that can ever pop the Openness "allow connection" dialog, and
    // only for the instance actually wanted.
    private ConnectResult ConnectToSpecificProcess(List<TiaPortalProcess> processes, int processId, string? projectPath)
    {
        var proc = processes.FirstOrDefault(p => p.Id == processId);
        if (proc == null)
        {
            var available = string.Join(", ", processes.Select(p => p.Id));
            return new ConnectResult(false, null, null, $"No running TIA Portal instance with process id {processId}. Currently running: {available}. Call list_tia_instances to see them.");
        }

        TiaPortal portal;
        try
        {
            portal = proc.Attach();
        }
        catch (Exception ex)
        {
            return new ConnectResult(false, null, null, $"Could not attach to TIA Portal process {processId}: {ex.Message}");
        }

        Project? existing;
        try
        {
            existing = portal.Projects.FirstOrDefault();
        }
        catch
        {
            existing = null;
        }

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            if (existing == null)
            {
                portal.Dispose();
                return new ConnectResult(false, null, null, $"Attached to TIA Portal process {processId}, but no project is open in it. Pass projectPath to open one by path.");
            }
            return FinalizeConnect(portal, existing);
        }

        var fileInfo = new FileInfo(projectPath);
        if (!fileInfo.Exists)
        {
            portal.Dispose();
            return new ConnectResult(false, null, null, $"Project file not found: {projectPath}");
        }

        if (existing?.Path != null && string.Equals(existing.Path.FullName, fileInfo.FullName, StringComparison.OrdinalIgnoreCase))
        {
            return FinalizeConnect(portal, existing);
        }

        if (existing != null)
        {
            portal.Dispose();
            return new ConnectResult(false, null, null,
                $"TIA Portal process {processId} already has a different project open: '{existing.Name}' ({existing.Path?.FullName ?? "unsaved"}). Close it first (tia_connect with processId={processId}, then close_project) or pick a different processId.");
        }

        try
        {
            var opened = portal.Projects.Open(fileInfo);
            return FinalizeConnect(portal, opened);
        }
        catch (Exception ex)
        {
            portal.Dispose();
            return new ConnectResult(false, null, null, $"Failed to open project at '{projectPath}': {ex.Message}");
        }
    }

    private ConnectResult FinalizeConnect(TiaPortal portal, Project project)
    {
        _tiaPortal = portal;
        _project = project;
        _softwareByName.Clear();
        _hmiSoftwareByName.Clear();
        foreach (Device device in project.Devices)
        {
            CollectSoftware(device.DeviceItems);
        }

        return new ConnectResult(true, project.Name, project.Path?.FullName, null);
    }

    public ConnectResult SaveProjectAs(string targetDirectory)
    {
        EnsureConnected();
        try
        {
            _project!.SaveAs(new DirectoryInfo(targetDirectory));
            return new ConnectResult(true, _project.Name, _project.Path?.FullName, null);
        }
        catch (Exception ex)
        {
            return new ConnectResult(false, null, null, ex.Message);
        }
    }

    public SimpleResult SaveProject()
    {
        EnsureConnected();
        try
        {
            _project!.Save();
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    public SimpleResult CloseProject()
    {
        EnsureConnected();
        try
        {
            _project!.Close();
            _project = null;
            _softwareByName.Clear();
            _hmiSoftwareByName.Clear();
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    public ProjectStatus GetProjectStatus()
    {
        EnsureConnected();
        return new ProjectStatus(
            _project!.Name,
            _project.Path?.FullName,
            _project.IsModified,
            _project.Author,
            _project.LastModified,
            _project.LastModifiedBy,
            _project.Version);
    }

    private void CollectSoftware(DeviceItemComposition items)
    {
        foreach (DeviceItem item in items)
        {
            var container = item.GetService<SoftwareContainer>();
            if (container?.Software is PlcSoftware plcSoftware)
            {
                _softwareByName[plcSoftware.Name] = plcSoftware;
            }
            if (container?.Software is HmiSoftware hmiSoftware)
            {
                _hmiSoftwareByName[hmiSoftware.Name] = hmiSoftware;
            }

            CollectSoftware(item.DeviceItems);
        }
    }

    public IReadOnlyList<DeviceSummary> ListDevices()
    {
        EnsureConnected();
        var result = new List<DeviceSummary>();
        foreach (Device device in _project!.Devices)
        {
            CollectDeviceSummaries(device.Name, device.DeviceItems, result);
        }
        return result;
    }

    private void CollectDeviceSummaries(string deviceName, DeviceItemComposition items, List<DeviceSummary> result)
    {
        foreach (DeviceItem item in items)
        {
            var software = item.GetService<SoftwareContainer>()?.Software as PlcSoftware;
            if (software != null)
            {
                result.Add(new DeviceSummary(deviceName, item.Name, software.Name));
            }

            CollectDeviceSummaries(deviceName, item.DeviceItems, result);
        }
    }

    public IReadOnlyList<HmiDeviceSummary> ListHmiDevices()
    {
        EnsureConnected();
        var result = new List<HmiDeviceSummary>();
        foreach (Device device in _project!.Devices)
        {
            CollectHmiDeviceSummaries(device.Name, device.DeviceItems, result);
        }
        return result;
    }

    private void CollectHmiDeviceSummaries(string deviceName, DeviceItemComposition items, List<HmiDeviceSummary> result)
    {
        foreach (DeviceItem item in items)
        {
            var software = item.GetService<SoftwareContainer>()?.Software as HmiSoftware;
            if (software != null)
            {
                result.Add(new HmiDeviceSummary(deviceName, item.Name, software.Name));
            }

            CollectHmiDeviceSummaries(deviceName, item.DeviceItems, result);
        }
    }

    public IReadOnlyList<BlockSummary> ListBlocks(string plcName)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);
        var result = new List<BlockSummary>();
        WalkBlocks(plcName, software.BlockGroup.Blocks, software.BlockGroup.Groups, "", result);
        return result;
    }

    private void WalkBlocks(string plcName, PlcBlockComposition blocks, PlcBlockUserGroupComposition groups, string groupPath, List<BlockSummary> result)
    {
        foreach (PlcBlock block in blocks)
        {
            if (block.ProgrammingLanguage == ProgrammingLanguage.Undef) continue;
            result.Add(new BlockSummary(plcName, groupPath, block.Name, block.ProgrammingLanguage.ToString(), block.IsConsistent));
        }

        foreach (var group in groups)
        {
            var childPath = string.IsNullOrEmpty(groupPath) ? group.Name : $"{groupPath}/{group.Name}";
            WalkBlocks(plcName, group.Blocks, group.Groups, childPath, result);
        }
    }

    public IReadOnlyList<TypeSummary> ListPlcTypes(string plcName)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);
        var result = new List<TypeSummary>();
        WalkTypes(plcName, software.TypeGroup.Types, software.TypeGroup.Groups, "", result);
        return result;
    }

    private void WalkTypes(string plcName, PlcTypeComposition types, PlcTypeUserGroupComposition groups, string groupPath, List<TypeSummary> result)
    {
        foreach (var type in types)
        {
            result.Add(new TypeSummary(plcName, groupPath, type.Name));
        }

        foreach (var group in groups)
        {
            var childPath = string.IsNullOrEmpty(groupPath) ? group.Name : $"{groupPath}/{group.Name}";
            WalkTypes(plcName, group.Types, group.Groups, childPath, result);
        }
    }

    public ExportResult ReadBlock(string plcName, string blockName)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);
        var found = FindBlockByName(software.BlockGroup.Blocks, software.BlockGroup.Groups, blockName);
        if (found == null)
        {
            return new ExportResult(false, Array.Empty<BlockDocument>(), $"Block '{blockName}' not found in PLC '{plcName}'.");
        }

        var (block, _) = found.Value;

        if (block.ProgrammingLanguage == ProgrammingLanguage.GRAPH)
        {
            return ReadGraphBlock(block);
        }

        if (block.ProgrammingLanguage == ProgrammingLanguage.STL)
        {
            return ReadPureStlBlock(software, block);
        }

        var outDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "TiaMcp", "export", Guid.NewGuid().ToString("N")));
        outDir.Create();
        try
        {
            DocumentExportResult? result = null;
            string? exportError = null;
            try
            {
                result = block.ExportAsDocuments(outDir, block.Name);
                if (result.State == DocumentResultState.Failure)
                {
                    exportError = string.Join("; ", result.Messages.Select(m => m.Message));
                    result = null;
                }
            }
            catch (Exception ex)
            {
                exportError = ex.Message;
            }

            // A block whose primary language is FBD/LAD/SCL but that contains one or more
            // embedded STL networks fails the whole-block ExportAsDocuments call with this exact
            // signature - not a corner case, 26 real blocks in one real project hit this. Fall
            // back to the relay technique: strip the STL network(s), export the rest through
            // this same, already-working route via a disposable temp block, and render the STL
            // network(s) separately (see ReadMixedStlBlock's doc comment for the full mechanism).
            if (exportError != null && exportError.Contains("cannot be exported to or imported from SIMATIC SD"))
            {
                return ReadMixedStlBlock(software, block);
            }

            var docs = result?.ExportedDocuments
                .Select(f => new BlockDocument(f.Name, File.ReadAllText(f.FullName)))
                .ToList() ?? new List<BlockDocument>();

            // ExportAsDocuments (the plain .s7dcl/.s7res route, no STL involved here) can itself
            // silently drop a network's S7_NetworkTitle/S7_NetworkComment even on a direct,
            // non-relayed export: one real FB lost every one of its 13 network titles, another
            // lost about half of its 24 and additionally emitted the SAME multilingual-text id
            // for two different networks' titles (both bugs verified by comparing
            // ExportAsDocuments's own .s7dcl/.s7res against a raw XML Export() of the same block,
            // which does carry every title/comment correctly). This isn't specific to the
            // STL-relay path - reuse the same
            // ExtractCompileUnitTexts/RebuildDclWithNetworkTitles patching that path already does
            // for its surviving networks, best-effort, so a plain successful read doesn't quietly
            // ship with titles/comments the GUI shows but the export lost.
            try { PatchMissingNetworkTitles(block, docs); } catch { /* best effort - leave docs as ExportAsDocuments produced them */ }

            // Instance-DBs only export a bare linkage line (the interface lives on the FB
            // type, which the SIMATIC-SD export route can fail to reach entirely - e.g. if
            // the FB contains an STL network). Read the resolved interface directly off the
            // instance instead - this is a live object-model property, not tied to that
            // export route at all, so it works even when the FB export itself is broken.
            if (block is InstanceDB idb)
            {
                docs.Add(new BlockDocument($"{block.Name}.interface.txt", FormatInstanceInterface(idb)));
            }

            if (docs.Count == 0)
            {
                return new ExportResult(false, Array.Empty<BlockDocument>(), exportError ?? "Export produced no documents.");
            }

            return new ExportResult(true, docs, null);
        }
        finally
        {
            try { outDir.Delete(true); } catch { /* best effort cleanup */ }
        }
    }

    // Cross-checks a plain, already-successful ExportAsDocuments read against a raw XML Export()
    // of the same block, and patches any network title/comment the former dropped - see the call
    // site's comment for why this is needed even outside the STL-relay path. Mutates docs in
    // place; throws on any unexpected shape so the caller's best-effort catch leaves docs as they
    // were (a failed patch attempt must never be worse than no patch attempt).
    private static void PatchMissingNetworkTitles(PlcBlock block, List<BlockDocument> docs)
    {
        var dclIndex = docs.FindIndex(d => d.FileName.EndsWith(".s7dcl", StringComparison.OrdinalIgnoreCase));
        if (dclIndex < 0) return;

        var outDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "TiaMcp", "titlecheck", Guid.NewGuid().ToString("N")));
        outDir.Create();
        try
        {
            var xmlFile = new FileInfo(Path.Combine(outDir.FullName, block.Name + ".xml"));
            block.Export(xmlFile, ExportOptions.WithDefaults);
            var xdoc = XDocument.Load(xmlFile.FullName);
            var compileUnits = xdoc.Descendants("SW.Blocks.CompileUnit").ToList();
            if (compileUnits.Count == 0) return;

            var allNetworkInfos = compileUnits
                .Select((cu, i) => new StlNetworkInfo(i, false, ExtractCompileUnitTexts(cu, "Title"), ExtractCompileUnitTexts(cu, "Comment")))
                .ToList();
            if (allNetworkInfos.All(n => n.Title.Count == 0 && n.Comment.Count == 0)) return;

            var resIndex = docs.FindIndex(d => d.FileName.EndsWith(".s7res", StringComparison.OrdinalIgnoreCase));
            var (newDcl, newRes) = RebuildDclWithNetworkTitles(docs[dclIndex].Content, resIndex >= 0 ? docs[resIndex].Content : null, allNetworkInfos);

            docs[dclIndex] = docs[dclIndex] with { Content = newDcl };
            if (resIndex >= 0) docs[resIndex] = docs[resIndex] with { Content = newRes };
            else docs.Add(new BlockDocument($"{block.Name}.s7res", newRes));
        }
        finally
        {
            try { outDir.Delete(true); } catch { /* best effort cleanup */ }
        }
    }

    // GRAPH (S7-GRAPH/SFC) blocks can never be read via ExportAsDocuments (the .s7dcl/.s7res
    // route every other language uses) - TIA Portal fails that call with a misleading
    // "know-how-protected" error regardless of actual protection state; the real cause is that
    // route simply doesn't support GRAPH's language. The only working mechanism is this
    // different Siemens API - PlcBlock.Export(FileInfo, ExportOptions) producing a single XML
    // document - which additionally requires the block to be consistent (compiled) first.
    private ExportResult ReadGraphBlock(PlcBlock block)
    {
        if (!block.IsConsistent)
        {
            return new ExportResult(false, Array.Empty<BlockDocument>(),
                $"Block '{block.Name}' is a GRAPH block and is not consistent. GRAPH export requires a consistent/compiled block - call compile first, then read_block again.");
        }

        var outDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "TiaMcp", "export", Guid.NewGuid().ToString("N")));
        outDir.Create();
        try
        {
            var outFile = new FileInfo(Path.Combine(outDir.FullName, block.Name + ".xml"));
            try
            {
                block.Export(outFile, ExportOptions.WithDefaults);
            }
            catch (Exception ex)
            {
                return new ExportResult(false, Array.Empty<BlockDocument>(), $"GRAPH export failed: {ex.Message}");
            }

            string xml;
            try
            {
                xml = File.ReadAllText(outFile.FullName);
            }
            catch (Exception ex)
            {
                return new ExportResult(false, Array.Empty<BlockDocument>(), $"Could not read exported GRAPH XML: {ex.Message}");
            }

            string il;
            try
            {
                il = GraphConverter.XmlToIL(xml);
            }
            catch (Exception ex)
            {
                return new ExportResult(false, Array.Empty<BlockDocument>(),
                    $"GRAPH XML export succeeded but conversion to the readable intermediate language failed: {ex.GetType().Name}: {ex.Message}");
            }

            var doc = new BlockDocument($"{block.Name}.graph.il", il);
            return new ExportResult(true, new[] { doc }, null);
        }
        finally
        {
            try { outDir.Delete(true); } catch { /* best effort cleanup */ }
        }
    }

    // A block whose declared language is STL (the whole block, not just one embedded network)
    // fails ExportAsDocuments identically to a mixed block. Rather than the raw single-XML
    // Export() route (which hands back TIA's internal block-XML schema - complete and lossless,
    // but not actual STL/AWL text), this uses PlcSoftware.ExternalSourceGroup.GenerateSource -
    // the Openness equivalent of the GUI's "Generate source" command - which writes TIA's own
    // real AWL mnemonic text straight to disk: genuine "FUNCTION ... NETWORK ... NOP 0; CLR; ...
    // END_FUNCTION" text, not XML. No fallback to the XML route - if GenerateSource fails, that's
    // surfaced directly rather than silently degrading to a different, less readable format.
    private ExportResult ReadPureStlBlock(PlcSoftware software, PlcBlock block)
    {
        if (!block.IsConsistent)
        {
            return new ExportResult(false, Array.Empty<BlockDocument>(),
                $"Block '{block.Name}' is not consistent. Reading an STL block requires a consistent/compiled block - call compile first, then read_block again.");
        }

        var outDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "TiaMcp", "export-stl", Guid.NewGuid().ToString("N")));
        outDir.Create();
        try
        {
            var outFile = new FileInfo(Path.Combine(outDir.FullName, block.Name + ".awl"));
            try
            {
                software.ExternalSourceGroup.GenerateSource(new IGenerateSource[] { block }, outFile);
            }
            catch (Exception ex)
            {
                return new ExportResult(false, Array.Empty<BlockDocument>(), $"STL source generation failed: {ex.Message}");
            }

            var doc = new BlockDocument($"{block.Name}.awl", File.ReadAllText(outFile.FullName));
            return new ExportResult(true, new[] { doc }, null);
        }
        finally
        {
            try { outDir.Delete(true); } catch { /* best effort cleanup */ }
        }
    }

    // A block whose primary language is FBD/LAD/SCL but contains one or more embedded STL
    // networks can't be read via ExportAsDocuments at all - not because of anything about the
    // FBD/LAD/SCL content, but because TIA's SIMATIC-SD converter refuses the whole block if ANY
    // network inside is STL. There is no per-network export API (no CompileUnit/Network object
    // exists anywhere in the Openness object model), so the only way to get the well-understood,
    // already-working .s7dcl rendering for the non-STL networks is this relay: strip the STL
    // CompileUnit(s) out of the XML export, import the result as a disposable temp block in a
    // disposable scratch group (now valid - no STL left), run the EXISTING ExportAsDocuments path
    // against that temp block, then append the STL network(s) back as their own raw, complete XML
    // (filtered from the same original export - a lossless subset, not a translated rendering).
    // The temp group (and the block inside it) is deleted immediately after, leaving zero trace
    // in the project. No compile is needed anywhere in this path.
    private ExportResult ReadMixedStlBlock(PlcSoftware software, PlcBlock block)
    {
        var outDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "TiaMcp", "export-stl-relay", Guid.NewGuid().ToString("N")));
        outDir.Create();
        PlcBlockUserGroup? relayGroup = null;
        try
        {
            var xmlFile = new FileInfo(Path.Combine(outDir.FullName, block.Name + ".xml"));
            try
            {
                block.Export(xmlFile, ExportOptions.WithDefaults);
            }
            catch (Exception ex)
            {
                return new ExportResult(false, Array.Empty<BlockDocument>(),
                    $"Could not export the block via the XML route to relay around its embedded STL network(s): {ex.Message}");
            }

            var xdoc = XDocument.Load(xmlFile.FullName);
            var compileUnits = xdoc.Descendants("SW.Blocks.CompileUnit").ToList();
            var isStlFlags = compileUnits
                .Select(cu => (string?)cu.Element("AttributeList")?.Element("ProgrammingLanguage") == "STL")
                .ToList();
            var stlUnits = compileUnits.Where((_, i) => isStlFlags[i]).ToList();

            if (stlUnits.Count == 0)
            {
                return new ExportResult(false, Array.Empty<BlockDocument>(),
                    "Export failed with the STL-network signature, but no STL network was found in the XML export - this block may need to be read manually in the TIA Portal GUI.");
            }

            // Capture every network's title/comment - the only part of an STL network that
            // .s7dcl/.s7res CAN represent - and its position among ALL networks (STL and non-STL) in
            // original document order, before the STL CompileUnits are stripped below. This is
            // captured for non-STL networks too, not just STL ones: the relay reimport (importing
            // the XML with only the STL units removed, then ExportAsDocuments-ing
            // that as a temp block) does NOT reliably carry a surviving network's own
            // S7_NetworkTitle/S7_NetworkComment pragma into the relayed .s7dcl the way a direct,
            // non-relayed export of the same network would - so those need patching back in from this
            // same original export too. RebuildDclWithNetworkTitles uses this to splice a placeholder
            // network back in for each STL network (at the exact slot the real network occupies, so
            // "Network N" numbering/order matches the TIA Portal GUI - the STL network's actual logic
            // still only lives in '<name>.stl-networks.xml', see that method's doc comment for why the
            // logic itself can't follow) and to patch the title/comment pragma back onto each
            // surviving network's own relayed text where the relay dropped it.
            var allNetworkInfos = new List<StlNetworkInfo>();
            for (int i = 0; i < compileUnits.Count; i++)
            {
                allNetworkInfos.Add(new StlNetworkInfo(
                    i,
                    isStlFlags[i],
                    ExtractCompileUnitTexts(compileUnits[i], "Title"),
                    ExtractCompileUnitTexts(compileUnits[i], "Comment")));
            }

            // Snapshot the STL network(s) as raw, complete XML before stripping them out below -
            // a full clone of the export with every non-STL CompileUnit removed, so the STL
            // networks keep their real surrounding structure/attributes rather than being
            // reduced to a lossy text rendering.
            var stlXml = new XDocument(xdoc);
            foreach (var unit in stlXml.Descendants("SW.Blocks.CompileUnit").ToList())
            {
                var isStl = (string?)unit.Element("AttributeList")?.Element("ProgrammingLanguage") == "STL";
                if (!isStl) unit.Remove();
            }

            foreach (var unit in stlUnits)
            {
                unit.Remove();
            }

            var relayId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var tempName = "TiaMcpReadRelay_" + relayId;

            var nameEl = xdoc.Descendants("Name").FirstOrDefault();
            if (nameEl != null) nameEl.Value = tempName;
            var autoNumEl = xdoc.Descendants("AutoNumber").FirstOrDefault();
            if (autoNumEl != null) autoNumEl.Value = "true";

            var strippedFile = new FileInfo(Path.Combine(outDir.FullName, tempName + ".xml"));
            xdoc.Save(strippedFile.FullName);

            relayGroup = software.BlockGroup.Groups.Create("TiaMcpReadRelay_" + relayId);
            relayGroup.Blocks.Import(strippedFile, ImportOptions.Override);

            var tempBlock = relayGroup.Blocks.OfType<PlcBlock>().FirstOrDefault(b => b.Name == tempName);
            if (tempBlock == null)
            {
                return new ExportResult(false, Array.Empty<BlockDocument>(), "Relay import succeeded but the temporary block could not be found afterward.");
            }

            var relayExportDir = new DirectoryInfo(Path.Combine(outDir.FullName, "relay-export"));
            relayExportDir.Create();
            var relayResult = tempBlock.ExportAsDocuments(relayExportDir, tempBlock.Name);
            if (relayResult.State == DocumentResultState.Failure)
            {
                var msg = string.Join("; ", relayResult.Messages.Select(m => m.Message));
                return new ExportResult(false, Array.Empty<BlockDocument>(), $"Relay export (after removing the STL network(s)) failed unexpectedly: {msg}");
            }

            var docs = relayResult.ExportedDocuments
                .Select(f => new BlockDocument(
                    f.Name.Replace(tempName, block.Name),
                    File.ReadAllText(f.FullName).Replace(tempName, block.Name)))
                .ToList();

            // Patching titles/comments back in (below) is a best-effort improvement on top of an
            // already-successful relay read, not something that should be able to fail the whole
            // read - if the .s7dcl has some shape this text-splicing doesn't handle, fall back to the
            // plain relayed .s7dcl/.s7res (every network's title/comment then stays available only in
            // '<name>.stl-networks.xml', same as before this patching existed) rather than losing the
            // read entirely.
            try
            {
                var dclIndex = docs.FindIndex(d => d.FileName.EndsWith(".s7dcl", StringComparison.OrdinalIgnoreCase));
                if (dclIndex >= 0)
                {
                    var resIndex = docs.FindIndex(d => d.FileName.EndsWith(".s7res", StringComparison.OrdinalIgnoreCase));
                    var (newDcl, newRes) = RebuildDclWithNetworkTitles(
                        docs[dclIndex].Content,
                        resIndex >= 0 ? docs[resIndex].Content : null,
                        allNetworkInfos);

                    docs[dclIndex] = docs[dclIndex] with { Content = newDcl };
                    if (resIndex >= 0) docs[resIndex] = docs[resIndex] with { Content = newRes };
                    else docs.Add(new BlockDocument($"{block.Name}.s7res", newRes));
                }
            }
            catch { /* best effort - leave the plain relayed .s7dcl/.s7res as they were */ }

            // Each network's title/comment now also lives in .s7dcl/.s7res (above, best effort) so it
            // reads consistently across the whole block; only the STL instructions themselves - which
            // .s7dcl has no notation for - live in the sidecar below, as either real AWL text (tried
            // first) or, failing that, raw XML (see TryGenerateStlNetworksAwl's doc comment for the
            // mechanism and why the fallback exists). Either way it's labeled with the same "Network N"
            // overall position used in the .s7dcl placeholder/TIA Portal GUI, since GenerateSource's
            // own AWL output has no notion of a network's position among the block's OTHER (non-STL)
            // networks - without that label, a genuinely mixed block (some STL, some FBD/LAD networks)
            // would give a reader no way to interleave the two sides back into GUI network order.
            var stlOnlyInfos = allNetworkInfos.Where(n => n.IsStl).ToList();
            var awlDoc = TryGenerateStlNetworksAwl(software, block.Name, stlXml, stlOnlyInfos, outDir, relayId);
            if (awlDoc != null)
            {
                docs.Add(awlDoc);
            }
            else
            {
                // Best effort - a navigation aid on top of the (already correct) raw XML, not
                // something that should be able to fail the read.
                try
                {
                    AnnotateStlNetworksXml(stlXml, stlOnlyInfos);
                }
                catch { /* best effort - leave the sidecar XML unlabeled */ }
                docs.Add(new BlockDocument($"{block.Name}.stl-networks.xml", stlXml.ToString()));
            }

            return new ExportResult(true, docs, null);
        }
        finally
        {
            if (relayGroup != null)
            {
                try { relayGroup.Delete(); } catch { /* best effort cleanup */ }
            }
            try { outDir.Delete(true); } catch { /* best effort cleanup */ }
        }
    }

    // Second relay on top of ReadMixedStlBlock's primary one: renders the already-isolated
    // STL-only stlXml through TIA's own GenerateSource converter (the same one ReadPureStlBlock
    // uses for whole-block STL) to get real AWL mnemonic text instead of raw XML - by building a
    // SYNTHETIC whole-block-STL clone out of just the embedded STL networks. GenerateSource's
    // "must be SCL or STL" check reads the block's own top-level ProgrammingLanguage attribute,
    // not each network's own copy - a mixed block carries its primary language there (e.g. FBD)
    // even when every remaining network, after stripping, is STL - so this rewrites that one
    // attribute to STL. SetENOAutomatically - an FBD/LAD-only attribute, invalid on a block
    // declared STL - is dropped for the same reason (Import() rejects it otherwise with an
    // explicit "attribute ... is not supported" error). The resulting temp block needs to be
    // consistent before GenerateSource will touch it (same requirement ReadPureStlBlock has for a
    // real whole-block-STL block) - tries a per-block compile via GetService&lt;ICompilable&gt;()
    // first (cheap, doesn't touch the rest of the PLC's compile state); PlcBlock doesn't
    // statically declare ICompilable but the service resolves at runtime regardless. Tested
    // end-to-end against a real production block with 20+ embedded STL networks in an
    // FBD-declared block. Best-effort: returns null on any failure so the caller falls back to
    // the raw-XML sidecar rather than losing the read - this synthetic-block trick can plausibly
    // fail in ways the established raw-XML route never risks (an interface shape GenerateSource
    // won't accept from an STL-declared block, e.g.), so correctness always wins over the nicer
    // rendering.
    // Block-level attributes valid only on an FBD/LAD-declared block: Import() rejects each with
    // an explicit "Attribute 'X' ... is not supported" error once the block's own
    // ProgrammingLanguage is forced to STL, so each has to be stripped before import can succeed.
    private static readonly string[] FbdLadOnlyBlockAttributes = { "SetENOAutomatically", "IsIECCheckEnabled" };

    private BlockDocument? TryGenerateStlNetworksAwl(PlcSoftware software, string blockName, XDocument stlXml, List<StlNetworkInfo> stlOnlyInfos, DirectoryInfo outDir, string relayId)
    {
        PlcBlockUserGroup? awlRelayGroup = null;
        try
        {
            var awlXdoc = new XDocument(stlXml);

            var blockLangEl = awlXdoc.Descendants("ProgrammingLanguage").FirstOrDefault();
            if (blockLangEl == null) return null;
            blockLangEl.Value = "STL";

            foreach (var incompatibleName in FbdLadOnlyBlockAttributes)
            {
                foreach (var el in awlXdoc.Descendants(incompatibleName).ToList())
                {
                    el.Remove();
                }
            }

            var tempName = "TiaMcpReadRelaySTL_" + relayId;
            var nameEl = awlXdoc.Descendants("Name").FirstOrDefault();
            if (nameEl != null) nameEl.Value = tempName;
            var autoNumEl = awlXdoc.Descendants("AutoNumber").FirstOrDefault();
            if (autoNumEl != null) autoNumEl.Value = "true";

            var strippedFile = new FileInfo(Path.Combine(outDir.FullName, tempName + ".xml"));
            awlXdoc.Save(strippedFile.FullName);

            awlRelayGroup = software.BlockGroup.Groups.Create("TiaMcpReadRelaySTL_" + relayId);
            awlRelayGroup.Blocks.Import(strippedFile, ImportOptions.Override);

            var tempBlock = awlRelayGroup.Blocks.OfType<PlcBlock>().FirstOrDefault(b => b.Name == tempName);
            if (tempBlock == null) return null;

            if (!tempBlock.IsConsistent)
            {
                try
                {
                    tempBlock.GetService<ICompilable>()?.Compile();
                }
                catch { /* fall through - GenerateSource below just fails if still inconsistent */ }
            }

            var awlFile = new FileInfo(Path.Combine(outDir.FullName, tempName + ".awl"));
            software.ExternalSourceGroup.GenerateSource(new IGenerateSource[] { tempBlock }, awlFile);

            var text = File.ReadAllText(awlFile.FullName).Replace(tempName, blockName);
            text = AnnotateAwlNetworkNumbers(text, stlOnlyInfos);
            return new BlockDocument($"{blockName}.stl-networks.awl", text);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (awlRelayGroup != null)
            {
                try { awlRelayGroup.Delete(); } catch { /* best effort cleanup */ }
            }
        }
    }

    // Inserts a "// Network N [: Title]" comment line right before each "NETWORK" keyword in
    // GenerateSource's AWL output, in the same order stlOnlyInfos lists them (both derive from the
    // same original document order, never reordered by anything upstream) - the AWL text itself
    // has no notion of a network's position among the block's OTHER, non-STL networks, so without
    // this label there'd be no way to line an STL network here up with its empty placeholder slot
    // in the relayed .s7dcl (or with the TIA Portal GUI's own network numbering) in a genuinely
    // mixed block. N is 1-based, matching the GUI. Falls back to leaving a network unlabeled
    // (rather than throwing) if the count of "NETWORK" lines found doesn't match stlOnlyInfos - a
    // shape mismatch here means something upstream changed and this should degrade, not corrupt.
    private static string AnnotateAwlNetworkNumbers(string awl, List<StlNetworkInfo> stlOnlyInfos)
    {
        var lines = awl.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        var networkIndex = 0;
        foreach (var line in lines)
        {
            if (line.Trim() == "NETWORK" && networkIndex < stlOnlyInfos.Count)
            {
                var info = stlOnlyInfos[networkIndex];
                var title = info.Title.FirstOrDefault(t => t.Culture == "en-US").Text ?? info.Title.FirstOrDefault().Text;
                sb.Append("// Network ").Append(info.OrdinalAmongAll + 1)
                    .Append(string.IsNullOrEmpty(title) ? "" : ": " + title)
                    .Append('\n');
                networkIndex++;
            }
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }

    private sealed record StlNetworkInfo(int OrdinalAmongAll, bool IsStl, List<(string Culture, string Text)> Title, List<(string Culture, string Text)> Comment);

    private static List<(string Culture, string Text)> ExtractCompileUnitTexts(XElement compileUnit, string compositionName)
    {
        var result = new List<(string, string)>();
        var mlText = compileUnit.Element("ObjectList")?.Elements("MultilingualText")
            .FirstOrDefault(mt => (string?)mt.Attribute("CompositionName") == compositionName);
        var items = mlText?.Element("ObjectList")?.Elements("MultilingualTextItem") ?? Enumerable.Empty<XElement>();
        foreach (var item in items)
        {
            var culture = (string?)item.Element("AttributeList")?.Element("Culture");
            var text = (string?)item.Element("AttributeList")?.Element("Text");
            if (!string.IsNullOrEmpty(culture) && !string.IsNullOrEmpty(text))
                result.Add((culture!, text!));
        }
        return result;
    }

    // Matches the leading "{ S7_Language := ...; ... }" attribute block that immediately precedes
    // every top-level NETWORK in a relayed .s7dcl export - both the single-line form ("{ S7_Language
    // := "FBD" }") and the multi-line form used once more attributes (S7_NetworkComment/Title) are
    // present. The \k<indent> backreference requires the same indentation before "NETWORK" as before
    // the opening "{", which is what distinguishes a network's own attribute block from an unrelated
    // one nested deeper inside a RUNG (e.g. "{ S7_Templates := ... }" before a timer call).
    private static readonly Regex NetworkStartRegex = new(
        @"(?m)^(?<indent>[ \t]*)\{(?<attrs>[^{}]*)\}[ \t]*\r?\n\k<indent>NETWORK\b",
        RegexOptions.Compiled);

    private static readonly Regex BlockEndRegex = new(@"(?m)^END_(FUNCTION_BLOCK|FUNCTION|DATA_BLOCK)\b", RegexOptions.Compiled);

    // Splices a placeholder "{ S7_Language := "STL"; S7_NetworkComment := "..."; S7_NetworkTitle :=
    // "..." } NETWORK RUNG END_RUNG END_NETWORK" block into the relayed .s7dcl at the exact position
    // each STL network occupies among ALL of the block's networks (stlNetworks carries that original
    // position), and appends the corresponding title/comment text as new MultiLingualTexts entries in
    // .s7res - the same S7_NetworkComment/S7_NetworkTitle mechanism TIA Portal itself already uses for
    // every non-STL network, just populated by hand here instead of by ExportAsDocuments. The
    // "S7_Language := "STL"" attribute (true - that network really is STL) is what tells a reader this
    // particular empty-bodied network isn't actually empty, unlike a genuinely blank FBD/LAD network.
    // The synthesized ids are prefixed "MLC_net" + a document-order index, which cannot collide with
    // TIA Portal's own short random-looking ids (e.g. "MLC_3ds").
    //
    // If the relayed .s7dcl doesn't have the expected "N leading-attribute-blocks-before-NETWORK,
    // matching N surviving (non-STL) networks" shape - the splicing assumption below - this bails out
    // and returns the .s7dcl/.s7res unchanged rather than risk mangling them; every network's
    // title/comment then simply stays available only in '<name>.stl-networks.xml' (STL networks) or
    // wherever the relay left it (surviving networks), same as before this method existed.
    private static (string Dcl, string Res) RebuildDclWithNetworkTitles(string dcl, string? res, List<StlNetworkInfo> allNetworkInfos)
    {
        var starts = NetworkStartRegex.Matches(dcl).Cast<Match>().ToList();
        var blockEndMatch = BlockEndRegex.Match(dcl);
        var tailStart = blockEndMatch.Success ? blockEndMatch.Index : dcl.Length;

        var survivingCount = allNetworkInfos.Count(n => !n.IsStl);
        if (starts.Count != survivingCount)
        {
            return (dcl, res ?? "MultiLingualTexts:\n");
        }

        var chunks = new List<string>();
        for (var i = 0; i < starts.Count; i++)
        {
            var chunkStart = starts[i].Index;
            var chunkEnd = i + 1 < starts.Count ? starts[i + 1].Index : tailStart;
            chunks.Add(dcl.Substring(chunkStart, chunkEnd - chunkStart));
        }

        var head = dcl.Substring(0, starts.Count > 0 ? starts[0].Index : tailStart);
        var tail = dcl.Substring(tailStart);
        var indent = starts.Count > 0 ? starts[0].Groups["indent"].Value : "    ";

        var resSb = new StringBuilder(string.IsNullOrEmpty(res) ? "MultiLingualTexts:\n" : res);
        if (resSb.Length > 0 && resSb[resSb.Length - 1] != '\n') resSb.Append('\n');

        var merged = new StringBuilder(head);
        var chunkIdx = 0;
        foreach (var info in allNetworkInfos)
        {
            merged.Append(info.IsStl
                ? RenderStlPlaceholderNetwork(info, indent, resSb)
                : PatchChunkNetworkTitle(chunks[chunkIdx++], info, resSb));
        }
        merged.Append(tail);

        return (merged.ToString(), resSb.ToString());
    }

    private static string RenderStlPlaceholderNetwork(StlNetworkInfo info, string indent, StringBuilder resSb)
    {
        var attrs = new List<string> { "S7_Language := \"STL\"" };

        if (info.Comment.Count > 0)
        {
            var id = $"MLC_net{info.OrdinalAmongAll}c";
            attrs.Add($"S7_NetworkComment := \"{id}\"");
            AppendResEntry(resSb, id, info.Comment);
        }
        if (info.Title.Count > 0)
        {
            var id = $"MLC_net{info.OrdinalAmongAll}t";
            attrs.Add($"S7_NetworkTitle := \"{id}\"");
            AppendResEntry(resSb, id, info.Title);
        }

        var sb = new StringBuilder();
        sb.Append(indent).Append('{').Append('\n');
        for (var i = 0; i < attrs.Count; i++)
            sb.Append(indent).Append(indent).Append(attrs[i]).Append(i < attrs.Count - 1 ? ";\n" : "\n");
        sb.Append(indent).Append('}').Append('\n');
        sb.Append(indent).Append("NETWORK").Append('\n');
        sb.Append(indent).Append(indent).Append("RUNG").Append('\n');
        sb.Append(indent).Append(indent).Append("END_RUNG").Append('\n');
        sb.Append(indent).Append("END_NETWORK").Append('\n');
        return sb.ToString();
    }

    // Patches a surviving (non-STL) network's own leading attribute block with
    // S7_NetworkTitle/S7_NetworkComment when the relay reimport dropped it - this happens even for
    // networks the relay never touched beyond carrying them through Import()/ExportAsDocuments
    // again. Only adds what's actually missing: a chunk that already carries its own
    // S7_NetworkTitle or S7_NetworkComment keeps it untouched, this only fills the gap.
    private static string PatchChunkNetworkTitle(string chunk, StlNetworkInfo info, StringBuilder resSb)
    {
        if (info.Title.Count == 0 && info.Comment.Count == 0) return chunk;

        var match = NetworkStartRegex.Match(chunk);
        if (!match.Success || match.Index != 0) return chunk;

        var attrsText = match.Groups["attrs"].Value;
        var hasTitle = attrsText.Contains("S7_NetworkTitle");
        var hasComment = attrsText.Contains("S7_NetworkComment");
        var needsTitle = !hasTitle && info.Title.Count > 0;
        var needsComment = !hasComment && info.Comment.Count > 0;
        if (!needsTitle && !needsComment) return chunk;

        var indent = match.Groups["indent"].Value;
        var attrs = attrsText.Split(';').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();

        if (needsComment)
        {
            var id = $"MLC_net{info.OrdinalAmongAll}c";
            attrs.Add($"S7_NetworkComment := \"{id}\"");
            AppendResEntry(resSb, id, info.Comment);
        }
        if (needsTitle)
        {
            var id = $"MLC_net{info.OrdinalAmongAll}t";
            attrs.Add($"S7_NetworkTitle := \"{id}\"");
            AppendResEntry(resSb, id, info.Title);
        }

        var pragma = new StringBuilder();
        pragma.Append(indent).Append('{').Append('\n');
        for (var i = 0; i < attrs.Count; i++)
            pragma.Append(indent).Append(indent).Append(attrs[i]).Append(i < attrs.Count - 1 ? ";\n" : "\n");
        pragma.Append(indent).Append('}');

        return pragma + "\n" + indent + "NETWORK" + chunk.Substring(match.Length);
    }

    private static void AppendResEntry(StringBuilder resSb, string id, List<(string Culture, string Text)> texts)
    {
        resSb.Append("  - id: ").Append(id).Append('\n');
        foreach (var (culture, text) in texts)
        {
            resSb.Append("    ").Append(culture).Append(": '").Append(text.Replace("'", "''")).Append("'\n");
        }
    }

    // Labels each STL CompileUnit left in the sidecar XML with the same network number/title the
    // relayed .s7dcl now shows for its placeholder, as an XML comment right before the CompileUnit -
    // purely a navigation aid so a network title seen in .s7dcl (or in the TIA Portal GUI) can be
    // located here; the CompileUnit XML itself is untouched.
    private static void AnnotateStlNetworksXml(XDocument stlXml, List<StlNetworkInfo> stlNetworkInfos)
    {
        var units = stlXml.Descendants("SW.Blocks.CompileUnit").ToList();
        for (var i = 0; i < units.Count && i < stlNetworkInfos.Count; i++)
        {
            var title = stlNetworkInfos[i].Title.FirstOrDefault(t => t.Culture == "en-US").Text
                ?? stlNetworkInfos[i].Title.FirstOrDefault().Text;
            var label = $" Network {stlNetworkInfos[i].OrdinalAmongAll + 1}{(string.IsNullOrEmpty(title) ? "" : ": " + title)} ";
            units[i].AddBeforeSelf(new XComment(label));
        }
    }

    private static string FormatInstanceInterface(InstanceDB idb)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Resolved interface of '{idb.Name}' (instance of '{idb.InstanceOfName}'), {idb.Interface.Members.Count} members:");
        foreach (Member member in idb.Interface.Members)
        {
            string dataType;
            try { dataType = member.GetAttribute("DataTypeName")?.ToString() ?? "?"; }
            catch { dataType = "?"; }
            sb.AppendLine($"{member.Name} : {dataType}");
        }
        return sb.ToString();
    }

    // UDTs (PlcType) export via the same ExportAsDocuments/.s7dcl route as SCL/FBD/LAD blocks,
    // producing plain "TYPE Name : STRUCT ... END_STRUCT; END_TYPE" text with no .s7res file at
    // all (UDTs carry no multilingual comment text the way blocks can), so there is no
    // duplicate-ID risk here the way there is for ImportBlockDocuments.
    public ExportResult ReadUdt(string plcName, string typeName)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);
        var found = FindTypeByName(software.TypeGroup.Types, software.TypeGroup.Groups, typeName);
        if (found == null)
        {
            return new ExportResult(false, Array.Empty<BlockDocument>(), $"UDT '{typeName}' not found in PLC '{plcName}'.");
        }

        var (type, _) = found.Value;
        var outDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "TiaMcp", "export-udt", Guid.NewGuid().ToString("N")));
        outDir.Create();
        try
        {
            Siemens.Engineering.SW.DocumentExportResult? result = null;
            string? exportError = null;
            try
            {
                result = type.ExportAsDocuments(outDir, type.Name);
                if (result.State == DocumentResultState.Failure)
                {
                    exportError = string.Join("; ", result.Messages.Select(m => m.Message));
                    result = null;
                }
            }
            catch (Exception ex)
            {
                exportError = ex.Message;
            }

            var docs = result?.ExportedDocuments
                .Select(f => new BlockDocument(f.Name, File.ReadAllText(f.FullName)))
                .ToList() ?? new List<BlockDocument>();

            if (docs.Count == 0)
            {
                return new ExportResult(false, Array.Empty<BlockDocument>(), exportError ?? "Export produced no documents.");
            }

            return new ExportResult(true, docs, null);
        }
        finally
        {
            try { outDir.Delete(true); } catch { /* best effort cleanup */ }
        }
    }

    public ImportResult WriteBlock(string plcName, string blockName, IReadOnlyList<BlockDocument> documents)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);
        var found = FindBlockByName(software.BlockGroup.Blocks, software.BlockGroup.Groups, blockName);
        if (found == null)
        {
            return new ImportResult(false, new[] { $"Block '{blockName}' not found in PLC '{plcName}'. write_block only overwrites an existing block in place; use create_block to make a new one." });
        }

        var (block, owner) = found.Value;

        if (block.ProgrammingLanguage == ProgrammingLanguage.GRAPH)
        {
            return WriteGraphBlock(software, block, owner, documents);
        }

        // A block whose declared language is STL (the whole block) round-trips as real AWL
        // mnemonic text, mirroring ReadPureStlBlock's use of GenerateSource() on the way out.
        if (block.ProgrammingLanguage == ProgrammingLanguage.STL)
        {
            return WriteStlBlock(software, block.Name, documents);
        }

        // Safety check: a .s7dcl that's missing a block's embedded STL network(s) - exactly what
        // read_block now hands back for a mixed block, since it can't
        // represent STL content in .s7dcl - imports SUCCESSFULLY via ImportFromDocuments and
        // SILENTLY DELETES the STL network(s) from the real block. Whole-block STL is handled
        // above via WriteStlBlock; a mixed FBD/LAD/SCL block with one or more embedded STL
        // networks has no such route yet (there's no per-network import API either) - refuse
        // outright here rather than risk a silent, destructive partial overwrite.
        if (IsStlInvolved(block))
        {
            return new ImportResult(false, new[]
            {
                $"Block '{block.Name}' contains one or more embedded STL networks - write_block doesn't support writing these back yet (read_block can read them, via '.stl-networks.xml'). " +
                "Writing the .s7dcl content back here would silently delete the STL network(s), since .s7dcl can't represent STL content. Edit this block's STL logic in the TIA Portal GUI instead.",
            });
        }

        return ImportBlockDocuments(owner, blockName, documents, ImportDocumentOptions.Override);
    }

    private static bool IsStlInvolved(PlcBlock block)
    {
        if (block.ProgrammingLanguage == ProgrammingLanguage.STL) return true;

        var probeDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "TiaMcp", "stl-probe", Guid.NewGuid().ToString("N")));
        probeDir.Create();
        try
        {
            var result = block.ExportAsDocuments(probeDir, block.Name);
            return result.State == DocumentResultState.Failure
                && result.Messages.Any(m => m.Message.Contains("cannot be exported to or imported from SIMATIC SD"));
        }
        catch (Exception ex)
        {
            return ex.Message.Contains("cannot be exported to or imported from SIMATIC SD");
        }
        finally
        {
            try { probeDir.Delete(true); } catch { /* best effort cleanup */ }
        }
    }

    // GRAPH write, mirroring ReadGraphBlock's use of the separate PlcBlock.Export/
    // PlcBlockComposition.Import XML API (not ImportFromDocuments). Conservative by design -
    // see GraphConverter.ILToXml's doc comment for exactly what it will and won't regenerate.
    private ImportResult WriteGraphBlock(PlcSoftware software, PlcBlock block, PlcBlockComposition owner, IReadOnlyList<BlockDocument> documents)
    {
        if (!block.IsConsistent)
        {
            return new ImportResult(false, new[] { $"Block '{block.Name}' is not consistent. GRAPH write requires a consistent/compiled block - call compile first, then try again." });
        }

        if (documents.Count == 0 || string.IsNullOrWhiteSpace(documents[0].Content))
        {
            return new ImportResult(false, new[] { "No content provided - pass the edited '<name>.graph.il' text (from read_block) as dclContent." });
        }

        var ilContent = documents[0].Content;
        var outDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "TiaMcp", "graph-write", Guid.NewGuid().ToString("N")));
        outDir.Create();
        try
        {
            var originalFile = new FileInfo(Path.Combine(outDir.FullName, block.Name + "_original.xml"));
            try
            {
                block.Export(originalFile, ExportOptions.WithDefaults);
            }
            catch (Exception ex)
            {
                return new ImportResult(false, new[] { $"Could not re-export the current GRAPH XML to compute the edit: {ex.Message}" });
            }

            string newXml;
            try
            {
                newXml = GraphConverter.ILToXml(ilContent, File.ReadAllText(originalFile.FullName));
            }
            catch (Exception ex)
            {
                return new ImportResult(false, new[] { ex.Message });
            }

            var importFile = new FileInfo(Path.Combine(outDir.FullName, block.Name + ".xml"));
            File.WriteAllText(importFile.FullName, newXml);

            try
            {
                // The target block name/identity comes from the XML's own <Name> element, which
                // ILToXml leaves unchanged - this is a true in-place overwrite of the original
                // block (same Number too), not a new block, avoiding the block-Number collision
                // risk documented in the plan's G0 findings.
                owner.Import(importFile, ImportOptions.Override);
            }
            catch (Exception ex)
            {
                return new ImportResult(false, new[] { $"GRAPH import failed: {ex.Message}" });
            }

            var messages = new List<string> { "GRAPH block updated." };

            // Given how much more can silently go wrong in hand-rolled graphical XML than in
            // text SCL/FBD, auto-recompile immediately and surface errors in the same response
            // rather than reporting success on import alone.
            try
            {
                var compileResult = software.GetService<ICompilable>().Compile();
                messages.Add($"Recompiled: {compileResult.State}, {compileResult.ErrorCount} error(s), {compileResult.WarningCount} warning(s).");
                foreach (var m in compileResult.Messages.Cast<CompilerResultMessage>().Where(m => m.State == CompilerResultState.Error))
                {
                    messages.Add($"[Error] {m.Description}");
                }
            }
            catch (Exception ex)
            {
                messages.Add($"(Import succeeded but recompile failed to run: {ex.Message})");
            }

            return new ImportResult(true, messages);
        }
        finally
        {
            try { outDir.Delete(true); } catch { /* best effort cleanup */ }
        }
    }

    // Whole-block STL create/write - the import counterpart to ReadPureStlBlock's GenerateSource()
    // export. Openness has no direct "create/overwrite this block from AWL text" API, so this
    // reproduces the GUI's own External source files workflow: write the AWL text to a temp file,
    // register it as a PlcExternalSource via CreateFromFile, then call GenerateBlocksFromSource() -
    // the Openness equivalent of the GUI's right-click "Generate blocks from source" command. If a
    // block with the name embedded in the AWL text (the FUNCTION/FUNCTION_BLOCK/DATA_BLOCK header,
    // not necessarily this method's blockName parameter) already exists anywhere in the PLC, it's
    // updated in place (same group/position); otherwise a new block is created - in targetGroup
    // when given (via the PlcBlockUserGroup-overload), else at the Program blocks root. The
    // external source registration is deleted again afterward so it doesn't linger in the
    // project's "External source files" node. Verified: in-place overwrite (both a no-op and a
    // real content-change round-trip) against a whole-block-STL scratch block; new-block creation
    // at root and in a nested group against fresh probe blocks (see CreateStlBlock).
    private ImportResult WriteStlBlock(PlcSoftware software, string blockName, IReadOnlyList<BlockDocument> documents, PlcBlockUserGroup? targetGroup = null)
    {
        if (documents.Count == 0 || string.IsNullOrWhiteSpace(documents[0].Content))
        {
            return new ImportResult(false, new[] { "No content provided - pass AWL text (the '<name>.awl' format from read_plc_block) as dclContent." });
        }

        var outDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "TiaMcp", "stl-write", Guid.NewGuid().ToString("N")));
        outDir.Create();
        try
        {
            var sourceFile = new FileInfo(Path.Combine(outDir.FullName, blockName + ".awl"));
            File.WriteAllText(sourceFile.FullName, documents[0].Content);

            var externalSourceName = "TiaMcpStlWrite_" + Guid.NewGuid().ToString("N");
            PlcExternalSource? externalSource = null;
            try
            {
                externalSource = software.ExternalSourceGroup.ExternalSources.CreateFromFile(externalSourceName, sourceFile.FullName);
                if (targetGroup != null)
                {
                    externalSource.GenerateBlocksFromSource(targetGroup, GenerateBlockOption.None);
                }
                else
                {
                    externalSource.GenerateBlocksFromSource();
                }
            }
            catch (Exception ex)
            {
                return new ImportResult(false, new[] { $"STL import failed: {ex.Message}" });
            }
            finally
            {
                try { externalSource?.Delete(); } catch { /* best effort cleanup */ }
            }

            var written = FindBlockByName(software.BlockGroup.Blocks, software.BlockGroup.Groups, blockName);
            if (written == null)
            {
                return new ImportResult(false, new[]
                {
                    $"Generate blocks from source completed, but no block named '{blockName}' exists afterward - the AWL text's FUNCTION/FUNCTION_BLOCK/DATA_BLOCK header name must match blockName exactly.",
                });
            }

            return new ImportResult(true, new[] { "STL block written." });
        }
        finally
        {
            try { outDir.Delete(true); } catch { /* best effort cleanup */ }
        }
    }

    // Brand-new whole-block-STL creation. Unlike SCL/FBD/LAD, this doesn't go through
    // ImportFromDocuments (it rejects AWL syntax outright, since it's the SCL-oriented .s7dcl
    // importer) - it reuses WriteStlBlock's GenerateBlocksFromSource route,
    // which creates a new block when none by that name exists yet rather than requiring one to
    // already be there. targetGroup is resolved from groupPath here (root maps to no group -
    // omitting the group argument to GenerateBlocksFromSource places new blocks at the Program
    // blocks root, matching the GUI's own default).
    public ImportResult CreateStlBlock(string plcName, string groupPath, string blockName, IReadOnlyList<BlockDocument> documents)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);

        var existing = FindBlockByName(software.BlockGroup.Blocks, software.BlockGroup.Groups, blockName);
        if (existing != null)
        {
            return new ImportResult(false, new[] { $"A block named '{blockName}' already exists in PLC '{plcName}' (block names are unique per-PLC, not per-group). Use write_plc_block to edit it, or pick a different name." });
        }

        PlcBlockUserGroup? targetGroup = null;
        if (!string.IsNullOrEmpty(groupPath))
        {
            targetGroup = FindGroupByPath(software.BlockGroup.Groups, groupPath);
            if (targetGroup == null)
            {
                return new ImportResult(false, new[] { $"Group '{groupPath}' not found in PLC '{plcName}'. Use create_plc_group first, or pass an empty groupPath for the root." });
            }
        }

        return WriteStlBlock(software, blockName, documents, targetGroup);
    }

    public ImportResult CreateBlock(string plcName, string groupPath, string blockName, IReadOnlyList<BlockDocument> documents)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);

        var existing = FindBlockByName(software.BlockGroup.Blocks, software.BlockGroup.Groups, blockName);
        if (existing != null)
        {
            return new ImportResult(false, new[] { $"A block named '{blockName}' already exists in PLC '{plcName}' (block names are unique per-PLC, not per-group). Use write_block to edit it, or pick a different name." });
        }

        var resolved = ResolveGroup(software, groupPath);
        if (resolved == null)
        {
            return new ImportResult(false, new[] { $"Group '{groupPath}' not found in PLC '{plcName}'. Use create_group first, or pass an empty groupPath for the root." });
        }

        return ImportBlockDocuments(resolved.Value.Blocks, blockName, documents, ImportDocumentOptions.None);
    }

    // Creating a brand-new GRAPH block from nothing isn't possible through Openness:
    // PlcBlockComposition.CreateFB(..., ProgrammingLanguage.GRAPH) is rejected outright regardless
    // of language ("The action \"Create block\" only supports the programming language
    // 'ProDiag'" - not GRAPH-specific, the same call fails for LAD too, so this method is
    // evidently restricted to ProDiag only in this Openness version), and GRAPH has no
    // ImportFromDocuments/.s7dcl route at all (see ReadGraphBlock's doc comment). The only working
    // route: clone an existing GRAPH block via the project's
    // MasterCopy mechanism (the same "copy to library, paste from library" indirection the TIA
    // Portal GUI itself uses for cross-PLC/cross-project block copies - MasterCopyComposition.
    // Create(IMasterCopySource) then PlcBlockComposition.CreateFrom(MasterCopy)), then reshape
    // the resulting real GRAPH block through the existing WriteGraphBlock/ILToXml pipeline to
    // the caller's desired step/transition layout. The template's own step/transition count is
    // irrelevant - reshaping happens unconditionally - so any existing GRAPH block in the PLC
    // works as a template, including ones with a completely different shape.
    public ImportResult CreateGraphBlock(string plcName, string groupPath, string blockName, string templateBlockName, string ilContent)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);

        var existing = FindBlockByName(software.BlockGroup.Blocks, software.BlockGroup.Groups, blockName);
        if (existing != null)
        {
            return new ImportResult(false, new[] { $"A block named '{blockName}' already exists in PLC '{plcName}' (block names are unique per-PLC, not per-group). Use write_plc_block to edit it, or pick a different name." });
        }

        var resolved = ResolveGroup(software, groupPath);
        if (resolved == null)
        {
            return new ImportResult(false, new[] { $"Group '{groupPath}' not found in PLC '{plcName}'. Use create_plc_group first, or pass an empty groupPath for the root." });
        }

        var templateFound = FindBlockByName(software.BlockGroup.Blocks, software.BlockGroup.Groups, templateBlockName);
        if (templateFound == null)
        {
            return new ImportResult(false, new[] { $"Template block '{templateBlockName}' not found in PLC '{plcName}'. Pass the name of an existing GRAPH block - its step/transition layout doesn't matter, it will be reshaped to match ilContent." });
        }

        var (templateBlock, _) = templateFound.Value;
        if (templateBlock.ProgrammingLanguage != ProgrammingLanguage.GRAPH)
        {
            return new ImportResult(false, new[] { $"Template block '{templateBlockName}' is {templateBlock.ProgrammingLanguage}, not GRAPH. Pass the name of an existing GRAPH block to use as the structural template." });
        }

        var masterCopyFolder = _project!.ProjectLibrary.MasterCopyFolder;

        // Clean up a same-named leftover from a previous run that failed after this point but
        // before its own cleanup ran.
        var leftover = masterCopyFolder.MasterCopies.Find(templateBlock.Name);
        if (leftover != null)
        {
            try { leftover.Delete(); } catch { /* best effort */ }
        }

        MasterCopy masterCopy;
        try
        {
            masterCopy = masterCopyFolder.MasterCopies.Create((IMasterCopySource)templateBlock);
        }
        catch (Exception ex)
        {
            return new ImportResult(false, new[] { $"Could not create a master copy from template block '{templateBlockName}': {ex.Message}" });
        }

        PlcBlock newBlock;
        try
        {
            newBlock = resolved.Value.Blocks.CreateFrom(masterCopy);
        }
        catch (Exception ex)
        {
            try { masterCopy.Delete(); } catch { /* best effort */ }
            return new ImportResult(false, new[] { $"Could not create block from master copy: {ex.Message}" });
        }

        try
        {
            newBlock.Name = blockName;
        }
        catch (Exception ex)
        {
            try { masterCopy.Delete(); } catch { /* best effort */ }
            try { newBlock.Delete(); } catch { /* best effort */ }
            return new ImportResult(false, new[] { $"Block was created but could not be renamed to '{blockName}': {ex.Message}. The partial clone was removed - retry with a different name." });
        }

        try { masterCopy.Delete(); } catch { /* best effort - leftover clutter in the library isn't worth failing the whole operation over */ }

        var messages = new List<string> { $"Created '{blockName}' as a structural clone of '{templateBlockName}'." };

        // Compile so the block is consistent - WriteGraphBlock (like ReadGraphBlock) requires a
        // consistent/compiled block, the same requirement any existing GRAPH block hits.
        try
        {
            var compileResult = software.GetService<ICompilable>().Compile();
            messages.Add($"Compiled: {compileResult.State}, {compileResult.ErrorCount} error(s), {compileResult.WarningCount} warning(s).");
        }
        catch (Exception ex)
        {
            messages.Add($"(Clone created but compile failed to run: {ex.Message})");
        }

        if (!newBlock.IsConsistent)
        {
            messages.Add($"'{blockName}' is not consistent after cloning/compiling, so reshaping to ilContent was skipped. It currently exists as an exact copy of '{templateBlockName}' under its own name - resolve the compile error (see compile_plc), then call write_plc_block with ilContent to reshape it.");
            return new ImportResult(true, messages);
        }

        var reshapeResult = WriteGraphBlock(software, newBlock, resolved.Value.Blocks, new[] { new BlockDocument($"{blockName}.graph.il", ilContent) });
        messages.Add(reshapeResult.Success
            ? "Reshaped to the requested layout."
            : $"Reshape FAILED - '{blockName}' exists as an exact structural copy of '{templateBlockName}', not yet reshaped. Fix ilContent and call write_plc_block to retry.");
        messages.AddRange(reshapeResult.Messages);

        return new ImportResult(reshapeResult.Success, messages);
    }

    private ImportResult ImportBlockDocuments(PlcBlockComposition target, string blockName, IReadOnlyList<BlockDocument> documents, ImportDocumentOptions options)
    {
        var resFile = documents.FirstOrDefault(d => d.FileName.EndsWith(".s7res", StringComparison.OrdinalIgnoreCase));
        if (resFile != null)
        {
            var dupes = ResourceFileGuard.FindDuplicateIds(resFile.Content);
            if (dupes.Count > 0)
            {
                return new ImportResult(false, new[]
                {
                    $"Refusing to import: the .s7res resource file has {dupes.Count} duplicate multilingual-text ID(s) ({string.Join(", ", dupes)}). " +
                    "This is a known TIA Portal export defect (see Phase 0 findings) where different comment/title texts collide onto the same generated ID. " +
                    "Automatic repair isn't implemented yet because it would require assuming the Nth duplicate reference in the .s7dcl lines up with the Nth matching entry in the .s7res - an unverified heuristic that could silently attach the wrong comment to the wrong network. " +
                    "Edit this block manually in the TIA Portal GUI for now.",
                });
            }
        }

        var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "TiaMcp", "import", Guid.NewGuid().ToString("N")));
        dir.Create();
        try
        {
            foreach (var doc in documents)
            {
                File.WriteAllText(Path.Combine(dir.FullName, doc.FileName), doc.Content);
            }

            var result = target.ImportFromDocuments(dir, blockName, options);
            var messages = result.Messages.Select(m => m.Message).ToArray();
            return new ImportResult(result.State != DocumentResultState.Failure, messages);
        }
        catch (Exception ex)
        {
            return new ImportResult(false, new[] { ex.Message });
        }
        finally
        {
            try { dir.Delete(true); } catch { /* best effort cleanup */ }
        }
    }

    public ImportResult WriteUdt(string plcName, string typeName, IReadOnlyList<BlockDocument> documents)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);
        var found = FindTypeByName(software.TypeGroup.Types, software.TypeGroup.Groups, typeName);
        if (found == null)
        {
            return new ImportResult(false, new[] { $"UDT '{typeName}' not found in PLC '{plcName}'. write_udt only overwrites an existing UDT in place; use create_udt to make a new one." });
        }

        var (_, owner) = found.Value;
        return ImportTypeDocuments(owner, typeName, documents, ImportDocumentOptions.Override);
    }

    public ImportResult CreateUdt(string plcName, string groupPath, string typeName, IReadOnlyList<BlockDocument> documents)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);

        var existing = FindTypeByName(software.TypeGroup.Types, software.TypeGroup.Groups, typeName);
        if (existing != null)
        {
            return new ImportResult(false, new[] { $"A UDT named '{typeName}' already exists in PLC '{plcName}' (type names are unique per-PLC, not per-group). Use write_udt to edit it, or pick a different name." });
        }

        var resolved = ResolveTypeGroup(software, groupPath);
        if (resolved == null)
        {
            return new ImportResult(false, new[] { $"Type group '{groupPath}' not found in PLC '{plcName}'. Use create_plc_udt_group first, or pass an empty groupPath for the root." });
        }

        return ImportTypeDocuments(resolved.Value.Types, typeName, documents, ImportDocumentOptions.None);
    }

    public SimpleResult DeleteUdt(string plcName, string typeName)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);
        var found = FindTypeByName(software.TypeGroup.Types, software.TypeGroup.Groups, typeName);
        if (found == null)
        {
            return new SimpleResult(false, $"UDT '{typeName}' not found in PLC '{plcName}'.");
        }

        var (type, _) = found.Value;
        try
        {
            type.Delete();
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    public SimpleResult RenameUdt(string plcName, string typeName, string newName)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);
        var found = FindTypeByName(software.TypeGroup.Types, software.TypeGroup.Groups, typeName);
        if (found == null)
        {
            return new SimpleResult(false, $"UDT '{typeName}' not found in PLC '{plcName}'.");
        }

        var existing = FindTypeByName(software.TypeGroup.Types, software.TypeGroup.Groups, newName);
        if (existing != null)
        {
            return new SimpleResult(false, $"A UDT named '{newName}' already exists in PLC '{plcName}' (type names are unique per-PLC, not per-group).");
        }

        var (type, _) = found.Value;
        try
        {
            type.Name = newName;
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    private ImportResult ImportTypeDocuments(PlcTypeComposition target, string typeName, IReadOnlyList<BlockDocument> documents, ImportDocumentOptions options)
    {
        var dir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "TiaMcp", "import-udt", Guid.NewGuid().ToString("N")));
        dir.Create();
        try
        {
            foreach (var doc in documents)
            {
                File.WriteAllText(Path.Combine(dir.FullName, doc.FileName), doc.Content);
            }

            var result = target.ImportFromDocuments(dir, typeName, options);
            var messages = result.Messages.Select(m => m.Message).ToArray();
            return new ImportResult(result.State != DocumentResultState.Failure, messages);
        }
        catch (Exception ex)
        {
            return new ImportResult(false, new[] { ex.Message });
        }
        finally
        {
            try { dir.Delete(true); } catch { /* best effort cleanup */ }
        }
    }

    public SimpleResult CreateInstanceDb(string plcName, string groupPath, string dbName, string instanceOfFbName)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);

        var existing = FindBlockByName(software.BlockGroup.Blocks, software.BlockGroup.Groups, dbName);
        if (existing != null)
        {
            return new SimpleResult(false, $"A block named '{dbName}' already exists in PLC '{plcName}' (block names are unique per-PLC, not per-group).");
        }

        var resolved = ResolveGroup(software, groupPath);
        if (resolved == null)
        {
            return new SimpleResult(false, $"Group '{groupPath}' not found in PLC '{plcName}'. Use create_plc_group first, or pass an empty groupPath for the root.");
        }

        try
        {
            // isAutoNumbered:true with number:0 is unreliable - Openness takes 0 literally as a
            // preferred/starting number whenever it happens to be free (the very first instance-DB
            // created after a fresh connect got DB number 0, an invalid S7 block number, while
            // later calls landed on legitimate free numbers once 0 was taken). Seeding with 1 -
            // the lowest valid DB number - instead of 0 keeps auto-numbering from ever proposing
            // an invalid number.
            resolved.Value.Blocks.CreateInstanceDB(dbName, isAutoNumbered: true, number: 1, instanceOfName: instanceOfFbName);
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    public SimpleResult CreateBlockGroup(string plcName, string parentGroupPath, string groupName)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);
        var resolved = ResolveGroup(software, parentGroupPath);
        if (resolved == null)
        {
            return new SimpleResult(false, $"Parent group '{parentGroupPath}' not found in PLC '{plcName}'.");
        }

        try
        {
            resolved.Value.Groups.Create(groupName);
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    public SimpleResult DeleteBlockGroup(string plcName, string groupPath)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);
        var group = FindGroupByPath(software.BlockGroup.Groups, groupPath);
        if (group == null)
        {
            return new SimpleResult(false, $"Group '{groupPath}' not found in PLC '{plcName}'.");
        }

        try
        {
            group.Delete();
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    public SimpleResult RenamePlcGroup(string plcName, string groupPath, string newName)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);
        var group = FindGroupByPath(software.BlockGroup.Groups, groupPath);
        if (group == null)
        {
            return new SimpleResult(false, $"Group '{groupPath}' not found in PLC '{plcName}'.");
        }

        try
        {
            group.Name = newName;
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    // Deletes a single block (any language, including GRAPH and instance-DBs) without touching
    // its containing group or siblings - the escape hatch a block beyond repair needs (e.g. a
    // GRAPH block WriteGraphBlock refuses to touch because it's stuck inconsistent - its own
    // Export() call has the same consistency requirement as ReadGraphBlock's, so there's no way
    // to make the write path tolerate that starting point; delete-and-recreate via
    // CreateGraphBlock is the only recovery). Also covers renaming, which Openness has no direct
    // API for either. An instance-DB still bound to an FB/FC must be deleted before the FB/FC
    // itself - TIA Portal refuses block.Delete() on a type block with a live instance; that
    // ordering is surfaced via TIA's own exception message rather than resolved here.
    public SimpleResult DeleteBlock(string plcName, string blockName)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);
        var found = FindBlockByName(software.BlockGroup.Blocks, software.BlockGroup.Groups, blockName);
        if (found == null)
        {
            return new SimpleResult(false, $"Block '{blockName}' not found in PLC '{plcName}'.");
        }

        var (block, _) = found.Value;
        try
        {
            block.Delete();
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    // A plain .Name assignment is safe even on an existing, compiled block with real dependents -
    // a bound instance-DB and a second block calling it. Renaming the FB (or, separately,
    // renaming the instance-DB) leaves both dependents temporarily INCONSISTENT, exactly like any
    // other block edit; a normal compile_plc afterward resolves cleanly, and the caller's call
    // statement text follows the rename automatically. This means TIA resolves block/DB
    // references by internal object identity, not by name text, so a rename is safe project-wide
    // - unlike delete, which TIA refuses outright when a live dependent exists (see DeleteBlock
    // above), rename has no such guard needed because nothing breaks.
    public SimpleResult RenamePlcBlock(string plcName, string blockName, string newName)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);
        var found = FindBlockByName(software.BlockGroup.Blocks, software.BlockGroup.Groups, blockName);
        if (found == null)
        {
            return new SimpleResult(false, $"Block '{blockName}' not found in PLC '{plcName}'.");
        }

        var existing = FindBlockByName(software.BlockGroup.Blocks, software.BlockGroup.Groups, newName);
        if (existing != null)
        {
            return new SimpleResult(false, $"A block named '{newName}' already exists in PLC '{plcName}' (block names are unique per-PLC, not per-group).");
        }

        var (block, _) = found.Value;
        try
        {
            block.Name = newName;
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    private static (PlcBlockComposition Blocks, PlcBlockUserGroupComposition Groups)? ResolveGroup(PlcSoftware software, string groupPath)
    {
        if (string.IsNullOrEmpty(groupPath))
        {
            return (software.BlockGroup.Blocks, software.BlockGroup.Groups);
        }

        var group = FindGroupByPath(software.BlockGroup.Groups, groupPath);
        if (group == null) return null;
        return (group.Blocks, group.Groups);
    }

    private static PlcBlockUserGroup? FindGroupByPath(PlcBlockUserGroupComposition rootGroups, string path)
    {
        var segments = path.Split('/');
        var current = rootGroups;
        PlcBlockUserGroup? group = null;
        foreach (var segment in segments)
        {
            group = current.Find(segment);
            if (group == null) return null;
            current = group.Groups;
        }
        return group;
    }

    private static (PlcTypeComposition Types, PlcTypeUserGroupComposition Groups)? ResolveTypeGroup(PlcSoftware software, string groupPath)
    {
        if (string.IsNullOrEmpty(groupPath))
        {
            return (software.TypeGroup.Types, software.TypeGroup.Groups);
        }

        var group = FindTypeGroupByPath(software.TypeGroup.Groups, groupPath);
        if (group == null) return null;
        return (group.Types, group.Groups);
    }

    private static PlcTypeUserGroup? FindTypeGroupByPath(PlcTypeUserGroupComposition rootGroups, string path)
    {
        var segments = path.Split('/');
        var current = rootGroups;
        PlcTypeUserGroup? group = null;
        foreach (var segment in segments)
        {
            group = current.Find(segment);
            if (group == null) return null;
            current = group.Groups;
        }
        return group;
    }

    public SimpleResult CreateTypeGroup(string plcName, string parentGroupPath, string groupName)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);
        var resolved = ResolveTypeGroup(software, parentGroupPath);
        if (resolved == null)
        {
            return new SimpleResult(false, $"Parent group '{parentGroupPath}' not found in PLC '{plcName}'.");
        }

        try
        {
            resolved.Value.Groups.Create(groupName);
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    public SimpleResult DeleteTypeGroup(string plcName, string groupPath)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);
        var group = FindTypeGroupByPath(software.TypeGroup.Groups, groupPath);
        if (group == null)
        {
            return new SimpleResult(false, $"Group '{groupPath}' not found in PLC '{plcName}'.");
        }

        try
        {
            group.Delete();
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    public SimpleResult RenameTypeGroup(string plcName, string groupPath, string newName)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);
        var group = FindTypeGroupByPath(software.TypeGroup.Groups, groupPath);
        if (group == null)
        {
            return new SimpleResult(false, $"Group '{groupPath}' not found in PLC '{plcName}'.");
        }

        try
        {
            group.Name = newName;
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    public IReadOnlyList<TagTableSummary> ListTagTables(string plcName)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);
        var result = new List<TagTableSummary>();
        WalkTagTables(plcName, software.TagTableGroup.TagTables, software.TagTableGroup.Groups, "", result);
        return result;
    }

    private void WalkTagTables(string plcName, PlcTagTableComposition tables, PlcTagTableUserGroupComposition groups, string groupPath, List<TagTableSummary> result)
    {
        foreach (PlcTagTable table in tables)
        {
            result.Add(new TagTableSummary(plcName, groupPath, table.Name));
        }

        foreach (var group in groups)
        {
            var childPath = string.IsNullOrEmpty(groupPath) ? group.Name : $"{groupPath}/{group.Name}";
            WalkTagTables(plcName, group.TagTables, group.Groups, childPath, result);
        }
    }

    public TagTableResult ReadTagTable(string plcName, string tableName)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);
        var table = FindTagTableByName(software.TagTableGroup.TagTables, software.TagTableGroup.Groups, tableName);
        if (table == null)
        {
            return new TagTableResult(false, Array.Empty<TagInfo>(), $"Tag table '{tableName}' not found in PLC '{plcName}'.");
        }

        var tags = table.Tags
            .Select(t => new TagInfo(t.Name, t.DataTypeName, t.LogicalAddress, ExtractText(t.Comment)))
            .ToArray();
        return new TagTableResult(true, tags, null);
    }

    public WriteTagsResult WriteTagTable(string plcName, string tableName, IReadOnlyList<TagSpec> tags)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);
        var table = FindTagTableByName(software.TagTableGroup.TagTables, software.TagTableGroup.Groups, tableName);
        if (table == null)
        {
            return new WriteTagsResult(false, new[] { $"Tag table '{tableName}' not found in PLC '{plcName}'." });
        }

        var messages = new List<string>();
        foreach (var spec in tags)
        {
            try
            {
                var existing = table.Tags.OfType<PlcTag>().FirstOrDefault(t => t.Name == spec.Name);
                if (existing != null)
                {
                    existing.DataTypeName = spec.DataType;
                    if (spec.LogicalAddress != null) existing.LogicalAddress = spec.LogicalAddress;
                    messages.Add($"Updated '{spec.Name}'.");
                }
                else
                {
                    table.Tags.Create(spec.Name, spec.DataType, spec.LogicalAddress ?? "");
                    messages.Add($"Created '{spec.Name}'.");
                }
            }
            catch (Exception ex)
            {
                messages.Add($"FAILED '{spec.Name}': {ex.Message}");
            }
        }

        var success = messages.All(m => !m.StartsWith("FAILED"));
        return new WriteTagsResult(success, messages);
    }

    private static string? ExtractText(MultilingualText? text)
    {
        if (text == null) return null;
        var items = text.Items.Cast<MultilingualTextItem>().Where(i => !string.IsNullOrEmpty(i.Text)).ToArray();
        if (items.Length == 0) return null;
        return string.Join(" | ", items.Select(i => i.Text));
    }

    private static PlcTagTable? FindTagTableByName(PlcTagTableComposition tables, PlcTagTableUserGroupComposition groups, string name)
    {
        var direct = tables.OfType<PlcTagTable>().FirstOrDefault(t => t.Name == name);
        if (direct != null) return direct;

        foreach (var group in groups)
        {
            var found = FindTagTableByName(group.TagTables, group.Groups, name);
            if (found != null) return found;
        }

        return null;
    }

    public SimpleResult CreateTagTable(string plcName, string groupPath, string tableName)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);

        var existing = FindTagTableByName(software.TagTableGroup.TagTables, software.TagTableGroup.Groups, tableName);
        if (existing != null)
        {
            return new SimpleResult(false, $"A tag table named '{tableName}' already exists in PLC '{plcName}' (tag table names are unique per-PLC, not per-group).");
        }

        var resolved = ResolveTagTableGroup(software, groupPath);
        if (resolved == null)
        {
            return new SimpleResult(false, $"Group '{groupPath}' not found in PLC '{plcName}'. Use create_plc_tag_table_group first, or pass an empty groupPath for the root.");
        }

        try
        {
            resolved.Value.TagTables.Create(tableName);
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    public SimpleResult DeleteTagTable(string plcName, string tableName)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);
        var table = FindTagTableByName(software.TagTableGroup.TagTables, software.TagTableGroup.Groups, tableName);
        if (table == null)
        {
            return new SimpleResult(false, $"Tag table '{tableName}' not found in PLC '{plcName}'.");
        }

        try
        {
            table.Delete();
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    public SimpleResult RenameTagTable(string plcName, string tableName, string newName)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);
        var table = FindTagTableByName(software.TagTableGroup.TagTables, software.TagTableGroup.Groups, tableName);
        if (table == null)
        {
            return new SimpleResult(false, $"Tag table '{tableName}' not found in PLC '{plcName}'.");
        }

        var existing = FindTagTableByName(software.TagTableGroup.TagTables, software.TagTableGroup.Groups, newName);
        if (existing != null)
        {
            return new SimpleResult(false, $"A tag table named '{newName}' already exists in PLC '{plcName}' (tag table names are unique per-PLC, not per-group).");
        }

        try
        {
            table.Name = newName;
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    private static (PlcTagTableComposition TagTables, PlcTagTableUserGroupComposition Groups)? ResolveTagTableGroup(PlcSoftware software, string groupPath)
    {
        if (string.IsNullOrEmpty(groupPath))
        {
            return (software.TagTableGroup.TagTables, software.TagTableGroup.Groups);
        }

        var group = FindTagTableGroupByPath(software.TagTableGroup.Groups, groupPath);
        if (group == null) return null;
        return (group.TagTables, group.Groups);
    }

    private static PlcTagTableUserGroup? FindTagTableGroupByPath(PlcTagTableUserGroupComposition rootGroups, string path)
    {
        var segments = path.Split('/');
        var current = rootGroups;
        PlcTagTableUserGroup? group = null;
        foreach (var segment in segments)
        {
            group = current.Find(segment);
            if (group == null) return null;
            current = group.Groups;
        }
        return group;
    }

    public SimpleResult CreateTagTableGroup(string plcName, string parentGroupPath, string groupName)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);
        var resolved = ResolveTagTableGroup(software, parentGroupPath);
        if (resolved == null)
        {
            return new SimpleResult(false, $"Parent group '{parentGroupPath}' not found in PLC '{plcName}'.");
        }

        try
        {
            resolved.Value.Groups.Create(groupName);
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    public SimpleResult DeleteTagTableGroup(string plcName, string groupPath)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);
        var group = FindTagTableGroupByPath(software.TagTableGroup.Groups, groupPath);
        if (group == null)
        {
            return new SimpleResult(false, $"Group '{groupPath}' not found in PLC '{plcName}'.");
        }

        try
        {
            group.Delete();
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    public SimpleResult RenameTagTableGroup(string plcName, string groupPath, string newName)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);
        var group = FindTagTableGroupByPath(software.TagTableGroup.Groups, groupPath);
        if (group == null)
        {
            return new SimpleResult(false, $"Group '{groupPath}' not found in PLC '{plcName}'.");
        }

        try
        {
            group.Name = newName;
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    // ---- WinCC Unified HMI tags and alarms ----
    // Mirrors the PLC tag-table methods above with Plc*->Hmi* type substitutions. One real
    // structural difference: PlcSoftware bundles root tag tables and root groups under one
    // "TagTableGroup" property; HmiSoftware exposes them as two
    // separate flat properties, "TagTables" and "TagTableGroups", directly on HmiSoftware itself.

    private HmiSoftware GetHmiSoftware(string hmiName)
    {
        if (!_hmiSoftwareByName.TryGetValue(hmiName, out var software))
        {
            var available = string.Join(", ", _hmiSoftwareByName.Keys);
            throw new InvalidOperationException(
                $"Unknown HMI '{hmiName}'. Known HMIs: [{available}]. " +
                "If an HMI device was renamed in the TIA Portal GUI since the last tia_connect, call tia_connect again - the HMI name index is only built at connect time.");
        }
        return software;
    }

    public IReadOnlyList<HmiTagTableSummary> ListHmiTagTables(string hmiName)
    {
        EnsureConnected();
        var software = GetHmiSoftware(hmiName);
        var result = new List<HmiTagTableSummary>();
        WalkHmiTagTables(hmiName, software.TagTables, software.TagTableGroups, "", result);
        return result;
    }

    private void WalkHmiTagTables(string hmiName, HmiTagTableComposition tables, HmiTagTableGroupComposition groups, string groupPath, List<HmiTagTableSummary> result)
    {
        foreach (HmiTagTable table in tables)
        {
            result.Add(new HmiTagTableSummary(hmiName, groupPath, table.Name));
        }

        foreach (var group in groups)
        {
            var childPath = string.IsNullOrEmpty(groupPath) ? group.Name : $"{groupPath}/{group.Name}";
            WalkHmiTagTables(hmiName, group.TagTables, group.Groups, childPath, result);
        }
    }

    private static HmiTagTable? FindHmiTagTableByName(HmiTagTableComposition tables, HmiTagTableGroupComposition groups, string name)
    {
        var direct = tables.OfType<HmiTagTable>().FirstOrDefault(t => t.Name == name);
        if (direct != null) return direct;

        foreach (var group in groups)
        {
            var found = FindHmiTagTableByName(group.TagTables, group.Groups, name);
            if (found != null) return found;
        }

        return null;
    }

    public HmiTagTableResult ReadHmiTagTable(string hmiName, string tableName)
    {
        EnsureConnected();
        var software = GetHmiSoftware(hmiName);
        var table = FindHmiTagTableByName(software.TagTables, software.TagTableGroups, tableName);
        if (table == null)
        {
            return new HmiTagTableResult(false, Array.Empty<HmiTagInfo>(), $"HMI tag table '{tableName}' not found in HMI '{hmiName}'.");
        }

        var tags = table.Tags
            .OfType<HmiTag>()
            .Select(t => new HmiTagInfo(t.Name, t.DataType, t.Address, t.Connection, t.PlcName, t.PlcTag, ExtractText(t.Comment)))
            .ToArray();
        return new HmiTagTableResult(true, tags, null);
    }

    public WriteTagsResult WriteHmiTagTable(string hmiName, string tableName, IReadOnlyList<HmiTagSpec> tags)
    {
        EnsureConnected();
        var software = GetHmiSoftware(hmiName);
        var table = FindHmiTagTableByName(software.TagTables, software.TagTableGroups, tableName);
        if (table == null)
        {
            return new WriteTagsResult(false, new[] { $"HMI tag table '{tableName}' not found in HMI '{hmiName}'." });
        }

        var messages = new List<string>();
        foreach (var spec in tags)
        {
            try
            {
                var existing = table.Tags.OfType<HmiTag>().FirstOrDefault(t => t.Name == spec.Name);
                var tag = existing ?? table.Tags.Create(spec.Name);
                bool isPlcBound = !string.IsNullOrEmpty(spec.Connection) && !string.IsNullOrEmpty(spec.PlcTag);
                if (!isPlcBound)
                {
                    // No PLC binding (an internal tag) - nothing else can tell TIA what type this
                    // is, so DataType must be set explicitly here.
                    tag.DataType = spec.DataType;
                }
                if (spec.Address != null) tag.Address = spec.Address;
                if (spec.Connection != null) tag.Connection = spec.Connection;
                // PlcName is derived from Connection, not independently settable: HmiTag.PlcName
                // has no public setter, and even SetAttribute("PlcName", ...) throws
                // "'set_PlcName' is not supported by type HmiTag". Setting Connection (+ PlcTag)
                // alone is sufficient; TIA resolves and populates PlcName itself. spec.PlcName is
                // intentionally ignored here (kept on the record only so read/write share one shape).
                if (spec.PlcTag != null) tag.PlcTag = spec.PlcTag;
                // For a PLC-bound tag, DataType is intentionally left untouched above and NOT set
                // from spec.DataType: when a tag has a PLC tag binding, the GUI never sets Data
                // type itself, it is auto-derived by TIA from the bound PLC tag and shown
                // read-only. Explicitly calling set_DataType on a struct/UDT-typed bound tag is
                // what throws "Empty data type or HMI data type at tag <name>" - the actual root
                // cause of the long-standing "cannot create struct/UDT-typed HMI tags" limitation,
                // not a genuine Openness restriction on struct-typed HMI tags. spec.DataType is
                // still accepted/required by the tool schema for the bound case (kept so callers
                // don't need two shapes), but is ignored here.
                messages.Add(existing != null ? $"Updated '{spec.Name}'." : $"Created '{spec.Name}'.");
            }
            catch (Exception ex)
            {
                messages.Add($"FAILED '{spec.Name}': {ex.Message}");
            }
        }

        var success = messages.All(m => !m.StartsWith("FAILED"));
        return new WriteTagsResult(success, messages);
    }

    public SimpleResult CreateHmiTagTable(string hmiName, string groupPath, string tableName)
    {
        EnsureConnected();
        var software = GetHmiSoftware(hmiName);

        var existing = FindHmiTagTableByName(software.TagTables, software.TagTableGroups, tableName);
        if (existing != null)
        {
            return new SimpleResult(false, $"An HMI tag table named '{tableName}' already exists in HMI '{hmiName}' (tag table names are unique per-HMI, not per-group).");
        }

        var resolved = ResolveHmiTagTableGroup(software, groupPath);
        if (resolved == null)
        {
            return new SimpleResult(false, $"Group '{groupPath}' not found in HMI '{hmiName}'. Use create_hmi_tag_table_group first, or pass an empty groupPath for the root.");
        }

        try
        {
            resolved.Value.TagTables.Create(tableName);
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    public SimpleResult DeleteHmiTagTable(string hmiName, string tableName)
    {
        EnsureConnected();
        var software = GetHmiSoftware(hmiName);
        var table = FindHmiTagTableByName(software.TagTables, software.TagTableGroups, tableName);
        if (table == null)
        {
            return new SimpleResult(false, $"HMI tag table '{tableName}' not found in HMI '{hmiName}'.");
        }

        try
        {
            table.Delete();
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    public SimpleResult RenameHmiTagTable(string hmiName, string tableName, string newName)
    {
        EnsureConnected();
        var software = GetHmiSoftware(hmiName);
        var table = FindHmiTagTableByName(software.TagTables, software.TagTableGroups, tableName);
        if (table == null)
        {
            return new SimpleResult(false, $"HMI tag table '{tableName}' not found in HMI '{hmiName}'.");
        }

        var existing = FindHmiTagTableByName(software.TagTables, software.TagTableGroups, newName);
        if (existing != null)
        {
            return new SimpleResult(false, $"An HMI tag table named '{newName}' already exists in HMI '{hmiName}' (tag table names are unique per-HMI, not per-group).");
        }

        try
        {
            table.Name = newName;
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    private static (HmiTagTableComposition TagTables, HmiTagTableGroupComposition Groups)? ResolveHmiTagTableGroup(HmiSoftware software, string groupPath)
    {
        if (string.IsNullOrEmpty(groupPath))
        {
            return (software.TagTables, software.TagTableGroups);
        }

        var group = FindHmiTagTableGroupByPath(software.TagTableGroups, groupPath);
        if (group == null) return null;
        return (group.TagTables, group.Groups);
    }

    private static HmiTagTableGroup? FindHmiTagTableGroupByPath(HmiTagTableGroupComposition rootGroups, string path)
    {
        var segments = path.Split('/');
        var current = rootGroups;
        HmiTagTableGroup? group = null;
        foreach (var segment in segments)
        {
            group = current.Find(segment);
            if (group == null) return null;
            current = group.Groups;
        }
        return group;
    }

    public SimpleResult CreateHmiTagTableGroup(string hmiName, string parentGroupPath, string groupName)
    {
        EnsureConnected();
        var software = GetHmiSoftware(hmiName);
        var resolved = ResolveHmiTagTableGroup(software, parentGroupPath);
        if (resolved == null)
        {
            return new SimpleResult(false, $"Parent group '{parentGroupPath}' not found in HMI '{hmiName}'.");
        }

        try
        {
            resolved.Value.Groups.Create(groupName);
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    public SimpleResult DeleteHmiTagTableGroup(string hmiName, string groupPath)
    {
        EnsureConnected();
        var software = GetHmiSoftware(hmiName);
        var group = FindHmiTagTableGroupByPath(software.TagTableGroups, groupPath);
        if (group == null)
        {
            return new SimpleResult(false, $"Group '{groupPath}' not found in HMI '{hmiName}'.");
        }

        try
        {
            group.Delete();
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    public SimpleResult RenameHmiTagTableGroup(string hmiName, string groupPath, string newName)
    {
        EnsureConnected();
        var software = GetHmiSoftware(hmiName);
        var group = FindHmiTagTableGroupByPath(software.TagTableGroups, groupPath);
        if (group == null)
        {
            return new SimpleResult(false, $"Group '{groupPath}' not found in HMI '{hmiName}'.");
        }

        try
        {
            group.Name = newName;
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    // Alarms and alarm classes have no group/folder concept in Openness - HmiDiscreteAlarmComposition/
    // HmiAnalogAlarmComposition/HmiAlarmClassComposition expose only Create/Find/Count/Item/Parent,
    // even though TIA Portal's GUI may show them organized into folders.

    public IReadOnlyList<HmiAlarmClassInfo> ListHmiAlarmClasses(string hmiName)
    {
        EnsureConnected();
        var software = GetHmiSoftware(hmiName);
        return software.AlarmClasses
            .Select(c => new HmiAlarmClassInfo(c.Name, c.Priority, c.Log, (int)c.Id, c.IsSystem))
            .ToArray();
    }

    public SimpleResult WriteHmiAlarmClass(string hmiName, HmiAlarmClassSpec spec)
    {
        EnsureConnected();
        var software = GetHmiSoftware(hmiName);
        try
        {
            var alarmClass = software.AlarmClasses.Find(spec.Name) ?? software.AlarmClasses.Create(spec.Name);
            if (spec.Priority.HasValue) alarmClass.Priority = (byte)spec.Priority.Value;
            if (spec.Log != null) alarmClass.Log = spec.Log;
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    public SimpleResult DeleteHmiAlarmClass(string hmiName, string name)
    {
        EnsureConnected();
        var software = GetHmiSoftware(hmiName);
        var alarmClass = software.AlarmClasses.Find(name);
        if (alarmClass == null)
        {
            return new SimpleResult(false, $"Alarm class '{name}' not found in HMI '{hmiName}'.");
        }

        try
        {
            alarmClass.Delete();
            return new SimpleResult(true, null);
        }
        catch (Exception ex)
        {
            return new SimpleResult(false, ex.Message);
        }
    }

    public IReadOnlyList<HmiAlarmInfo> ListHmiAlarms(string hmiName, string? type = null)
    {
        EnsureConnected();
        var software = GetHmiSoftware(hmiName);
        var result = new List<HmiAlarmInfo>();

        if (type == null || string.Equals(type, "Discrete", StringComparison.OrdinalIgnoreCase))
        {
            result.AddRange(software.DiscreteAlarms.Select(ToHmiAlarmInfo));
        }
        if (type == null || string.Equals(type, "Analog", StringComparison.OrdinalIgnoreCase))
        {
            result.AddRange(software.AnalogAlarms.Select(ToHmiAlarmInfo));
        }

        return result;
    }

    public HmiAlarmInfo? ReadHmiAlarm(string hmiName, string type, string alarmName)
    {
        EnsureConnected();
        var software = GetHmiSoftware(hmiName);
        if (string.Equals(type, "Discrete", StringComparison.OrdinalIgnoreCase))
        {
            var alarm = software.DiscreteAlarms.Find(alarmName);
            return alarm == null ? null : ToHmiAlarmInfo(alarm);
        }
        if (string.Equals(type, "Analog", StringComparison.OrdinalIgnoreCase))
        {
            var alarm = software.AnalogAlarms.Find(alarmName);
            return alarm == null ? null : ToHmiAlarmInfo(alarm);
        }
        throw new ArgumentException($"Unknown alarm type '{type}'. Use 'Discrete' or 'Analog'.");
    }

    // Several AlarmBase properties throw "PropertyDoesNotExists" rather than returning null when
    // the underlying feature isn't enabled/configured for a given alarm (e.g. AuditClass, on an
    // alarm in a project without audit trail set up) - every property read here must tolerate
    // that, or list_hmi_alarms crashes outright on any real project with alarms that predate this
    // server.
    private static string? TryGet(Func<string?> getter)
    {
        try { return getter(); }
        catch { return null; }
    }

    private HmiAlarmInfo ToHmiAlarmInfo(HmiDiscreteAlarm a) =>
        new("Discrete", a.Name, TryGet(() => a.AlarmClass), TryGet(() => ExtractText(a.EventText)), TryGet(() => ExtractText(a.InfoText)),
            TryGet(() => a.TriggerBitAddress), null, null, TryGet(() => a.RaisedStateTag), TryGet(() => a.AuditClass), TryGet(() => a.Area), TryGet(() => a.Origin));

    private HmiAlarmInfo ToHmiAlarmInfo(HmiAnalogAlarm a) =>
        new("Analog", a.Name, TryGet(() => a.AlarmClass), TryGet(() => ExtractText(a.EventText)), TryGet(() => ExtractText(a.InfoText)),
            TryGet(() => a.TriggerAddress), TryGet(() => a.Condition.ToString()), TryGet(() => a.ConditionValue?.ToString()), TryGet(() => a.RaisedStateTag), TryGet(() => a.AuditClass), TryGet(() => a.Area), TryGet(() => a.Origin));

    // Alarm property interdependencies (e.g. whether AlarmClass must be set before
    // RaisedStateTag/TriggerBitAddress, which likely reference other HMI tags) aren't verified
    // against a live project yet, so each property is set independently via SetIfPresent - one
    // failure doesn't abort the rest, and the caller sees exactly which property failed.
    public SimpleResult WriteHmiAlarm(string hmiName, HmiAlarmSpec spec)
    {
        EnsureConnected();
        var software = GetHmiSoftware(hmiName);
        var messages = new List<string>();

        if (string.Equals(spec.Type, "Discrete", StringComparison.OrdinalIgnoreCase))
        {
            HmiDiscreteAlarm alarm;
            try { alarm = software.DiscreteAlarms.Find(spec.Name) ?? software.DiscreteAlarms.Create(spec.Name); }
            catch (Exception ex) { return new SimpleResult(false, $"Could not find/create discrete alarm '{spec.Name}': {ex.Message}"); }

            SetIfPresent(() => { if (spec.AlarmClass != null) alarm.AlarmClass = spec.AlarmClass; }, "AlarmClass", messages);
            if (spec.TriggerAddress != null) messages.Add(TriggerAddressUnsupportedMessage("TriggerBitAddress"));
            SetIfPresent(() => { if (spec.RaisedStateTag != null) alarm.RaisedStateTag = spec.RaisedStateTag; }, "RaisedStateTag", messages);
            if (spec.RaisedStateTag != null) VerifyRaisedStateTagBound(spec.RaisedStateTag, () => TryGet(() => alarm.TriggerBitAddress), messages);
            SetIfPresent(() => { if (spec.AuditClass != null) alarm.AuditClass = spec.AuditClass; }, "AuditClass", messages);
            SetIfPresent(() => { if (spec.Area != null) alarm.Area = spec.Area; }, "Area", messages);
            SetIfPresent(() => { if (spec.Origin != null) alarm.Origin = spec.Origin; }, "Origin", messages);
            SetIfPresent(() => { if (spec.EventText != null) SetText(alarm.EventText, spec.EventText); }, "EventText", messages);
            SetIfPresent(() => { if (spec.InfoText != null) SetText(alarm.InfoText, spec.InfoText); }, "InfoText", messages);
        }
        else if (string.Equals(spec.Type, "Analog", StringComparison.OrdinalIgnoreCase))
        {
            HmiAnalogAlarm alarm;
            try { alarm = software.AnalogAlarms.Find(spec.Name) ?? software.AnalogAlarms.Create(spec.Name); }
            catch (Exception ex) { return new SimpleResult(false, $"Could not find/create analog alarm '{spec.Name}': {ex.Message}"); }

            SetIfPresent(() => { if (spec.AlarmClass != null) alarm.AlarmClass = spec.AlarmClass; }, "AlarmClass", messages);
            if (spec.TriggerAddress != null) messages.Add(TriggerAddressUnsupportedMessage("TriggerAddress"));
            SetIfPresent(() => { if (spec.Condition != null) alarm.Condition = (HmiAlarmCondition)Enum.Parse(typeof(HmiAlarmCondition), spec.Condition, true); }, "Condition", messages);
            // ConditionValue is typed System.Object but rejects a string at runtime ("the type of
            // the argument 'ConditionValue' (System.String) is invalid"); it wants an actual
            // numeric value, so the incoming text is parsed to double first.
            SetIfPresent(() => { if (spec.ConditionValue != null) alarm.ConditionValue = double.Parse(spec.ConditionValue, System.Globalization.CultureInfo.InvariantCulture); }, "ConditionValue", messages);
            SetIfPresent(() => { if (spec.RaisedStateTag != null) alarm.RaisedStateTag = spec.RaisedStateTag; }, "RaisedStateTag", messages);
            if (spec.RaisedStateTag != null) VerifyRaisedStateTagBound(spec.RaisedStateTag, () => TryGet(() => alarm.TriggerAddress), messages);
            SetIfPresent(() => { if (spec.AuditClass != null) alarm.AuditClass = spec.AuditClass; }, "AuditClass", messages);
            SetIfPresent(() => { if (spec.Area != null) alarm.Area = spec.Area; }, "Area", messages);
            SetIfPresent(() => { if (spec.Origin != null) alarm.Origin = spec.Origin; }, "Origin", messages);
            SetIfPresent(() => { if (spec.EventText != null) SetText(alarm.EventText, spec.EventText); }, "EventText", messages);
            SetIfPresent(() => { if (spec.InfoText != null) SetText(alarm.InfoText, spec.InfoText); }, "InfoText", messages);
        }
        else
        {
            return new SimpleResult(false, $"Unknown alarm type '{spec.Type}'. Use 'Discrete' or 'Analog'.");
        }

        var success = messages.Count == 0;
        return new SimpleResult(success, success ? null : string.Join(" | ", messages));
    }

    public SimpleResult DeleteHmiAlarm(string hmiName, string type, string alarmName)
    {
        EnsureConnected();
        var software = GetHmiSoftware(hmiName);

        if (string.Equals(type, "Discrete", StringComparison.OrdinalIgnoreCase))
        {
            var alarm = software.DiscreteAlarms.Find(alarmName);
            if (alarm == null) return new SimpleResult(false, $"Discrete alarm '{alarmName}' not found in HMI '{hmiName}'.");
            try { alarm.Delete(); return new SimpleResult(true, null); }
            catch (Exception ex) { return new SimpleResult(false, ex.Message); }
        }
        if (string.Equals(type, "Analog", StringComparison.OrdinalIgnoreCase))
        {
            var alarm = software.AnalogAlarms.Find(alarmName);
            if (alarm == null) return new SimpleResult(false, $"Analog alarm '{alarmName}' not found in HMI '{hmiName}'.");
            try { alarm.Delete(); return new SimpleResult(true, null); }
            catch (Exception ex) { return new SimpleResult(false, ex.Message); }
        }
        return new SimpleResult(false, $"Unknown alarm type '{type}'. Use 'Discrete' or 'Analog'.");
    }

    private static void SetIfPresent(Action setter, string propertyName, List<string> messages)
    {
        try
        {
            setter();
        }
        catch (Exception ex)
        {
            messages.Add($"FAILED '{propertyName}': {ex.Message}");
        }
    }

    // A dotted RaisedStateTag (a member path into a structured HMI tag, e.g.
    // "Device1.Alarm.Fault") is accepted by the RaisedStateTag setter with no exception -
    // it's a real, working WinCC Unified addressing convention - but Openness resolves
    // RaisedStateTag by exact-name lookup against the flat HMI tag list, not by walking into a
    // struct's members, so the lookup finds nothing and the alarm's own read-only, auto-computed
    // trigger address stays blank: the write reports Success but the alarm will never fire.
    // Detected here by re-reading that computed address right after the set and failing loudly if
    // it's still blank, since Openness gives no other signal that the bind didn't take. Only
    // checked for a dotted reference - a bare tag name always resolves to *something*, even if
    // wrong for a struct tag, so blankness there wouldn't be a good failure signal.
    private static void VerifyRaisedStateTagBound(string raisedStateTag, Func<string?> readTriggerAddress, List<string> messages)
    {
        if (!raisedStateTag.Contains('.')) return;
        if (string.IsNullOrEmpty(readTriggerAddress()))
        {
            messages.Add($"FAILED 'RaisedStateTag': set to '{raisedStateTag}' but no trigger address was resolved - WinCC Unified's Openness API does not bind a struct-member dotted path via RaisedStateTag (only an exact flat tag name), so this alarm was written but will never fire. Workaround: TIA Portal's native GUI 'Import from Excel' bulk alarm import, or bind to a dedicated flat Bool tag instead of a struct member.");
        }
    }

    // Every route this file uses elsewhere for "read-only-looking" properties fails here too:
    // HmiDiscreteAlarm.TriggerBitAddress / HmiAnalogAlarm.TriggerAddress have no public setter;
    // SetAttribute(propertyName, ...) under that same name throws "not supported"; TIA Portal's
    // own GUI labels this field "Trigger tag" (not "trigger address"), but
    // SetAttribute("TriggerTag", ...) also throws "not supported"; and GetAttributeInfos() on the
    // live alarm object itself throws "PropertyDoesNotExists", so the real attribute name can't
    // even be discovered that way. This narrow attribute (the raw combined
    // TriggerBitAddress/TriggerAddress string) has no working Openness setter, full stop. BUT
    // this does NOT mean an alarm's trigger can't be bound via Openness at all - RaisedStateTag
    // does that job (set two lines below this call site): pointing a new alarm's RaisedStateTag
    // at a tag already used as another alarm's real trigger causes this same TriggerBitAddress to
    // auto-compute correctly, matching the source alarm's address exactly. Only verified for the
    // common case (a dedicated bool tag at bit 0 already wired to some existing alarm) - not
    // verified for a signal with no existing alarm binding yet, or a non-zero bit/explicit
    // connection.
    private static string TriggerAddressUnsupportedMessage(string propertyName) =>
        $"FAILED '{propertyName}': WinCC Unified's Openness API (V21) exposes no working setter for this specific field - every route tried (the property setter, SetAttribute(\"{propertyName}\", ...), SetAttribute(\"TriggerTag\", ...), and GetAttributeInfos()) is rejected or fails. Set RaisedStateTag instead to bind the alarm's trigger.";

    // MultilingualTextItemComposition has no Create - items for every project editing language
    // pre-exist, only .Text is mutable - so writing a comment/event/info text means finding the
    // item for the project's current editing language and setting its Text. WinCC Unified alarm
    // EventText/InfoText items store a small HTML fragment ("<body><p>...</p></body>"), not plain
    // text - setting a bare string throws "invalid format". Plain text passed in here is wrapped
    // to match, with the three HTML metacharacters escaped.
    private void SetText(MultilingualText? text, string value)
    {
        if (text == null) return;
        var escaped = value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        var htmlValue = $"<body><p>{escaped}</p></body>";
        var item = text.Items.Find(_project!.LanguageSettings.EditingLanguage);
        if (item != null) item.Text = htmlValue;
    }

    public CompileResult Compile(string plcName)
    {
        EnsureConnected();
        var software = GetSoftware(plcName);
        var compilable = software.GetService<ICompilable>();
        var result = compilable.Compile();
        var messages = result.Messages.Cast<CompilerResultMessage>().Select(m => $"[{m.State}] {m.Description}").ToArray();
        return new CompileResult(result.State.ToString(), result.ErrorCount, result.WarningCount, messages);
    }

    // Block names are unique per-PLC regardless of group (enforced by TIA Portal itself, and
    // relied on elsewhere - see CreateBlock's "not per-group" duplicate-name message), but
    // list_blocks reports each block as "<groupPath>/<name>" for display. Accept that same
    // group-path-qualified form here too - not just the bare name - by matching on the last path
    // segment, so a name round-tripped straight from list_blocks' output works without the
    // caller having to strip the path themselves.
    private static (PlcBlock block, PlcBlockComposition owner)? FindBlockByName(PlcBlockComposition blocks, PlcBlockUserGroupComposition groups, string name)
    {
        var bareName = name.Substring(name.LastIndexOf('/') + 1);
        var direct = blocks.OfType<PlcBlock>().FirstOrDefault(b => b.Name == bareName);
        if (direct != null) return (direct, blocks);

        foreach (var group in groups)
        {
            var found = FindBlockByName(group.Blocks, group.Groups, name);
            if (found != null) return found;
        }

        return null;
    }

    private static (PlcType type, PlcTypeComposition owner)? FindTypeByName(PlcTypeComposition types, PlcTypeUserGroupComposition groups, string name)
    {
        var direct = types.FirstOrDefault(t => t.Name == name);
        if (direct != null) return (direct, types);

        foreach (var group in groups)
        {
            var found = FindTypeByName(group.Types, group.Groups, name);
            if (found != null) return found;
        }

        return null;
    }

    private PlcSoftware GetSoftware(string plcName)
    {
        if (!_softwareByName.TryGetValue(plcName, out var software))
        {
            var available = string.Join(", ", _softwareByName.Keys);
            throw new InvalidOperationException(
                $"Unknown PLC '{plcName}'. Known PLCs: [{available}]. " +
                "If a PLC/device was renamed in the TIA Portal GUI since the last tia_connect, call tia_connect again - the PLC name index is only built at connect time.");
        }
        return software;
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("Not connected. Call tia_connect first.");
        }
    }

    public void Dispose()
    {
        _tiaPortal?.Dispose();
    }
}
