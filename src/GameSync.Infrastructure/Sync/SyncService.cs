using GameSync.Core.Abstractions;
using GameSync.Core.Abstractions.Configuration;
using GameSync.Core.Abstractions.Git;
using GameSync.Core.Abstractions.Repository;
using GameSync.Core.Abstractions.Sync;
using GameSync.Core.Errors;
using GameSync.Core.Models;
using GameSync.Core.Services;
using Microsoft.Extensions.Logging;

namespace GameSync.Infrastructure.Sync;

public sealed class SyncService : ISyncService, ISyncWorkflow
{
    private readonly IMachineConfigurationStore _machineStore;
    private readonly ISharedGamesConfigurationStore _gamesStore;
    private readonly IConfigurationValidator _validator;
    private readonly IRepositoryService _repositoryService;
    private readonly IGitService _gitService;
    private readonly ISaveService _saveService;
    private readonly IBackupService _backupService;
    private readonly IPathResolver _pathResolver;
    private readonly ILogger<SyncService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SyncService(
        IMachineConfigurationStore machineStore,
        ISharedGamesConfigurationStore gamesStore,
        IConfigurationValidator validator,
        IRepositoryService repositoryService,
        IGitService gitService,
        ISaveService saveService,
        IBackupService backupService,
        IPathResolver pathResolver,
        ILogger<SyncService> logger)
    {
        _machineStore = machineStore;
        _gamesStore = gamesStore;
        _validator = validator;
        _repositoryService = repositoryService;
        _gitService = gitService;
        _saveService = saveService;
        _backupService = backupService;
        _pathResolver = pathResolver;
        _logger = logger;
    }

    public Task<SyncResult> SyncBeforeGameLaunchAsync(string gameId, CancellationToken cancellationToken = default) =>
        WithGateAsync(() => SyncBeforeGameLaunchCoreAsync(gameId, cancellationToken), cancellationToken);

    public Task<SyncResult> SyncAfterGameExitAsync(string gameId, CancellationToken cancellationToken = default) =>
        WithGateAsync(() => SyncAfterGameExitCoreAsync(gameId, cancellationToken), cancellationToken);

    public Task<SyncResult> ResolveSaveDivergenceAsync(
        string gameId,
        ConflictResolution resolution,
        CancellationToken cancellationToken = default) =>
        WithGateAsync(() => ResolveSaveDivergenceCoreAsync(gameId, resolution, cancellationToken), cancellationToken);

    private async Task<SyncResult> SyncBeforeGameLaunchCoreAsync(string gameId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        _logger.LogInformation("SyncBeforeGameLaunch starting for {GameId}", gameId);

        try
        {
            var (machine, games, game, repoPath) = await LoadContextAsync(gameId, cancellationToken).ConfigureAwait(false);

            if (!await _repositoryService.IsLocalRepositoryReadyAsync(repoPath, cancellationToken).ConfigureAwait(false))
            {
                if (machine.Repository is null)
                {
                    throw new RepositoryUnavailableException("No repository is configured on this machine.");
                }

                var ensured = await _repositoryService.EnsureLocalRepositoryAsync(machine.Repository, cancellationToken).ConfigureAwait(false);
                repoPath = ensured.LocalPath ?? repoPath;
            }

            var preexistingConflicts = await _gitService.GetConflictsAsync(repoPath, cancellationToken).ConfigureAwait(false);
            if (preexistingConflicts.Count > 0)
            {
                return SyncResult.Failure(
                    SyncStatus.Conflicted,
                    "Repository already has unresolved Git conflicts. Resolve them before launching.",
                    gameId,
                    conflicts: preexistingConflicts);
            }

            var remoteReachable = true;
            try
            {
                await _gitService.FetchAsync(repoPath, cancellationToken).ConfigureAwait(false);
            }
            catch (GitPullFailedException ex)
            {
                // Network / auth failures must not block launching with local saves.
                remoteReachable = false;
                _logger.LogWarning(ex, "Fetch failed before launch for {GameId}; continuing with local saves", gameId);
            }

            var status = await _gitService.GetStatusAsync(repoPath, cancellationToken).ConfigureAwait(false);
            if (status.HasUncommittedChanges)
            {
                _logger.LogWarning("Local uncommitted Git changes detected before launch for {GameId}", gameId);
                var protectPaths = game.SaveLocations
                    .Select(s => _pathResolver.Resolve(s.LocalPath))
                    .ToArray();
                await _backupService.CreateBackupAsync(gameId, protectPaths, cancellationToken).ConfigureAwait(false);
            }

            if (remoteReachable)
            {
                try
                {
                    await _gitService.PullAsync(repoPath, cancellationToken).ConfigureAwait(false);
                }
                catch (GitConflictDetectedException ex)
                {
                    _logger.LogError("Git conflicts detected during pre-launch sync for {GameId}", gameId);
                    return SyncResult.Failure(
                        SyncStatus.Conflicted,
                        "Git conflicts detected. Resolve them before launching.",
                        gameId,
                        ex,
                        ex.Conflicts);
                }
                catch (GitPullFailedException ex)
                {
                    remoteReachable = false;
                    _logger.LogWarning(ex, "Pull failed before launch for {GameId}; continuing with local saves", gameId);
                }
            }

            var conflicts = await _gitService.GetConflictsAsync(repoPath, cancellationToken).ConfigureAwait(false);
            if (conflicts.Count > 0)
            {
                return SyncResult.Failure(
                    SyncStatus.Conflicted,
                    "Git conflicts detected. Resolve them before launching.",
                    gameId,
                    conflicts: conflicts);
            }

            if (!remoteReachable)
            {
                _logger.LogInformation(
                    "Launching {GameId} with local saves because the remote repository was unreachable",
                    gameId);
                return SyncResult.Success(
                    SyncStatus.LocalChanges,
                    "Could not reach GitHub. Launching with local saves; sync will retry after you exit.",
                    gameId);
            }

            // Do not silently overwrite divergent local saves with remote copies.
            // First-time cases:
            // - remote empty + local has saves → keep local; upload happens after exit
            // - remote has saves + local empty → restore remote
            // - both have content and differ → refuse
            var divergent = await _saveService.DetectChangesAsync(game, repoPath, cancellationToken).ConfigureAwait(false);
            var remoteHasSaves = _saveService.HasRepositorySaveContent(game, repoPath);
            var localHasSaves = _saveService.HasLocalSaveContent(game);

            if (divergent.HasChanges && remoteHasSaves && localHasSaves)
            {
                _logger.LogWarning(
                    "Local saves differ from repository for {GameId} ({Count} change(s)); refusing automatic overwrite",
                    gameId,
                    divergent.TotalChanges);
                var divergenceConflicts = BuildSaveDivergenceConflicts(game, divergent, repoPath, machine.MachineId);
                return SyncResult.Failure(
                    SyncStatus.Conflicted,
                    "Local and remote saves differ. Choose whether to keep local saves or replace them with remote saves.",
                    gameId,
                    conflicts: divergenceConflicts);
            }

            if (!remoteHasSaves)
            {
                _logger.LogInformation(
                    "No remote saves for {GameId} yet; launching with local saves (will upload after exit)",
                    gameId);
                return SyncResult.Success(
                    SyncStatus.LocalChanges,
                    "No remote saves yet. Your local saves will sync after you exit the game.",
                    gameId);
            }

            await _saveService.RestoreRepositoryToLocalAsync(game, repoPath, createBackup: true, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation("SyncBeforeGameLaunch completed for {GameId}", gameId);
            return SyncResult.Success(SyncStatus.UpToDate, "Remote saves restored before launch.", gameId);
        }
        catch (GameSyncException ex)
        {
            _logger.LogError(ex, "SyncBeforeGameLaunch failed for {GameId}", gameId);
            return SyncResult.Failure(SyncStatus.Failed, ex.Message, gameId, ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected SyncBeforeGameLaunch failure for {GameId}", gameId);
            return SyncResult.Failure(SyncStatus.Failed, "Unexpected sync failure before launch.", gameId, ex);
        }
    }

    private async Task<SyncResult> SyncAfterGameExitCoreAsync(string gameId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        _logger.LogInformation("SyncAfterGameExit starting for {GameId}", gameId);

        try
        {
            var (machine, _, game, repoPath) = await LoadContextAsync(gameId, cancellationToken).ConfigureAwait(false);

            var changes = await _saveService.DetectChangesAsync(game, repoPath, cancellationToken).ConfigureAwait(false);
            if (!changes.HasChanges)
            {
                _logger.LogInformation("No save changes detected after exit for {GameId}", gameId);
                return SyncResult.Success(SyncStatus.UpToDate, "No save changes detected.", gameId);
            }

            var localPaths = game.SaveLocations
                .Select(l => _pathResolver.Resolve(l.LocalPath))
                .Where(p => File.Exists(p) || Directory.Exists(p))
                .ToArray();
            if (localPaths.Length > 0)
            {
                await _backupService.CreateBackupAsync(gameId, localPaths, cancellationToken).ConfigureAwait(false);
            }

            // Integrate remote commits before rewriting the working tree / pushing.
            await _gitService.FetchAsync(repoPath, cancellationToken).ConfigureAwait(false);
            var remoteStatus = await _gitService.GetStatusAsync(repoPath, cancellationToken).ConfigureAwait(false);
            if (remoteStatus.SyncStatus is SyncStatus.BehindRemote or SyncStatus.Diverged or SyncStatus.Conflicted)
            {
                try
                {
                    await _gitService.PullAsync(repoPath, cancellationToken).ConfigureAwait(false);
                }
                catch (GitConflictDetectedException ex)
                {
                    return SyncResult.Failure(
                        SyncStatus.Conflicted,
                        "Remote changes conflict with this PC after gameplay. Resolve conflicts before pushing.",
                        gameId,
                        ex,
                        ex.Conflicts);
                }

                var pullConflicts = await _gitService.GetConflictsAsync(repoPath, cancellationToken).ConfigureAwait(false);
                if (pullConflicts.Count > 0)
                {
                    return SyncResult.Failure(
                        SyncStatus.Conflicted,
                        "Remote changes conflict with this PC after gameplay. Resolve conflicts before pushing.",
                        gameId,
                        conflicts: pullConflicts);
                }
            }

            await _saveService.CopyLocalToRepositoryAsync(game, repoPath, cancellationToken).ConfigureAwait(false);

            var stagedPaths = game.SaveLocations
                .Select(l => l.RemotePath.Replace('\\', '/'))
                .ToArray();
            await _gitService.AddAsync(repoPath, stagedPaths, cancellationToken).ConfigureAwait(false);

            var message = SyncCommitMessage.ForGameUpdate(game.Title, machine.MachineId);
            await _gitService.CommitAsync(repoPath, message, cancellationToken).ConfigureAwait(false);
            await _gitService.PushAsync(repoPath, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "SyncAfterGameExit completed for {GameId} with {ChangeCount} change(s)",
                gameId,
                changes.TotalChanges);

            return SyncResult.Success(
                SyncStatus.UpToDate,
                $"Pushed {changes.TotalChanges} save change(s).",
                gameId);
        }
        catch (GameSyncException ex)
        {
            _logger.LogError(ex, "SyncAfterGameExit failed for {GameId}", gameId);
            return SyncResult.Failure(SyncStatus.Failed, ex.Message, gameId, ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected SyncAfterGameExit failure for {GameId}", gameId);
            return SyncResult.Failure(SyncStatus.Failed, "Unexpected sync failure after exit.", gameId, ex);
        }
    }

    public async Task<SyncResult> SyncAllAsync(CancellationToken cancellationToken = default)
    {
        var machine = await _machineStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var repoPath = await ResolveRepositoryPathAsync(machine, cancellationToken).ConfigureAwait(false);
        var games = await _gamesStore.LoadAsync(repoPath, cancellationToken).ConfigureAwait(false);

        SyncResult? last = null;
        foreach (var game in games.Games)
        {
            last = await SyncGameAsync(game.Id, cancellationToken).ConfigureAwait(false);
            if (!last.Succeeded)
            {
                return last;
            }
        }

        return last ?? SyncResult.Success(SyncStatus.UpToDate, "No games configured.");
    }

    public async Task<SyncResult> SyncGameAsync(string gameId, CancellationToken cancellationToken = default)
    {
        var before = await SyncBeforeGameLaunchAsync(gameId, cancellationToken).ConfigureAwait(false);
        if (!before.Succeeded)
        {
            return before;
        }

        return await SyncAfterGameExitAsync(gameId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SyncResult> ResolveSaveDivergenceCoreAsync(
        string gameId,
        ConflictResolution resolution,
        CancellationToken cancellationToken)
    {
        if (resolution == ConflictResolution.Abort)
        {
            return SyncResult.Failure(SyncStatus.Conflicted, "Resolution cancelled.", gameId);
        }

        try
        {
            var (machine, _, game, repoPath) = await LoadContextAsync(gameId, cancellationToken).ConfigureAwait(false);

            var preexisting = await _gitService.GetConflictsAsync(repoPath, cancellationToken).ConfigureAwait(false);
            if (preexisting.Count > 0)
            {
                return SyncResult.Failure(
                    SyncStatus.Conflicted,
                    "Repository still has unresolved Git conflicts. Resolve every conflicted file, then try again.",
                    gameId,
                    conflicts: preexisting);
            }

            try
            {
                await _gitService.FetchAsync(repoPath, cancellationToken).ConfigureAwait(false);
                await _gitService.PullAsync(repoPath, cancellationToken).ConfigureAwait(false);
            }
            catch (GitPullFailedException ex)
            {
                // Divergence can still be resolved locally without a reachable remote.
                _logger.LogWarning(ex, "Fetch/pull failed while resolving divergence for {GameId}; continuing offline", gameId);
            }
            catch (GitConflictDetectedException ex)
            {
                return SyncResult.Failure(
                    SyncStatus.Conflicted,
                    "Git conflicts detected while resolving local/remote divergence.",
                    gameId,
                    ex,
                    ex.Conflicts);
            }

            if (resolution == ConflictResolution.Remote)
            {
                await _saveService.RestoreRepositoryToLocalAsync(game, repoPath, createBackup: true, cancellationToken)
                    .ConfigureAwait(false);
                return SyncResult.Success(
                    SyncStatus.UpToDate,
                    "Remote saves restored locally.",
                    gameId);
            }

            await _saveService.CopyLocalToRepositoryAsync(game, repoPath, cancellationToken).ConfigureAwait(false);
            var stagedPaths = game.SaveLocations
                .Select(l => l.RemotePath.Replace('\\', '/'))
                .ToArray();
            await _gitService.AddAsync(repoPath, stagedPaths, cancellationToken).ConfigureAwait(false);
            await _gitService.CommitAsync(repoPath, SyncCommitMessage.ForGameUpdate(game.Title, machine.MachineId), cancellationToken)
                .ConfigureAwait(false);
            await _gitService.PushAsync(repoPath, cancellationToken).ConfigureAwait(false);

            return SyncResult.Success(
                SyncStatus.UpToDate,
                "Local saves published to remote repository.",
                gameId);
        }
        catch (GitConflictDetectedException ex)
        {
            return SyncResult.Failure(
                SyncStatus.Conflicted,
                "Git conflicts detected while resolving local/remote divergence.",
                gameId,
                ex,
                ex.Conflicts);
        }
        catch (GameSyncException ex)
        {
            return SyncResult.Failure(SyncStatus.Failed, ex.Message, gameId, ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected divergence resolution failure for {GameId}", gameId);
            return SyncResult.Failure(SyncStatus.Failed, "Unexpected divergence resolution failure.", gameId, ex);
        }
    }

    public async Task<SyncStatus> GetStatusAsync(string? gameId = null, CancellationToken cancellationToken = default)
    {
        var machine = await _machineStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var repoPath = await ResolveRepositoryPathAsync(machine, cancellationToken).ConfigureAwait(false);
        if (!await _repositoryService.IsLocalRepositoryReadyAsync(repoPath, cancellationToken).ConfigureAwait(false))
        {
            return SyncStatus.Unknown;
        }

        var status = await _gitService.GetStatusAsync(repoPath, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return status.SyncStatus;
        }

        var games = await _gamesStore.LoadAsync(repoPath, cancellationToken).ConfigureAwait(false);
        var game = games.Games.FirstOrDefault(g => g.Id.Equals(gameId, StringComparison.OrdinalIgnoreCase));
        if (game is null)
        {
            return SyncStatus.Unknown;
        }

        var changes = await _saveService.DetectChangesAsync(game, repoPath, cancellationToken).ConfigureAwait(false);
        if (changes.HasChanges)
        {
            return SyncStatus.LocalChanges;
        }

        if (status.SyncStatus is SyncStatus.Conflicted or SyncStatus.Diverged)
        {
            return status.SyncStatus;
        }

        return SyncStatus.UpToDate;
    }

    public async Task<IReadOnlyDictionary<string, SyncStatus>> GetGameStatusesAsync(CancellationToken cancellationToken = default)
    {
        var machine = await _machineStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var repoPath = await ResolveRepositoryPathAsync(machine, cancellationToken).ConfigureAwait(false);
        if (!await _repositoryService.IsLocalRepositoryReadyAsync(repoPath, cancellationToken).ConfigureAwait(false))
        {
            return new Dictionary<string, SyncStatus>(StringComparer.OrdinalIgnoreCase);
        }

        var gitStatus = await _gitService.GetStatusAsync(repoPath, cancellationToken).ConfigureAwait(false);
        var games = await _gamesStore.LoadAsync(repoPath, cancellationToken).ConfigureAwait(false);
        var results = new Dictionary<string, SyncStatus>(StringComparer.OrdinalIgnoreCase);

        foreach (var game in games.Games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var changes = await _saveService.DetectChangesAsync(game, repoPath, cancellationToken).ConfigureAwait(false);
                var gameStatus = changes.HasChanges
                    ? SyncStatus.LocalChanges
                    : gitStatus.SyncStatus is SyncStatus.Conflicted or SyncStatus.Diverged
                        ? gitStatus.SyncStatus
                        : SyncStatus.UpToDate;
                results[game.Id] = gameStatus;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Status failed for {GameId}", game.Id);
                results[game.Id] = SyncStatus.Failed;
            }
        }

        return results;
    }

    private async Task<SyncResult> WithGateAsync(Func<Task<SyncResult>> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(MachineConfiguration Machine, GamesConfiguration Games, Game Game, string RepoPath)> LoadContextAsync(
        string gameId,
        CancellationToken cancellationToken)
    {
        var machine = await _machineStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var machineErrors = _validator.Validate(machine);
        if (machineErrors.Count > 0)
        {
            throw new ConfigurationValidationException(machineErrors);
        }

        var repoPath = await ResolveRepositoryPathAsync(machine, cancellationToken).ConfigureAwait(false);
        var games = await _gamesStore.LoadAsync(repoPath, cancellationToken).ConfigureAwait(false);
        var gameErrors = _validator.Validate(games);
        if (gameErrors.Count > 0)
        {
            throw new ConfigurationValidationException(gameErrors);
        }

        var game = games.Games.FirstOrDefault(g => g.Id.Equals(gameId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ConfigurationValidationException([$"Game '{gameId}' was not found in games.json."]);

        return (machine, games, game, repoPath);
    }

    private IReadOnlyList<Conflict> BuildSaveDivergenceConflicts(
        Game game,
        SaveChangesDetected changes,
        string repositoryLocalPath,
        string machineId)
    {
        var allPaths = changes.AddedFiles.Concat(changes.ChangedFiles).Concat(changes.DeletedFiles).Distinct(StringComparer.OrdinalIgnoreCase);
        var conflicts = new List<Conflict>();
        foreach (var relative in allPaths)
        {
            var split = relative.IndexOf('/');
            var locationId = split > 0 ? relative[..split] : relative;
            var locationRelative = split > 0 && split + 1 < relative.Length ? relative[(split + 1)..] : string.Empty;
            var location = game.SaveLocations.FirstOrDefault(s => s.Id.Equals(locationId, StringComparison.OrdinalIgnoreCase));
            if (location is null)
            {
                continue;
            }

            var remoteRelative = string.IsNullOrWhiteSpace(locationRelative)
                ? location.RemotePath.Replace('\\', '/')
                : $"{location.RemotePath.TrimEnd('/', '\\')}/{locationRelative}".Replace('\\', '/');
            var localRoot = _pathResolver.Resolve(location.LocalPath);
            var localPath = string.IsNullOrWhiteSpace(locationRelative)
                ? localRoot
                : Path.Combine(localRoot, locationRelative.Replace('/', Path.DirectorySeparatorChar));
            var remoteAbsolute = _pathResolver.MapRemotePathToRepository(repositoryLocalPath, remoteRelative);

            var localProbe = ConflictSideInfo.Probe(localPath);
            var remoteProbe = ConflictSideInfo.Probe(remoteAbsolute);

            conflicts.Add(new Conflict
            {
                Path = remoteRelative,
                Type = ConflictType.SaveDivergence,
                GameId = game.Id,
                SaveLocationId = location.Id,
                LocalPath = localPath,
                RemotePath = remoteRelative,
                Message = "Local and remote saves differ.",
                LocalLastWriteUtc = localProbe.LastWriteUtc,
                RemoteLastWriteUtc = remoteProbe.LastWriteUtc,
                LocalSizeBytes = localProbe.SizeBytes,
                RemoteSizeBytes = remoteProbe.SizeBytes,
                LocalExists = localProbe.Exists,
                RemoteExists = remoteProbe.Exists,
                LocalMachine = machineId,
                RemoteMachine = "GitHub"
            });
        }

        if (conflicts.Count > 0)
        {
            return conflicts;
        }

        var fallback = game.SaveLocations.FirstOrDefault();
        string? fallbackLocal = fallback is null ? null : _pathResolver.Resolve(fallback.LocalPath);
        string? fallbackRemoteAbs = null;
        string? fallbackRemoteRel = fallback?.RemotePath?.Replace('\\', '/');
        if (fallback is not null && !string.IsNullOrWhiteSpace(fallbackRemoteRel))
        {
            fallbackRemoteAbs = _pathResolver.MapRemotePathToRepository(repositoryLocalPath, fallbackRemoteRel);
        }

        var fbLocal = ConflictSideInfo.Probe(fallbackLocal);
        var fbRemote = ConflictSideInfo.Probe(fallbackRemoteAbs);
        return
        [
            new Conflict
            {
                Path = fallbackRemoteRel ?? game.Id,
                Type = ConflictType.SaveDivergence,
                GameId = game.Id,
                SaveLocationId = fallback?.Id,
                LocalPath = fallbackLocal,
                RemotePath = fallbackRemoteRel,
                Message = "Local and remote saves differ.",
                LocalLastWriteUtc = fbLocal.LastWriteUtc,
                RemoteLastWriteUtc = fbRemote.LastWriteUtc,
                LocalSizeBytes = fbLocal.SizeBytes,
                RemoteSizeBytes = fbRemote.SizeBytes,
                LocalExists = fbLocal.Exists,
                RemoteExists = fbRemote.Exists,
                LocalMachine = machineId,
                RemoteMachine = "GitHub"
            }
        ];
    }

    private async Task<string> ResolveRepositoryPathAsync(MachineConfiguration machine, CancellationToken cancellationToken)
    {
        if (machine.Repository is null)
        {
            throw new RepositoryUnavailableException("No repository is configured on this machine.");
        }

        if (!string.IsNullOrWhiteSpace(machine.Repository.LocalPath)
            && await _repositoryService.IsLocalRepositoryReadyAsync(machine.Repository.LocalPath, cancellationToken).ConfigureAwait(false))
        {
            return machine.Repository.LocalPath;
        }

        return _repositoryService.GetLocalRepositoryPath(machine.Repository.Owner, machine.Repository.Name);
    }
}
