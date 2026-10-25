using GameSync.Core.Abstractions;
using GameSync.Core.Abstractions.Sync;
using GameSync.Core.Errors;
using GameSync.Core.Models;
using GameSync.Core.Services;
using GameSync.Infrastructure.IO;
using Microsoft.Extensions.Logging;

namespace GameSync.Infrastructure.Sync;

public sealed class SaveService : ISaveService, ISaveSyncService
{
    private readonly IPathResolver _pathResolver;
    private readonly IBackupService _backupService;
    private readonly ILogger<SaveService> _logger;

    public SaveService(IPathResolver pathResolver, IBackupService backupService, ILogger<SaveService> logger)
    {
        _pathResolver = pathResolver;
        _backupService = backupService;
        _logger = logger;
    }

    public Task CopyLocalSavesToRepositoryAsync(Game game, string repositoryLocalPath, CancellationToken cancellationToken = default) =>
        CopyLocalToRepositoryAsync(game, repositoryLocalPath, cancellationToken);

    public Task RestoreSavesFromRepositoryAsync(Game game, string repositoryLocalPath, CancellationToken cancellationToken = default) =>
        RestoreRepositoryToLocalAsync(game, repositoryLocalPath, createBackup: true, cancellationToken);

    public async Task<bool> HasLocalSaveChangesAsync(Game game, string repositoryLocalPath, CancellationToken cancellationToken = default)
    {
        var changes = await DetectChangesAsync(game, repositoryLocalPath, cancellationToken).ConfigureAwait(false);
        return changes.HasChanges;
    }

    public Task CopyLocalToRepositoryAsync(Game game, string repositoryLocalPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryLocalPath);

        foreach (var location in game.SaveLocations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var local = _pathResolver.Resolve(location.LocalPath);
            if (!_pathResolver.IsAllowedLocalSaveTarget(local))
            {
                throw new ConfigurationValidationException(
                    [$"Save path for '{game.Id}/{location.Id}' is not allowed: '{local}'."]);
            }

            var remote = _pathResolver.MapRemotePathToRepository(repositoryLocalPath, location.RemotePath);

            if (location.Type == SaveLocationType.File)
            {
                if (!File.Exists(local))
                {
                    _logger.LogWarning("Local save file missing for {GameId}/{SaveId}: {Path}", game.Id, location.Id, local);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(remote)!);
                AtomicFile.Copy(local, remote, cancellationToken);
            }
            else
            {
                if (!Directory.Exists(local))
                {
                    _logger.LogWarning("Local save directory missing for {GameId}/{SaveId}: {Path}", game.Id, location.Id, local);
                    continue;
                }

                // Destructive mirror is only for an intentional "replace remote with local tree"
                // (e.g. Keep Local when the user chose to discard cloud-only paths). Prefer
                // MergeNonConflictingSavesAsync / ResolveContentConflictsAsync for normal sync.
                MirrorDirectory(local, remote, deleteExtraneous: true, cancellationToken);
            }

            _logger.LogInformation("Copied local saves {GameId}/{SaveId} to repository", game.Id, location.Id);
        }

        return Task.CompletedTask;
    }

    public async Task RestoreRepositoryToLocalAsync(Game game, string repositoryLocalPath, bool createBackup = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryLocalPath);

        var existingLocals = new List<string>();
        foreach (var location in game.SaveLocations)
        {
            var local = _pathResolver.Resolve(location.LocalPath);
            if (!_pathResolver.IsAllowedLocalSaveTarget(local))
            {
                throw new ConfigurationValidationException(
                    [$"Save path for '{game.Id}/{location.Id}' is not allowed: '{local}'."]);
            }

            if (File.Exists(local) || Directory.Exists(local))
            {
                existingLocals.Add(local);
            }
        }

        if (createBackup && existingLocals.Count > 0)
        {
            await _backupService.CreateBackupAsync(game.Id, existingLocals, cancellationToken).ConfigureAwait(false);
        }

        foreach (var location in game.SaveLocations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var local = _pathResolver.Resolve(location.LocalPath);
            var remote = _pathResolver.MapRemotePathToRepository(repositoryLocalPath, location.RemotePath);

            if (location.Type == SaveLocationType.File)
            {
                if (!File.Exists(remote))
                {
                    throw new SavePathNotFoundException($"Repository save file not found for '{game.Id}/{location.Id}'.", remote);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                AtomicFile.Copy(remote, local, cancellationToken);
            }
            else
            {
                if (!Directory.Exists(remote))
                {
                    // Empty remote directory is allowed — create empty local if needed.
                    Directory.CreateDirectory(local);
                    continue;
                }

                Directory.CreateDirectory(local);
                MirrorDirectory(remote, local, deleteExtraneous: true, cancellationToken);
            }

            _logger.LogInformation("Restored repository saves {GameId}/{SaveId} to local path", game.Id, location.Id);
        }
    }

    public Task<SaveChangesDetected> DetectChangesAsync(Game game, string repositoryLocalPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryLocalPath);

        var added = new List<string>();
        var changed = new List<string>();
        var deleted = new List<string>();

        foreach (var location in game.SaveLocations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var local = _pathResolver.Resolve(location.LocalPath);
            var remote = _pathResolver.MapRemotePathToRepository(repositoryLocalPath, location.RemotePath);
            var localExists = location.Type == SaveLocationType.File
                ? File.Exists(local)
                : Directory.Exists(local);

            if (!localExists)
            {
                _logger.LogDebug(
                    "Skipping change detection for {GameId}/{SaveId}; local path does not exist: {Path}",
                    game.Id,
                    location.Id,
                    local);
                continue;
            }

            var prefix = $"{location.Id}/";

            var comparison = FileTreeComparer.Compare(local, remote, location.Type);
            added.AddRange(comparison.AddedFiles.Select(f => prefix + f));
            changed.AddRange(comparison.ChangedFiles.Select(f => prefix + f));
            deleted.AddRange(comparison.DeletedFiles.Select(f => prefix + f));
        }

        return Task.FromResult(new SaveChangesDetected
        {
            AddedFiles = added,
            ChangedFiles = changed,
            DeletedFiles = deleted
        });
    }

    public async Task MergeNonConflictingSavesAsync(Game game, string repositoryLocalPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryLocalPath);

        foreach (var location in game.SaveLocations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureAllowed(game, location);

            var local = _pathResolver.Resolve(location.LocalPath);
            var remote = _pathResolver.MapRemotePathToRepository(repositoryLocalPath, location.RemotePath);

            if (location.Type == SaveLocationType.File)
            {
                MergeFileLocation(game, location, local, remote, cancellationToken);
                continue;
            }

            // Ensure both roots exist when either side has content.
            var localExists = Directory.Exists(local);
            var remoteExists = Directory.Exists(remote);
            if (!localExists && !remoteExists)
            {
                continue;
            }

            if (!localExists)
            {
                Directory.CreateDirectory(local);
            }

            if (!remoteExists)
            {
                Directory.CreateDirectory(remote);
            }

            var comparison = FileTreeComparer.Compare(local, remote, SaveLocationType.Directory);

            // Local-only → copy into repository (keep remote-only intact).
            foreach (var relative in comparison.AddedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CopyRelativeFile(local, remote, relative, cancellationToken);
            }

            // Remote-only → copy into local (keep local-only intact).
            foreach (var relative in comparison.DeletedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CopyRelativeFile(remote, local, relative, cancellationToken);
            }

            if (comparison.AddedFiles.Count > 0 || comparison.DeletedFiles.Count > 0)
            {
                _logger.LogInformation(
                    "Union-merged {GameId}/{SaveId}: {LocalOnly} local-only, {RemoteOnly} remote-only (skipped {Conflicts} content conflict(s))",
                    game.Id,
                    location.Id,
                    comparison.AddedFiles.Count,
                    comparison.DeletedFiles.Count,
                    comparison.ChangedFiles.Count);
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task ResolveContentConflictsAsync(
        Game game,
        string repositoryLocalPath,
        ConflictResolution resolution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryLocalPath);
        if (resolution is not (ConflictResolution.Local or ConflictResolution.Remote))
        {
            throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "Expected Local or Remote.");
        }

        var changes = await DetectChangesAsync(game, repositoryLocalPath, cancellationToken).ConfigureAwait(false);

        // Apply the user's choice only to paths that exist on both sides with different bytes.
        foreach (var prefixed in changes.ChangedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TrySplitPrefixedPath(game, prefixed, out var location, out var locationRelative))
            {
                continue;
            }

            EnsureAllowed(game, location);
            var localRoot = _pathResolver.Resolve(location.LocalPath);
            var remoteRoot = _pathResolver.MapRemotePathToRepository(repositoryLocalPath, location.RemotePath);

            string localFile;
            string remoteFile;
            if (location.Type == SaveLocationType.File)
            {
                localFile = localRoot;
                remoteFile = remoteRoot;
            }
            else
            {
                localFile = Path.Combine(localRoot, locationRelative.Replace('/', Path.DirectorySeparatorChar));
                remoteFile = Path.Combine(remoteRoot, locationRelative.Replace('/', Path.DirectorySeparatorChar));
            }

            if (resolution == ConflictResolution.Local)
            {
                if (!File.Exists(localFile))
                {
                    _logger.LogWarning("Keep-local skipped missing local file {Path}", localFile);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(remoteFile)!);
                AtomicFile.Copy(localFile, remoteFile, cancellationToken);
            }
            else
            {
                if (!File.Exists(remoteFile))
                {
                    _logger.LogWarning("Keep-remote skipped missing remote file {Path}", remoteFile);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(localFile)!);
                AtomicFile.Copy(remoteFile, localFile, cancellationToken);
            }
        }

        // Always preserve unique slots/runs on both sides after resolving true conflicts.
        await MergeNonConflictingSavesAsync(game, repositoryLocalPath, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Resolved {Count} content conflict(s) for {GameId} using {Resolution}; unique paths union-merged",
            changes.ChangedFiles.Count,
            game.Id,
            resolution);
    }

    public bool HasRepositorySaveContent(Game game, string repositoryLocalPath)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryLocalPath);

        foreach (var location in game.SaveLocations)
        {
            var remote = _pathResolver.MapRemotePathToRepository(repositoryLocalPath, location.RemotePath);
            if (location.Type == SaveLocationType.File)
            {
                if (File.Exists(remote))
                {
                    return true;
                }

                continue;
            }

            if (Directory.Exists(remote) && FileTreeComparer.EnumerateRelativeFiles(remote).Count > 0)
            {
                return true;
            }
        }

        return false;
    }

    public bool HasLocalSaveContent(Game game)
    {
        ArgumentNullException.ThrowIfNull(game);

        foreach (var location in game.SaveLocations)
        {
            var local = _pathResolver.Resolve(location.LocalPath);
            if (location.Type == SaveLocationType.File)
            {
                if (File.Exists(local))
                {
                    return true;
                }

                continue;
            }

            if (Directory.Exists(local) && FileTreeComparer.EnumerateRelativeFiles(local).Count > 0)
            {
                return true;
            }
        }

        return false;
    }

    private void MergeFileLocation(
        Game game,
        SaveLocation location,
        string local,
        string remote,
        CancellationToken cancellationToken)
    {
        var localExists = File.Exists(local);
        var remoteExists = File.Exists(remote);

        if (localExists && !remoteExists)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(remote)!);
            AtomicFile.Copy(local, remote, cancellationToken);
            _logger.LogInformation("Union-copied local-only file {GameId}/{SaveId} into repository", game.Id, location.Id);
            return;
        }

        if (!localExists && remoteExists)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            AtomicFile.Copy(remote, local, cancellationToken);
            _logger.LogInformation("Union-copied remote-only file {GameId}/{SaveId} into local path", game.Id, location.Id);
        }

        // Both missing, or both present (equal or conflicting) — leave untouched here.
    }

    private void EnsureAllowed(Game game, SaveLocation location)
    {
        var local = _pathResolver.Resolve(location.LocalPath);
        if (!_pathResolver.IsAllowedLocalSaveTarget(local))
        {
            throw new ConfigurationValidationException(
                [$"Save path for '{game.Id}/{location.Id}' is not allowed: '{local}'."]);
        }
    }

    private static bool TrySplitPrefixedPath(
        Game game,
        string prefixedRelative,
        out SaveLocation location,
        out string locationRelative)
    {
        location = null!;
        locationRelative = string.Empty;
        var split = prefixedRelative.IndexOf('/');
        var locationId = split > 0 ? prefixedRelative[..split] : prefixedRelative;
        locationRelative = split > 0 && split + 1 < prefixedRelative.Length ? prefixedRelative[(split + 1)..] : string.Empty;
        var match = game.SaveLocations.FirstOrDefault(s => s.Id.Equals(locationId, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return false;
        }

        location = match;
        return true;
    }

    private static void CopyRelativeFile(string sourceRoot, string destRoot, string relative, CancellationToken cancellationToken)
    {
        var sourceFile = Path.Combine(sourceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        var destFile = Path.Combine(destRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(sourceFile))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
        AtomicFile.Copy(sourceFile, destFile, cancellationToken);
    }

    private static void MirrorDirectory(string sourceDir, string destDir, bool deleteExtraneous, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destDir);

        foreach (var directory in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceDir, directory);
            Directory.CreateDirectory(Path.Combine(destDir, relative));
        }

        var sourceFiles = FileTreeComparer.EnumerateRelativeFiles(sourceDir);
        foreach (var relative in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceFile = Path.Combine(sourceDir, relative.Replace('/', Path.DirectorySeparatorChar));
            var destFile = Path.Combine(destDir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            AtomicFile.Copy(sourceFile, destFile, cancellationToken);
        }

        if (!deleteExtraneous)
        {
            return;
        }

        var destFiles = FileTreeComparer.EnumerateRelativeFiles(destDir);
        foreach (var relative in destFiles)
        {
            if (sourceFiles.Contains(relative))
            {
                continue;
            }

            var destFile = Path.Combine(destDir, relative.Replace('/', Path.DirectorySeparatorChar));
            File.Delete(destFile);
        }

        // Remove empty directories left behind after deletions.
        foreach (var directory in Directory.EnumerateDirectories(destDir, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
    }
}
