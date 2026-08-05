using D365FO.Core.Labels;

namespace D365FO.Core.Journal;

/// <summary>
/// Best-effort modification-journal append for label writes (issue #113). Shared by both
/// call sites that write through <see cref="LabelFileWriter"/> — the CLI's
/// <c>LabelCreateCommand</c>/<c>LabelRenameCommand</c>/<c>LabelDeleteCommand</c> and the MCP
/// <c>labels</c> tool's <c>CreateLabel</c>/<c>RenameLabel</c>/<c>DeleteLabel</c> handlers —
/// so a write journaled from either surface is undo-able the same way. Never lets a journal
/// failure fail the label write it is recording.
/// </summary>
public static class LabelJournalRecorder
{
    public static void RecordCreateOrUpdate(WriteResult res, string command)
    {
        try
        {
            JournalOperation? op = res.Outcome switch
            {
                WriteOutcome.Created => JournalOperation.Create,
                WriteOutcome.Updated => JournalOperation.Update,
                _ => null,
            };
            if (op is null) return; // KeyExists / NoChange / FileMissing / … — nothing was written

            ModificationJournal.ForIndex().Append(new JournalEntry(
                Id: Guid.NewGuid().ToString("N"),
                TimestampUtc: DateTimeOffset.UtcNow,
                Command: command,
                TargetType: JournalTargetType.Label,
                Kind: "label",
                ObjectName: res.Key,
                SecondaryKey: null,
                Model: null,
                Operation: op.Value,
                WritePath: JournalWritePath.Disk,
                TargetPath: res.Path,
                PreImage: op == JournalOperation.Update ? res.OldValue : null,
                IsTombstone: op == JournalOperation.Create,
                RnrProjDelta: null));
        }
        catch { /* best-effort */ }
    }

    public static void RecordRename(WriteResult res, string oldKey, string command)
    {
        try
        {
            if (res.Outcome != WriteOutcome.Renamed) return;

            ModificationJournal.ForIndex().Append(new JournalEntry(
                Id: Guid.NewGuid().ToString("N"),
                TimestampUtc: DateTimeOffset.UtcNow,
                Command: command,
                TargetType: JournalTargetType.Label,
                Kind: "label",
                ObjectName: res.Key, // new key
                SecondaryKey: oldKey,
                Model: null,
                Operation: JournalOperation.Rename,
                WritePath: JournalWritePath.Disk,
                TargetPath: res.Path,
                PreImage: null,
                IsTombstone: false,
                RnrProjDelta: null));
        }
        catch { /* best-effort */ }
    }

    public static void RecordDelete(WriteResult res, string command)
    {
        try
        {
            if (res.Outcome != WriteOutcome.Deleted) return;

            ModificationJournal.ForIndex().Append(new JournalEntry(
                Id: Guid.NewGuid().ToString("N"),
                TimestampUtc: DateTimeOffset.UtcNow,
                Command: command,
                TargetType: JournalTargetType.Label,
                Kind: "label",
                ObjectName: res.Key,
                SecondaryKey: null,
                Model: null,
                Operation: JournalOperation.Delete,
                WritePath: JournalWritePath.Disk,
                TargetPath: res.Path,
                PreImage: res.OldValue,
                IsTombstone: false,
                RnrProjDelta: null));
        }
        catch { /* best-effort */ }
    }
}
