namespace Apparatus.Core.Backup;

public enum SyncDecision
{
    /// <summary>Safe to replace the file in the sync folder with this computer's backup.</summary>
    Write,

    /// <summary>The file holds changes from another computer that this one has not seen; overwriting would lose them.</summary>
    HoldBackConflict,
}

/// <summary>
/// The rules of the "sync folder" (a folder kept in sync by OneDrive, Dropbox or similar): one backup file, last writer wins
/// except that a computer never overwrites a newer file written by another computer before having seen it.
/// </summary>
public static class SyncPolicy
{
    public const string FileName = "Apparatus.apparatus";

    /// <param name="existing">The manifest of the file now in the folder, or null when there is none (or it is unreadable).</param>
    /// <param name="thisMachine">Name of this computer.</param>
    /// <param name="seenUtc">Creation time of the newest file from another computer that this one has imported or already overwritten knowingly.</param>
    public static SyncDecision BeforeWrite(BackupManifest? existing, string thisMachine, DateTime seenUtc)
    {
        if (existing is null) return SyncDecision.Write;
        return IsUnseenFromElsewhere(existing, thisMachine, seenUtc) ? SyncDecision.HoldBackConflict : SyncDecision.Write;
    }

    /// <summary>True when the file was written by another computer after the last one this computer has seen.</summary>
    public static bool IsUnseenFromElsewhere(BackupManifest existing, string thisMachine, DateTime seenUtc) =>
        !string.Equals(existing.Machine, thisMachine, StringComparison.OrdinalIgnoreCase) && existing.CreatedUtc > seenUtc;
}
