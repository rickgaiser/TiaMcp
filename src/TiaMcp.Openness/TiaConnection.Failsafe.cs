using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.CrossReference;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;

namespace TiaMcp.Openness;

/// <summary>
/// Fail-safe (F-) blocks. TIA Portal refuses to import them from .s7dcl, even unchanged, so an
/// F-block is written from its raw XML (ImportBlockXml). What an F-block needs on top of a plain
/// XML import: the safety-program login, the parts of each network in power-flow order, a compile
/// after the import, and a stop after a failed import - a refused F import leaves orphaned
/// cross-references ('object not available') in the project that only closing it without saving
/// removes. A new F-block can also be created from its .s7dcl, with the same checks.
/// </summary>
public sealed partial class TiaConnection
{
    private static bool IsFailsafe(PlcBlock block) =>
        block.ProgrammingLanguage.ToString().StartsWith("F_", StringComparison.Ordinal);

    private static bool IsFailsafeXml(string xml) =>
        Regex.IsMatch(xml, @"<ProgrammingLanguage>\s*F_", RegexOptions.CultureInvariant);

    private static bool IsFailsafeSource(IReadOnlyList<BlockDocument> documents) =>
        documents.Any(d => d.FileName.EndsWith(".s7dcl", StringComparison.OrdinalIgnoreCase)
                           && Regex.IsMatch(d.Content, @"S7_Safety\s*:=\s*""TRUE""", RegexOptions.IgnoreCase));

    private static ImportResult FailsafeFromS7dcl(string blockName, string language) => new(false, new[]
    {
        $"'{blockName}' is a fail-safe block ({language}). TIA Portal does not import F-blocks from .s7dcl - make the change in '{blockName}.xml' " +
        "(read it with read_source_tree, item line with [xml]) and write again.",
    });

    // ImportBlockXml for an F-block: groupPath resolved by the caller.
    private ImportResult ImportFailsafeBlockXml(PlcSoftware software, PlcBlockComposition target, string blockName, string xml)
    {
        if (FailsafePrecheck(software) is { } refused) return refused;

        var watched = GlobalNamesIn(xml).Prepend(blockName).Distinct(StringComparer.OrdinalIgnoreCase).Take(40).ToList();
        var orphansBefore = CountOrphanReferences(software, watched);
        var content = OrderPartsByPowerFlow(xml, out var reordered);

        var result = ImportXml(content, "SW.Blocks.", "a block", blockName, file => target.Import(file, ImportOptions.Override));
        if (!result.Success)
        {
            // Checked before the import (name, element) - nothing reached TIA Portal.
            if (result.Messages.Any(m => m.StartsWith("Nothing was written", StringComparison.Ordinal))) return result;

            MarkFailsafeImportFailed(blockName, software.Name, watched, orphansBefore);
            var added = CountOrphanReferences(software, watched) - orphansBefore;
            return new ImportResult(false, result.Messages.Concat(new[]
            {
                added > 0
                    ? $"STOP: the failed import left {added} orphaned cross-reference(s) ('object not available') in the project. Close the project WITHOUT saving and open it again - that is the only way to remove them. Nothing further is written until then."
                    : "No orphaned cross-references were found after the failed import. Block writes stay blocked in this project state anyway - check the project, then reconnect (tia_connect).",
            }).ToList(), true);
        }

        var messages = new List<string> { "Fail-safe block written from XML." };
        if (reordered > 0) messages.Add($"Parts put into power-flow order in {reordered} network(s).");
        return CompileAndCheckFailsafe(software, blockName, watched, orphansBefore, messages);
    }

    // A new F-block from .s7dcl: the same checks and compile as an XML write.
    private ImportResult CreateFailsafeBlock(PlcSoftware software, PlcBlockComposition target, string blockName, IReadOnlyList<BlockDocument> documents)
    {
        if (FailsafePrecheck(software) is { } refused) return refused;
        var dcl = documents.First(d => d.FileName.EndsWith(".s7dcl", StringComparison.OrdinalIgnoreCase)).Content;
        var watched = Regex.Matches(dcl, "\"([^\"]+)\"").Cast<Match>().Select(m => m.Groups[1].Value)
            .Prepend(blockName).Distinct(StringComparer.OrdinalIgnoreCase).Take(40).ToList();
        var orphansBefore = CountOrphanReferences(software, watched);
        var created = ImportBlockDocuments(target, blockName, documents, ImportDocumentOptions.None);
        if (!created.Success)
        {
            var added = CountOrphanReferences(software, watched) - orphansBefore;
            if (added == 0) return created;
            MarkFailsafeImportFailed(blockName, software.Name, watched, orphansBefore);
            return new ImportResult(false, created.Messages.Concat(new[] { $"STOP: the failed create left {added} orphaned cross-reference(s). Close the project WITHOUT saving and open it again. Nothing further is written until then." }).ToList(), true);
        }
        return CompileAndCheckFailsafe(software, blockName, watched, orphansBefore, new List<string> { "Fail-safe block created from .s7dcl." });
    }

    // The login first: an F import that TIA Portal refuses for a missing login still leaves
    // orphans. Then the block after a failed import: never retried in the same project state.
    private ImportResult? FailsafePrecheck(PlcSoftware software)
    {
        var loginError = CheckSafetyLogin(software);
        if (loginError != null) return new ImportResult(false, new[] { loginError });
        return RefusedAfterFailedImport();
    }

    private ImportResult? RefusedAfterFailedImport() => _failsafeImportFailed == null ? null : new ImportResult(false, new[]
    {
        $"REFUSED - nothing written: the F-block import of '{_failsafeImportFailed}' failed earlier in this project state. " +
        "Close the project in TIA Portal WITHOUT saving, open it again and call tia_connect. " +
        "If the project was saved since (the orphaned cross-references are then part of it), tia_connect with releaseFailedImportBlock=true lifts this block.",
    });

    // An F-block stays inconsistent - and can't be exported again - until the safety program is
    // compiled, and compiling just the block isn't enough. ShowMessages is set on every result,
    // so the report keeps the compile and cross-reference lines and a failed result isn't retried
    // as a create (the block exists by then).
    private ImportResult CompileAndCheckFailsafe(PlcSoftware software, string blockName, IReadOnlyList<string> watched, int orphansBefore, List<string> messages)
    {
        var compileErrors = 0;
        try
        {
            var result = software.GetService<ICompilable>()?.Compile();
            if (result != null)
            {
                compileErrors = result.ErrorCount;
                messages.Add($"Compiled: {result.State}, {result.ErrorCount} error(s), {result.WarningCount} warning(s).");
                foreach (var m in FlattenMessages(result.Messages).Where(m => m.State == CompilerResultState.Error && !string.IsNullOrWhiteSpace(m.Description) && !m.Description.StartsWith("Compiling finished", StringComparison.Ordinal)).Take(20))
                    messages.Add($"[Error] {m.Path}: {m.Description}");
            }
        }
        catch (Exception ex)
        {
            compileErrors = -1;
            messages.Add($"(The compile failed to run: {ex.Message})");
        }

        var orphansAfter = CountOrphanReferences(software, watched);
        if (orphansAfter > orphansBefore)
        {
            MarkFailsafeImportFailed(blockName, software.Name, watched, orphansBefore);
            messages.Add($"STOP: the write added {orphansAfter - orphansBefore} orphaned cross-reference(s) ('object not available'). Close the project WITHOUT saving and open it again. Nothing further is written until then.");
            return new ImportResult(false, messages, true);
        }
        messages.Add($"Cross-reference check: no new orphaned references ({orphansAfter} before and after, over {watched.Count} object(s)).");

        // Written, but not compiled cleanly: the block can't be read back, so the tree keeps the edited files.
        if (compileErrors != 0)
        {
            messages.Add($"'{blockName}' was written, but the PLC doesn't compile cleanly, so it can't be read back yet. Fix the errors, compile, then read it with read_source_tree.");
            return new ImportResult(false, messages, true);
        }
        return new ImportResult(true, messages, true);
    }

    // Name of the F-block whose import failed in this project state; blocks further block writes.
    // Kept in a file per project, so a restarted server doesn't write into the same project again.
    private string? _failsafeImportFailed;

    private static string FailsafeFlagFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TiaMcp", "failed_f_imports.txt");

    private string? ProjectKey() => _project?.Path?.FullName?.ToLowerInvariant();

    // Entry: project|block|plc|orphans before the import|watched names (;-separated).
    private void MarkFailsafeImportFailed(string blockName, string plcName, IReadOnlyList<string> watched, int orphansBefore)
    {
        _failsafeImportFailed = blockName;
        try
        {
            var key = ProjectKey();
            if (key == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(FailsafeFlagFile)!);
            var lines = File.Exists(FailsafeFlagFile) ? File.ReadAllLines(FailsafeFlagFile).Where(l => !l.StartsWith(key + "|", StringComparison.Ordinal)).ToList() : new List<string>();
            lines.Add($"{key}|{blockName}|{plcName}|{orphansBefore}|{string.Join(";", watched)}");
            File.WriteAllLines(FailsafeFlagFile, lines);
        }
        catch
        {
            // the in-memory flag still holds for this server process
        }
    }

    // At connect: count the orphans of a recorded failed import again. More than before the
    // import: still the polluted state, stay blocked. Back at the old count: the project was
    // reopened without saving, so the record goes. (IsModified can't tell - some projects show
    // as modified right after opening.)
    private void LoadFailsafeImportFlag()
    {
        _failsafeImportFailed = null;
        try
        {
            var key = ProjectKey();
            if (key == null || !File.Exists(FailsafeFlagFile)) return;
            var lines = File.ReadAllLines(FailsafeFlagFile).ToList();
            var entry = lines.FirstOrDefault(l => l.StartsWith(key + "|", StringComparison.Ordinal));
            if (entry == null) return;
            var f = entry.Split('|');
            var polluted = true;
            if (f.Length >= 5 && _softwareByName.TryGetValue(f[2], out var software) && int.TryParse(f[3], out var before))
                polluted = CountOrphanReferences(software, f[4].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)) > before;
            if (polluted) _failsafeImportFailed = f[1];
            else File.WriteAllLines(FailsafeFlagFile, lines.Where(l => l != entry));
        }
        catch
        {
            // unreadable flag file - not blocking
        }
    }

    /// <summary>Lifts the failed-import block for the connected project (after it was saved with the orphans in it).</summary>
    public string? ReleaseFailedImportBlock()
    {
        EnsureConnected();
        var released = _failsafeImportFailed;
        _failsafeImportFailed = null;
        try
        {
            var key = ProjectKey();
            if (key != null && File.Exists(FailsafeFlagFile))
                File.WriteAllLines(FailsafeFlagFile, File.ReadAllLines(FailsafeFlagFile).Where(l => !l.StartsWith(key + "|", StringComparison.Ordinal)));
        }
        catch
        {
            // nothing recorded
        }
        return released;
    }

    /// <summary>Name of the F-block whose failed import currently blocks writes (null when none).</summary>
    public string? FailedImportBlock => _failsafeImportFailed;

    // TIA Portal refuses a network whose parts are not in power-flow order ("elements must be
    // sorted by power flow"). Puts the parts of every FBD/LAD network in that order: accesses
    // first (as TIA Portal exports them), then each rung - a connected group of parts and calls -
    // as one run, each part after the ones that feed it; rungs and ties keep their first order.
    // Returns the input unchanged when nothing moves.
    private static string OrderPartsByPowerFlow(string xml, out int reordered)
    {
        reordered = 0;
        XDocument doc;
        try { doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace); }
        catch { return xml; }

        foreach (var parts in doc.Descendants().Where(e => e.Name.LocalName == "Parts" && e.Parent?.Name.LocalName == "FlgNet").ToList())
        {
            var wires = parts.Parent!.Elements().FirstOrDefault(e => e.Name.LocalName == "Wires");
            var elements = parts.Elements().ToList();
            var nodes = elements.Where(e => e.Name.LocalName != "Access").ToList();
            if (nodes.Count < 2 || wires == null) continue;
            var uidOf = nodes.ToDictionary(e => e, e => (string?)e.Attribute("UId") ?? "");
            var byUid = nodes.Where(e => uidOf[e] != "").GroupBy(e => uidOf[e]).ToDictionary(g => g.Key, g => g.First());

            var next = nodes.ToDictionary(n => n, n => new List<XElement>());
            var indegree = nodes.ToDictionary(n => n, n => 0);
            var neighbours = nodes.ToDictionary(n => n, n => new HashSet<XElement>());
            foreach (var wire in wires.Elements().Where(w => w.Name.LocalName == "Wire"))
            {
                var cons = wire.Elements().ToList();
                if (cons.Count < 2) continue;
                var srcUid = (string?)cons[0].Attribute("UId");
                if (srcUid == null || !byUid.TryGetValue(srcUid, out var src)) continue;
                foreach (var c in cons.Skip(1))
                {
                    var uid = (string?)c.Attribute("UId");
                    if (uid == null || !byUid.TryGetValue(uid, out var dst) || dst == src) continue;
                    if (!next[src].Contains(dst)) { next[src].Add(dst); indegree[dst]++; }
                    neighbours[src].Add(dst); neighbours[dst].Add(src);
                }
            }

            var rungOf = new Dictionary<XElement, int>();
            var rungCount = 0;
            foreach (var n in nodes)
            {
                if (rungOf.ContainsKey(n)) continue;
                var stack = new Stack<XElement>(); stack.Push(n); rungOf[n] = rungCount;
                while (stack.Count > 0)
                    foreach (var m in neighbours[stack.Pop()])
                        if (!rungOf.ContainsKey(m)) { rungOf[m] = rungCount; stack.Push(m); }
                rungCount++;
            }

            var ordered = new List<XElement>();
            for (var r = 0; r < rungCount; r++)
            {
                var pending = nodes.Where(n => rungOf[n] == r).ToList();
                var deg = pending.ToDictionary(n => n, n => indegree[n]);
                while (pending.Count > 0)
                {
                    var ready = pending.FirstOrDefault(n => deg[n] == 0) ?? pending[0]; // a loop: keep the order
                    pending.Remove(ready); ordered.Add(ready);
                    foreach (var m in next[ready]) if (deg.ContainsKey(m)) deg[m]--;
                }
            }

            var newOrder = elements.Where(e => e.Name.LocalName == "Access").Concat(ordered).ToList();
            if (newOrder.SequenceEqual(elements)) continue;

            var indent = parts.Nodes().OfType<XText>().FirstOrDefault()?.Value ?? "\n";
            var closing = parts.Nodes().OfType<XText>().LastOrDefault()?.Value ?? "\n";
            parts.RemoveNodes();
            foreach (var e in newOrder) { parts.Add(new XText(indent)); parts.Add(e); }
            parts.Add(new XText(closing));
            reordered++;
        }
        if (reordered == 0) return xml;
        var declaration = doc.Declaration != null ? doc.Declaration + (xml.Contains("\r\n") ? "\r\n" : "\n") : "";
        return declaration + doc.Root!.ToString(SaveOptions.DisableFormatting);
    }

    // First symbol component of every global access in a SimaticML block (DBs and PLC tags).
    private static IEnumerable<string> GlobalNamesIn(string xml)
    {
        try
        {
            return XDocument.Parse(xml).Descendants()
                .Where(e => e.Name.LocalName == "Access" && (string?)e.Attribute("Scope") == "GlobalVariable")
                .Select(e => e.Descendants().FirstOrDefault(c => c.Name.LocalName == "Component")?.Attribute("Name")?.Value)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static int CountOrphanReferences(PlcSoftware software, IEnumerable<string> names)
    {
        var count = 0;
        foreach (var name in names)
        {
            try
            {
                IEngineeringServiceProvider? provider =
                    (IEngineeringServiceProvider?)FindBlockByName(software.BlockGroup.Blocks, software.BlockGroup.Groups, name)?.block
                    ?? FindTagByName(software.TagTableGroup.TagTables, software.TagTableGroup.Groups, name);
                var service = provider?.GetService<CrossReferenceService>();
                if (service == null) continue;
                foreach (SourceObject source in service.GetCrossReferences(CrossReferenceFilter.AllObjects).Sources)
                    count += CountOrphans(source);
            }
            catch
            {
                // not resolvable right now - not counted
            }
        }
        return count;
    }

    private static int CountOrphans(SourceObject source)
    {
        static bool Placeholder(string? s) => s != null && s.StartsWith("[", StringComparison.Ordinal) && s.EndsWith("]", StringComparison.Ordinal);
        var count = 0;
        foreach (ReferenceObject reference in source.References)
            foreach (Location location in reference.Locations)
                if (Placeholder(reference.Name) || Placeholder(location.ReferenceLocation)) count++;
        foreach (SourceObject child in source.Children) count += CountOrphans(child);
        return count;
    }

    private static string? CheckSafetyLogin(PlcSoftware software)
    {
        try
        {
            var admin = ((software.Parent as SoftwareContainer)?.Parent as DeviceItem)?.GetService<SafetyAdministration>();
            if (admin == null) return null; // no safety administration reachable - let TIA Portal decide
            if (!admin.IsSafetyOfflineProgramPasswordSet || admin.IsLoggedOnToSafetyOfflineProgram) return null;
            return "Not logged on to the safety program. Log on in TIA Portal (safety administration) and write again - " +
                   "an F-block import that TIA Portal refuses still leaves orphaned cross-references in the project.";
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<CompilerResultMessage> FlattenMessages(CompilerResultMessageComposition messages)
    {
        foreach (var m in messages)
        {
            yield return m;
            foreach (var child in FlattenMessages(m.Messages)) yield return child;
        }
    }
}
