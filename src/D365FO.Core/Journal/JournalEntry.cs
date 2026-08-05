namespace D365FO.Core.Journal;

/// <summary>The mutation a journal entry records.</summary>
public enum JournalOperation
{
    /// <summary>A new object/label key was created. Pre-image is a tombstone (null) — undo deletes it.</summary>
    Create,

    /// <summary>An existing object/label value was overwritten in place. Pre-image is the exact prior bytes/value.</summary>
    Update,

    /// <summary>An object/label key was removed. Pre-image is the exact bytes/value that existed before removal.</summary>
    Delete,

    /// <summary>A label key was renamed in place (labels only — AOT objects have no rename primitive here).</summary>
    Rename,
}

/// <summary>Which write path produced the entry — determines how <c>undo</c> replays it.</summary>
public enum JournalWritePath
{
    /// <summary>Plain file I/O (<c>ScaffoldFileWriter</c> / <c>LabelFileWriter</c> / direct file delete).</summary>
    Disk,

    /// <summary>Round-tripped through <c>D365FO.Bridge</c>'s live <c>IMetadataProvider</c>.</summary>
    Bridge,
}

/// <summary>What kind of artefact the entry targets — selects the replay engine.</summary>
public enum JournalTargetType
{
    /// <summary>An AOT object (class/table/edt/enum/form/…), replayed via disk file I/O or the bridge.</summary>
    AotObject,

    /// <summary>A <c>*.label.txt</c> resource key, replayed via <see cref="D365FO.Core.Labels.LabelFileWriter"/>.</summary>
    Label,
}

/// <summary>
/// Best-effort <c>.rnrproj</c> (Visual Studio Dynamics project file) item-list delta captured
/// alongside an on-disk create/delete. D365FO's build/sync tooling discovers AOT objects by
/// directory convention, not by an explicit project item list, so most installs never touch a
/// <c>.rnrproj</c> at all — this only fires when the model's project file already enumerates
/// items explicitly (see <see cref="RnrProjRegistry"/>). <paramref name="WasAdded"/> records the
/// direction of the change the original write made, so undo can invert it precisely:
/// <c>true</c> = the write ADDED this item (a create) → undo must REMOVE it;
/// <c>false</c> = the write REMOVED this item (a delete) → undo must RE-ADD it.
/// </summary>
public sealed record RnrProjDelta(string RnrProjPath, string ItemElementName, string Include, bool WasAdded);

/// <summary>
/// A single reversible write, captured by every write path that funnels through
/// <c>ScaffoldFileWriter</c>, <c>LabelFileWriter</c>, or the bridge create/update/delete verbs.
/// </summary>
/// <remarks>
/// <c>PreImageHadBom</c> records whether the pre-image file started with a UTF-8 BOM.
/// <c>PreImage</c> is captured as a decoded string and the decoder strips the BOM — without this
/// flag undo would rewrite AOT XML three bytes shorter than the original, which is a real diff to
/// D365FO and Visual Studio (both write these files WITH a BOM). <c>null</c> means "not recorded"
/// (entries written by an older build); undo then falls back to the BOM state of the file
/// currently on disk.
/// </remarks>
public sealed record JournalEntry(
    string Id,
    DateTimeOffset TimestampUtc,
    string Command,
    JournalTargetType TargetType,
    string Kind,
    string ObjectName,
    string? SecondaryKey,
    string? Model,
    JournalOperation Operation,
    JournalWritePath WritePath,
    string? TargetPath,
    string? PreImage,
    bool IsTombstone,
    RnrProjDelta? RnrProjDelta,
    bool? PreImageHadBom = null);
