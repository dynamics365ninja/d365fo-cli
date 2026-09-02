using D365FO.Core.Bridge;
using D365FO.Core.Scaffolding;

namespace D365FO.Core.Journal;

/// <summary>
/// Removes an AOT object — through the live metadata provider when a model is named, or from
/// disk when a path is — and journals the removal so <c>undo</c> can put it back.
/// </summary>
/// <remarks>
/// The pre-image is captured before the delete, and a delete that cannot capture one is refused.
/// A deletion nothing can undo is not a deletion an agent should be able to make by accident,
/// and the bridge is the only thing that can render the object as the provider sees it.
/// </remarks>
public static class AotObjectDeleter
{
    public static ToolResult<object> Delete(string? kind, string? name, string? installTo, string? path, string? model)
    {
        if (string.IsNullOrWhiteSpace(kind))
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "kind is required.");
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "name is required.");

        var hasInstall = !string.IsNullOrWhiteSpace(installTo);
        var hasPath = !string.IsNullOrWhiteSpace(path);
        if (hasInstall == hasPath)
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "Exactly one of installTo (delete through the metadata provider) or path (delete the file) is required.");

        if (hasInstall)
        {
            var axKind = kind!.Trim().ToLowerInvariant();

            // Capture the exact pre-image BEFORE deleting so `undo` can recreate it.
            var preImage = BridgeGate.TryReadObjectXml(axKind, name!);
            if (preImage is null)
                return ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed,
                    $"Could not read '{name}' via the bridge before deleting — refusing to delete without a pre-image to undo with.",
                    "Ensure D365FO_BRIDGE_ENABLED=1 and the object exists in the given model.");

            var (ok, err) = BridgeGate.TryDeleteObject(axKind, name!, installTo!);
            if (!ok)
                return ToolResult<object>.Fail(D365FoErrorCodes.DeleteFailed,
                    $"Could not delete {axKind} '{name}' in model '{installTo}' via the metadata bridge: {err}");

            RecordBridgeDelete(axKind, name!, installTo!, preImage);
            return ToolResult<object>.Success(new
            {
                kind = axKind,
                name,
                model = installTo,
                source = "bridge",
            }, [$"Index not auto-refreshed — run `d365fo index refresh --model {installTo}`."]);
        }

        try
        {
            var res = ScaffoldFileWriter.Delete(path!, model);
            return ToolResult<object>.Success(new
            {
                kind,
                name,
                path = res.Path,
                source = "scaffold",
            }, ["Index not auto-refreshed — run `d365fo index refresh`."]);
        }
        catch (FileNotFoundException)
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.SourceUnreadable, $"File not found: {path}");
        }
        catch (Exception ex)
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.DeleteFailed, ex.Message);
        }
    }

    /// <summary>
    /// Best-effort journal append for a bridge-mediated delete. Carries the pre-image, so undo
    /// recreates the object through the provider rather than leaving a hole.
    /// </summary>
    private static void RecordBridgeDelete(string axKind, string name, string model, string preImage)
    {
        try
        {
            ModificationJournal.ForIndex().Append(new JournalEntry(
                Id: Guid.NewGuid().ToString("N"),
                TimestampUtc: DateTimeOffset.UtcNow,
                Command: "delete --install-to",
                TargetType: JournalTargetType.AotObject,
                Kind: axKind,
                ObjectName: name,
                SecondaryKey: null,
                Model: model,
                Operation: JournalOperation.Delete,
                WritePath: JournalWritePath.Bridge,
                TargetPath: null,
                PreImage: preImage,
                IsTombstone: false,
                RnrProjDelta: null));
        }
        catch { /* best-effort */ }
    }
}
