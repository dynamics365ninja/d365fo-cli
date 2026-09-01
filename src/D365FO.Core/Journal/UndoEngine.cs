using System.Text.Json.Nodes;
using D365FO.Core.Bridge;
using D365FO.Core.Guardrails;
using D365FO.Core.Labels;

namespace D365FO.Core.Journal;

/// <summary>
/// Replays journal entries in reverse through the SAME write path that produced them — plain
/// file I/O for <see cref="JournalWritePath.Disk"/>, the live <c>IMetadataProvider</c> (via
/// <see cref="BridgeClient"/>) for <see cref="JournalWritePath.Bridge"/> — restoring the exact
/// pre-image bytes/value captured at write time. This is the shared engine behind <c>d365fo undo</c>
/// and the MCP <c>undo_last_modification</c> tool.
/// </summary>
public static class UndoEngine
{
    public sealed record UndoStepResult(JournalEntry Entry, bool Ok, string? Error, string? Detail, string? RnrProjWarning);

    public sealed record UndoResult(IReadOnlyList<UndoStepResult> Steps, bool DryRun)
    {
        public bool AllOk => Steps.All(s => s.Ok);
    }

    /// <summary>
    /// Undo the last <paramref name="steps"/> entries (most-recent-first). Stops at the first
    /// failure so older entries are never skipped over silently — the journal stays a strict
    /// stack. In <paramref name="dryRun"/> mode nothing is written or removed; each step is
    /// previewed only.
    /// </summary>
    /// <param name="journal">Journal to pop entries from; successfully replayed entries are removed.</param>
    /// <param name="steps">
    /// How many entries to revert, most-recent-first. Zero or negative reverts nothing — a caller
    /// asking for no steps must not silently get one.
    /// </param>
    /// <param name="dryRun">Preview only: nothing is written and no entry is removed.</param>
    /// <param name="bridgeClientFactory">
    /// Test seam: when supplied, its return value is used for every bridge-written entry
    /// instead of spawning a real <c>D365FO.Bridge.exe</c> — the caller owns disposal (see
    /// <c>BridgeClientTests.FakeBridge</c> for the in-memory stdio pattern this is meant to
    /// plug into). When omitted, a real <see cref="BridgeClient"/> is created lazily on the
    /// first bridge-written entry, reused for the rest of this call, and disposed at the end.
    /// </param>
    public static UndoResult Undo(
        ModificationJournal journal,
        int steps,
        bool dryRun,
        Func<BridgeClient>? bridgeClientFactory = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (steps <= 0)
        {
            return new UndoResult(Array.Empty<UndoStepResult>(), dryRun);
        }

        var entries = journal.List(steps);
        var results = new List<UndoStepResult>(entries.Count);

        BridgeClient? ownedClient = null;
        try
        {
            foreach (var entry in entries)
            {
                if (dryRun)
                {
                    results.Add(new UndoStepResult(entry, true, null, Preview(entry), null));
                    continue;
                }

                BridgeClient? bridgeClient = null;
                if (entry.TargetType == JournalTargetType.AotObject && entry.WritePath == JournalWritePath.Bridge)
                {
                    if (bridgeClientFactory is not null)
                    {
                        bridgeClient = bridgeClientFactory();
                    }
                    else
                    {
                        ownedClient ??= new BridgeClient(DefaultBridgeOptions());
                        bridgeClient = ownedClient;
                    }
                }

                var (ok, error, detail, rnrWarn) = ReplayOne(entry, bridgeClient);
                results.Add(new UndoStepResult(entry, ok, error, detail, rnrWarn));
                if (ok)
                {
                    journal.Remove(entry.Id);
                }
                else
                {
                    break; // fail-fast — leave this and older entries in the journal
                }
            }
        }
        finally
        {
            ownedClient?.Dispose();
        }

        return new UndoResult(results, dryRun);
    }

    private static string Preview(JournalEntry e) => e.Operation switch
    {
        JournalOperation.Create => $"would delete {DisplayTarget(e)} (undo the create)",
        JournalOperation.Update => $"would restore the pre-image of {DisplayTarget(e)}",
        JournalOperation.Delete => $"would recreate {DisplayTarget(e)} from its pre-image",
        JournalOperation.Rename => $"would rename '{e.ObjectName}' back to '{e.SecondaryKey}' in {e.TargetPath}",
        _ => $"would revert {DisplayTarget(e)}",
    };

    private static string DisplayTarget(JournalEntry e)
        => e.WritePath == JournalWritePath.Bridge
            ? $"{e.Kind} '{e.ObjectName}' (model {e.Model})"
            : e.TargetPath ?? $"{e.Kind} '{e.ObjectName}'";

    private static (bool ok, string? error, string? detail, string? rnrWarn) ReplayOne(
        JournalEntry entry, BridgeClient? bridgeClient)
    {
        try
        {
            return entry.TargetType switch
            {
                JournalTargetType.Label => ReplayLabel(entry),
                JournalTargetType.AotObject => entry.WritePath switch
                {
                    JournalWritePath.Disk => ReplayDisk(entry),
                    JournalWritePath.Bridge => ReplayBridge(entry, bridgeClient!),
                    _ => (false, $"Unknown write path '{entry.WritePath}'.", null, null),
                },
                _ => (false, $"Unknown target type '{entry.TargetType}'.", null, null),
            };
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null, null);
        }
    }

    // ---- disk replay (AOT objects) --------------------------------------

    private static (bool, string?, string?, string?) ReplayDisk(JournalEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.TargetPath))
            return (false, "Journal entry has no target path.", null, null);

        // Same boundary the forward write paths enforce (XppScaffolder.Write / LabelFileWriter).
        // The target path comes out of the journal store, not from the current command line, so
        // undo must re-check it rather than trust it: a journal carried over from a different
        // D365FO_PACKAGES_PATH — or hand-edited — would otherwise let `undo` delete or rewrite
        // files anywhere on disk.
        var cfg = D365FoSettings.FromEnvironment();
        PathGuard.EnsureWithinBoundary(
            entry.TargetPath,
            new[] { cfg.PackagesPath, cfg.WorkspacePath }.Concat(cfg.CustomPackagesPaths).ToArray());

        string? rnrWarn = null;
        switch (entry.Operation)
        {
            case JournalOperation.Create:
                if (File.Exists(entry.TargetPath))
                {
                    File.Delete(entry.TargetPath);
                }
                if (entry.RnrProjDelta is not null && !RnrProjRegistry.Invert(entry.RnrProjDelta))
                    rnrWarn = $".rnrproj entry for '{entry.ObjectName}' could not be removed automatically — check {entry.RnrProjDelta.RnrProjPath}.";
                return (true, null, $"deleted {entry.TargetPath}", rnrWarn);

            case JournalOperation.Update:
                if (entry.PreImage is null)
                    return (false, "No pre-image recorded for this update — cannot restore exact bytes.", null, null);
                AtomicWriteText(entry.TargetPath, entry.PreImage, entry.PreImageHadBom);
                return (true, null, $"restored pre-image of {entry.TargetPath}", null);

            case JournalOperation.Delete:
                if (entry.PreImage is null)
                    return (false, "No pre-image recorded for this delete — cannot restore the file.", null, null);
                AtomicWriteText(entry.TargetPath, entry.PreImage, entry.PreImageHadBom);
                if (entry.RnrProjDelta is not null && !RnrProjRegistry.Invert(entry.RnrProjDelta))
                    rnrWarn = $".rnrproj entry for '{entry.ObjectName}' could not be restored automatically — check {entry.RnrProjDelta.RnrProjPath}.";
                return (true, null, $"recreated {entry.TargetPath}", rnrWarn);

            default:
                return (false, $"Operation '{entry.Operation}' is not valid for an on-disk AOT object.", null, null);
        }
    }

    /// <summary>
    /// Restore <paramref name="content"/> to <paramref name="path"/> via tmp+move.
    /// </summary>
    /// <remarks>
    /// <c>preImageHadBom</c> is the BOM state of the original file, recorded at write time.
    /// <see cref="JournalEntry.PreImage"/> is a decoded string, so the UTF-8 BOM the original
    /// carried is not in it — writing without re-emitting it would leave the "restored" AOT XML
    /// three bytes shorter than what D365FO and Visual Studio wrote, which shows up as a real
    /// change in git. <c>null</c> (entry written by an older build) falls back to the BOM state
    /// of the file currently on disk, and to "no BOM" when there is no file to inspect.
    /// </remarks>
    private static void AtomicWriteText(string path, string content, bool? preImageHadBom = null)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var emitBom = preImageHadBom ?? (File.Exists(full) && Scaffolding.ScaffoldFileWriter.DetectUtf8Bom(full) == true);

        var tmp = full + ".undo.tmp";
        File.WriteAllText(tmp, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: emitBom));
        File.Move(tmp, full, overwrite: true);
    }

    // ---- bridge replay (AOT objects) -------------------------------------

    private static (bool, string?, string?, string?) ReplayBridge(JournalEntry entry, BridgeClient client)
    {
        JsonObject? result;
        string detail;
        try
        {
            switch (entry.Operation)
            {
                case JournalOperation.Create:
                    result = client.SendAsync("deleteObject", new JsonObject
                    {
                        ["kind"] = entry.Kind,
                        ["name"] = entry.ObjectName,
                        ["model"] = entry.Model,
                    }).GetAwaiter().GetResult();
                    detail = $"deleted {entry.Kind} '{entry.ObjectName}' via bridge";
                    break;

                case JournalOperation.Update:
                    if (entry.PreImage is null)
                        return (false, "No pre-image recorded for this update — cannot restore exact XML.", null, null);
                    result = client.SendAsync("updateObject", new JsonObject
                    {
                        ["kind"] = entry.Kind,
                        ["name"] = entry.ObjectName,
                        ["model"] = entry.Model,
                        ["xml"] = entry.PreImage,
                    }).GetAwaiter().GetResult();
                    detail = $"restored pre-image of {entry.Kind} '{entry.ObjectName}' via bridge";
                    break;

                case JournalOperation.Delete:
                    if (entry.PreImage is null)
                        return (false, "No pre-image recorded for this delete — cannot recreate the object.", null, null);
                    result = client.SendAsync("createObject", new JsonObject
                    {
                        ["kind"] = entry.Kind,
                        ["name"] = entry.ObjectName,
                        ["model"] = entry.Model,
                        ["xml"] = entry.PreImage,
                    }).GetAwaiter().GetResult();
                    detail = $"recreated {entry.Kind} '{entry.ObjectName}' via bridge";
                    break;

                default:
                    return (false, $"Operation '{entry.Operation}' is not valid for a bridge-written AOT object.", null, null);
            }
        }
        catch (BridgeException ex)
        {
            return (false, "Bridge error: " + ex.Message, null, null);
        }

        if (result is null)
            return (false, "Bridge is not available (D365FO_BRIDGE_ENABLED / D365FO_BRIDGE_PATH) or returned no result.", null, null);
        var ok = (bool?)result["ok"] ?? false;
        if (!ok)
        {
            var code = (string?)result["error"] ?? "UNKNOWN";
            var msg = (string?)result["message"] ?? string.Empty;
            return (false, $"{code}: {msg}", null, null);
        }
        return (true, null, detail, null);
    }

    /// <summary>Build <see cref="BridgeOptions"/> from the unified config resolver.</summary>
    /// <remarks>The third copy of this, until <c>BridgeGate</c> moved into Core. See
    /// <see cref="Bridge.MethodModifyEngine.DefaultBridgeOptions"/>.</remarks>
    public static BridgeOptions DefaultBridgeOptions() => BridgeGate.DefaultOptions();

    // ---- label replay ------------------------------------------------------

    private static (bool, string?, string?, string?) ReplayLabel(JournalEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.TargetPath))
            return (false, "Journal entry has no label file path.", null, null);

        switch (entry.Operation)
        {
            case JournalOperation.Create:
            {
                var res = LabelFileWriter.Delete(entry.TargetPath, entry.ObjectName);
                return res.Outcome is WriteOutcome.Deleted or WriteOutcome.KeyMissing
                    ? (true, null, $"removed label '{entry.ObjectName}' from {entry.TargetPath}", null)
                    : (false, $"Could not remove label '{entry.ObjectName}': {res.Outcome}", null, null);
            }

            case JournalOperation.Update:
            {
                if (entry.PreImage is null)
                    return (false, "No pre-image recorded for this label update.", null, null);
                LabelFileWriter.CreateOrUpdate(entry.TargetPath, entry.ObjectName, entry.PreImage, overwrite: true);
                return (true, null, $"restored label '{entry.ObjectName}' in {entry.TargetPath}", null);
            }

            case JournalOperation.Delete:
            {
                if (entry.PreImage is null)
                    return (false, "No pre-image recorded for this label delete.", null, null);
                LabelFileWriter.CreateOrUpdate(entry.TargetPath, entry.ObjectName, entry.PreImage, overwrite: true);
                return (true, null, $"recreated label '{entry.ObjectName}' in {entry.TargetPath}", null);
            }

            case JournalOperation.Rename:
            {
                if (string.IsNullOrWhiteSpace(entry.SecondaryKey))
                    return (false, "No original key recorded for this rename.", null, null);
                var res = LabelFileWriter.Rename(entry.TargetPath, entry.ObjectName, entry.SecondaryKey, overwrite: true);
                return res.Outcome is WriteOutcome.Renamed or WriteOutcome.NoChange
                    ? (true, null, $"renamed '{entry.ObjectName}' back to '{entry.SecondaryKey}' in {entry.TargetPath}", null)
                    : (false, $"Could not rename '{entry.ObjectName}' back to '{entry.SecondaryKey}': {res.Outcome}", null, null);
            }

            default:
                return (false, $"Operation '{entry.Operation}' is not valid for a label entry.", null, null);
        }
    }
}
