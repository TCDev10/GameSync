using GameSync.Core.Models;

namespace GameSync.Core.Abstractions.Sync;

public interface IConflictResolver
{
    /// <summary>
    /// Applies a conflict resolution without prompting. UI supplies the choice.
    /// </summary>
    Task ApplyAsync(string repositoryLocalPath, Conflict conflict, ConflictResolution resolution, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the same resolution to every listed conflict, then completes an in-progress
    /// merge commit when the index is clean. Resolving only the first conflict leaves the
    /// repository stuck and blocks every subsequent launch.
    /// </summary>
    Task ApplyAllAsync(
        string repositoryLocalPath,
        IReadOnlyList<Conflict> conflicts,
        ConflictResolution resolution,
        CancellationToken cancellationToken = default);

    ConflictResolution ToResolution(ConflictResolutionChoice choice);
}
