using GameSync.Core.Abstractions.Sync;
using GameSync.Core.Errors;
using GameSync.Core.Models;
using GameSync.Core.Services;
using GameSync.Infrastructure.IO;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using DomainConflict = GameSync.Core.Models.Conflict;
using LibGitRepository = LibGit2Sharp.Repository;

namespace GameSync.Infrastructure.Sync;

/// <summary>
/// Applies Local / Remote / Abort resolutions. Never silently merges binary saves.
/// </summary>
public sealed class ConflictResolver : IConflictResolver
{
    private readonly IBackupService _backupService;
    private readonly ILogger<ConflictResolver> _logger;

    public ConflictResolver(IBackupService backupService, ILogger<ConflictResolver> logger)
    {
        _backupService = backupService;
        _logger = logger;
    }

    public ConflictResolution ToResolution(ConflictResolutionChoice choice) =>
        choice switch
        {
            ConflictResolutionChoice.UseLocal => ConflictResolution.Local,
            ConflictResolutionChoice.UseRemote => ConflictResolution.Remote,
            ConflictResolutionChoice.Cancel => ConflictResolution.Abort,
            ConflictResolutionChoice.ViewHistory => ConflictResolution.Abort,
            _ => ConflictResolution.Abort
        };

    public async Task ApplyAsync(
        string repositoryLocalPath,
        DomainConflict conflict,
        ConflictResolution resolution,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryLocalPath);
        ArgumentNullException.ThrowIfNull(conflict);
        cancellationToken.ThrowIfCancellationRequested();

        if (resolution == ConflictResolution.Abort)
        {
            _logger.LogInformation("Conflict resolution aborted for {Path}", conflict.Path);
            return;
        }

        if (!LibGitRepository.IsValid(repositoryLocalPath))
        {
            throw new RepositoryUnavailableException($"No valid Git repository at '{repositoryLocalPath}'.");
        }

        using var repo = new LibGitRepository(repositoryLocalPath);
        await ApplyOneAsync(repo, conflict, resolution, cancellationToken).ConfigureAwait(false);
        repo.Index.Write();
        _logger.LogInformation("Applied {Resolution} for conflict {Path}", resolution, conflict.Path);
    }

    public async Task ApplyAllAsync(
        string repositoryLocalPath,
        IReadOnlyList<DomainConflict> conflicts,
        ConflictResolution resolution,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryLocalPath);
        ArgumentNullException.ThrowIfNull(conflicts);
        cancellationToken.ThrowIfCancellationRequested();

        if (resolution == ConflictResolution.Abort)
        {
            _logger.LogInformation("Conflict resolution aborted for {Count} path(s)", conflicts.Count);
            return;
        }

        if (!LibGitRepository.IsValid(repositoryLocalPath))
        {
            throw new RepositoryUnavailableException($"No valid Git repository at '{repositoryLocalPath}'.");
        }

        using var repo = new LibGitRepository(repositoryLocalPath);

        // Prefer the full index conflict list so leftover entries are not left behind.
        var pending = conflicts.Count > 0
            ? conflicts
            : ReadIndexConflicts(repo);

        if (pending.Count == 0)
        {
            pending = ReadIndexConflicts(repo);
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var conflict in pending)
        {
            if (!paths.Add(conflict.Path))
            {
                continue;
            }

            await ApplyOneAsync(repo, conflict, resolution, cancellationToken).ConfigureAwait(false);
        }

        // Sweep any remaining index conflicts with the same choice (UI may only have listed one).
        foreach (var leftover in ReadIndexConflicts(repo))
        {
            if (!paths.Add(leftover.Path))
            {
                continue;
            }

            await ApplyOneAsync(repo, leftover, resolution, cancellationToken).ConfigureAwait(false);
        }

        repo.Index.Write();

        if (repo.Index.Conflicts.Any())
        {
            var remaining = ReadIndexConflicts(repo);
            throw new GitConflictDetectedException(
                $"Still have {remaining.Count} unresolved Git conflict(s) after applying {resolution}.",
                remaining);
        }

        CompleteMergeIfNeeded(repo);
        _logger.LogInformation(
            "Applied {Resolution} to {Count} conflict path(s) and completed merge if needed",
            resolution,
            paths.Count);
    }

    private async Task ApplyOneAsync(
        LibGitRepository repo,
        DomainConflict conflict,
        ConflictResolution resolution,
        CancellationToken cancellationToken)
    {
        var absolute = Path.Combine(repo.Info.WorkingDirectory, conflict.Path.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(absolute))
        {
            var gameId = conflict.GameId ?? "conflict";
            await _backupService.CreateBackupAsync(gameId, [absolute], cancellationToken).ConfigureAwait(false);
        }

        var entry = repo.Index.Conflicts[conflict.Path];
        if (entry is null)
        {
            // Already resolved or not a Git index conflict (e.g. SaveDivergence-only entry).
            _logger.LogDebug("No Git index conflict entry for {Path}; skipping", conflict.Path);
            return;
        }

        IndexEntry? chosen = resolution == ConflictResolution.Local ? entry.Ours : entry.Theirs;
        if (chosen is null)
        {
            if (File.Exists(absolute))
            {
                File.Delete(absolute);
            }

            repo.Index.Remove(conflict.Path);
        }
        else
        {
            var blob = repo.Lookup<Blob>(chosen.Id);
            WriteBlob(absolute, blob, cancellationToken);
            repo.Index.Add(conflict.Path);
        }
    }

    private static void CompleteMergeIfNeeded(LibGitRepository repo)
    {
        // MERGE_HEAD present means we still need a merge commit to leave the conflicted state.
        var mergeHead = Path.Combine(repo.Info.Path, "MERGE_HEAD");
        if (!File.Exists(mergeHead))
        {
            return;
        }

        var signature = new Signature("GameSync", "gamesync@local", DateTimeOffset.Now);
        try
        {
            repo.Commit(SyncCommitMessage.ForConflictResolution(), signature, signature);
        }
        catch (EmptyCommitException)
        {
            // Index already matched HEAD; clear merge state by committing is not possible — abort merge markers.
            // LibGit2Sharp has no Abort; remove merge state files when tree is clean.
            TryClearMergeState(repo.Info.Path);
        }
    }

    private static void TryClearMergeState(string gitDir)
    {
        foreach (var name in new[] { "MERGE_HEAD", "MERGE_MSG", "MERGE_MODE" })
        {
            var path = Path.Combine(gitDir, name);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static IReadOnlyList<DomainConflict> ReadIndexConflicts(LibGitRepository repo)
    {
        var list = new List<DomainConflict>();
        foreach (var conflict in repo.Index.Conflicts)
        {
            var path = conflict.Ours?.Path ?? conflict.Theirs?.Path ?? conflict.Ancestor?.Path ?? "unknown";
            list.Add(new DomainConflict
            {
                Path = path,
                Type = ConflictType.Content,
                IsBinary = true,
                Message = "Unresolved Git conflict. Binary saves must be resolved manually."
            });
        }

        return list;
    }

    private static void WriteBlob(string absolute, Blob? blob, CancellationToken cancellationToken)
    {
        if (blob is null)
        {
            return;
        }

        using var stream = blob.GetContentStream();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        AtomicFile.WriteAllBytes(absolute, ms.ToArray(), cancellationToken);
    }
}
