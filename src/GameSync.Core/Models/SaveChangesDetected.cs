namespace GameSync.Core.Models;

/// <summary>
/// Structured result of comparing configured game save locations to the repository copy.
/// Added = local-only, Deleted = remote-only, Changed = same relative path with different bytes.
/// </summary>
public sealed class SaveChangesDetected
{
    public static SaveChangesDetected Empty { get; } = new()
    {
        ChangedFiles = Array.Empty<string>(),
        AddedFiles = Array.Empty<string>(),
        DeletedFiles = Array.Empty<string>()
    };

    public IReadOnlyList<string> ChangedFiles { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> AddedFiles { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> DeletedFiles { get; init; } = Array.Empty<string>();

    public bool HasChanges => ChangedFiles.Count > 0 || AddedFiles.Count > 0 || DeletedFiles.Count > 0;

    public int TotalChanges => ChangedFiles.Count + AddedFiles.Count + DeletedFiles.Count;

    /// <summary>True when at least one relative path exists on both sides with different content.</summary>
    public bool HasContentConflicts => ChangedFiles.Count > 0;

    /// <summary>True when sides only differ by unique paths (safe to keep both without a dialog).</summary>
    public bool CanAutoUnion => HasChanges && !HasContentConflicts;
}
