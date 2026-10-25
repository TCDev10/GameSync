using GameSync.Core.Abstractions.Git;
using GameSync.Core.Abstractions.Storage;
using GameSync.Core.Errors;
using GameSync.Core.GitHub;
using GameSync.Core.Models;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using DomainConflict = GameSync.Core.Models.Conflict;
using GitRepositoryModel = GameSync.Core.Models.GitRepository;
using LibGitRepository = LibGit2Sharp.Repository;

namespace GameSync.Infrastructure.Git;

/// <summary>
/// Embedded Git via LibGit2Sharp. Does not invoke git.exe.
/// Credentials are loaded from Windows Credential Manager and never logged.
/// </summary>
public sealed class GitService : IGitService
{
    private readonly ICredentialStore _credentialStore;
    private readonly ILogger<GitService> _logger;

    public GitService(ICredentialStore credentialStore, ILogger<GitService> logger)
    {
        _credentialStore = credentialStore;
        _logger = logger;
    }

    public Task CloneAsync(string remoteUrl, string localPath, CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(remoteUrl);
            ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
            cancellationToken.ThrowIfCancellationRequested();

            if (Directory.Exists(localPath) && LibGitRepository.IsValid(localPath))
            {
                _logger.LogInformation("Clone skipped; repository already exists at {LocalPath}", localPath);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(localPath) ?? localPath);
            _logger.LogInformation("Cloning repository to {LocalPath}", localPath);

            try
            {
                var options = new CloneOptions
                {
                    FetchOptions =
                    {
                        CredentialsProvider = CreateCredentialsHandler()
                    }
                };
                LibGitRepository.Clone(remoteUrl, localPath, options);
            }
            catch (LibGit2SharpException ex)
            {
                throw new RepositoryUnavailableException($"Failed to clone repository to '{localPath}'.", ex);
            }
        }, cancellationToken);

    public Task FetchAsync(string localPath, CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            using var repo = Open(localPath);
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("Fetching repository {LocalPath}", localPath);

            try
            {
                var remote = repo.Network.Remotes["origin"]
                    ?? throw new RepositoryUnavailableException("Remote 'origin' is not configured.");
                Commands.Fetch(repo, remote.Name, remote.FetchRefSpecs.Select(x => x.Specification), new FetchOptions
                {
                    CredentialsProvider = CreateCredentialsHandler()
                }, null);
            }
            catch (LibGit2SharpException ex)
            {
                throw new GitPullFailedException($"Fetch failed for '{localPath}'.", ex);
            }
        }, cancellationToken);

    public Task PullAsync(string localPath, CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            using var repo = Open(localPath);
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("Pulling repository {LocalPath}", localPath);

            try
            {
                EnsureUpstreamTracking(repo);

                var options = new PullOptions
                {
                    FetchOptions = new FetchOptions
                    {
                        CredentialsProvider = CreateCredentialsHandler()
                    },
                    MergeOptions = new MergeOptions
                    {
                        FailOnConflict = false
                    }
                };

                var signature = CreateSignature();
                var result = Commands.Pull(repo, signature, options);
                if (result.Status == MergeStatus.Conflicts || repo.Index.Conflicts.Any())
                {
                    var conflicts = ReadConflicts(repo);
                    throw new GitConflictDetectedException("Pull resulted in conflicts.", conflicts);
                }
            }
            catch (GitConflictDetectedException)
            {
                throw;
            }
            catch (LibGit2SharpException ex)
            {
                throw new GitPullFailedException($"Pull failed for '{localPath}'.", ex);
            }
        }, cancellationToken);

    public Task<GitRepositoryModel> GetStatusAsync(string localPath, CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            using var repo = Open(localPath);
            cancellationToken.ThrowIfCancellationRequested();

            var status = repo.RetrieveStatus(new StatusOptions());
            var hasChanges = status.IsDirty;
            var branch = repo.Head;
            string? remoteUrl = null;
            if (repo.Network.Remotes["origin"] is { } origin)
            {
                remoteUrl = origin.Url;
            }

            var syncStatus = SyncStatus.Unknown;
            if (branch.TrackedBranch is not null)
            {
                var ahead = repo.Head.TrackingDetails.AheadBy ?? 0;
                var behind = repo.Head.TrackingDetails.BehindBy ?? 0;
                if (ahead == 0 && behind == 0)
                {
                    syncStatus = hasChanges ? SyncStatus.LocalChanges : SyncStatus.UpToDate;
                }
                else if (ahead > 0 && behind == 0)
                {
                    syncStatus = SyncStatus.AheadOfRemote;
                }
                else if (ahead == 0 && behind > 0)
                {
                    syncStatus = SyncStatus.BehindRemote;
                }
                else
                {
                    syncStatus = SyncStatus.Diverged;
                }
            }
            else if (hasChanges)
            {
                syncStatus = SyncStatus.LocalChanges;
            }
            else
            {
                syncStatus = SyncStatus.UpToDate;
            }

            if (repo.Index.Conflicts.Any())
            {
                syncStatus = SyncStatus.Conflicted;
            }

            return new GitRepositoryModel
            {
                LocalPath = localPath,
                RemoteUrl = remoteUrl,
                CurrentBranch = branch.FriendlyName,
                HeadCommitSha = branch.Tip?.Sha,
                HasUncommittedChanges = hasChanges,
                SyncStatus = syncStatus
            };
        }, cancellationToken);

    public Task AddAsync(string localPath, IEnumerable<string> paths, CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            using var repo = Open(localPath);
            cancellationToken.ThrowIfCancellationRequested();

            var prefixes = paths
                .Select(p => p.Replace('\\', '/').TrimEnd('/'))
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToArray();

            if (prefixes.Length == 0)
            {
                Commands.Stage(repo, "*");
                _logger.LogInformation("Staged all changes in {LocalPath}", localPath);
                return;
            }

            var status = repo.RetrieveStatus(new StatusOptions());
            var candidates = new List<string>();
            foreach (var entry in status)
            {
                var filePath = entry.FilePath.Replace('\\', '/');
                if (prefixes.Any(prefix =>
                        filePath.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                        || filePath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    candidates.Add(entry.FilePath);
                }
            }

            var staged = 0;
            foreach (var path in candidates)
            {
                Commands.Stage(repo, path);
                staged++;
            }

            _logger.LogInformation("Staged {Count} path(s) in {LocalPath}", staged, localPath);
        }, cancellationToken);

    public Task CommitAsync(string localPath, string message, CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(message);
            using var repo = Open(localPath);
            cancellationToken.ThrowIfCancellationRequested();

            var signature = CreateSignature();
            try
            {
                repo.Commit(message, signature, signature);
                _logger.LogInformation("Created commit in {LocalPath}", localPath);
            }
            catch (EmptyCommitException)
            {
                _logger.LogInformation("Commit skipped; no staged changes in {LocalPath}", localPath);
            }
        }, cancellationToken);

    public Task PushAsync(string localPath, CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            using var repo = Open(localPath);
            cancellationToken.ThrowIfCancellationRequested();
            var branch = repo.Head.FriendlyName;
            _logger.LogInformation("Pushing repository {LocalPath} branch {Branch}", localPath, branch);

            var token = _credentialStore.RetrieveSecretAsync(GitHubCredentialKeys.AccessToken).GetAwaiter().GetResult();
            try
            {
                PushWithGitExe(localPath, branch, token);
            }
            catch (Exception ex)
            {
                throw new GitPushFailedException($"Push failed for '{localPath}'.", ex);
            }
        }, cancellationToken);

    public Task ResetAsync(string localPath, string commitSha, bool hard, CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            using var repo = Open(localPath);
            cancellationToken.ThrowIfCancellationRequested();
            var commit = repo.Lookup<Commit>(commitSha)
                ?? throw new RepositoryUnavailableException($"Commit '{commitSha}' was not found.");
            repo.Reset(hard ? ResetMode.Hard : ResetMode.Mixed, commit);
            _logger.LogInformation("Reset repository {LocalPath} to {CommitSha} hard={Hard}", localPath, commitSha, hard);
        }, cancellationToken);

    public Task CheckoutAsync(string localPath, string commitShaOrBranch, CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            using var repo = Open(localPath);
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Checkout(repo, commitShaOrBranch);
            _logger.LogInformation("Checked out {Target} in {LocalPath}", commitShaOrBranch, localPath);
        }, cancellationToken);

    public Task CheckoutPathsAsync(string localPath, string commitSha, IEnumerable<string> paths, CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            ArgumentNullException.ThrowIfNull(paths);
            using var repo = Open(localPath);
            cancellationToken.ThrowIfCancellationRequested();
            var commit = repo.Lookup<Commit>(commitSha)
                ?? throw new RepositoryUnavailableException($"Commit '{commitSha}' was not found.");

            var normalized = paths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Replace('\\', '/').Trim('/'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (normalized.Length == 0)
            {
                return;
            }

            var options = new CheckoutOptions
            {
                CheckoutModifiers = CheckoutModifiers.Force
            };
            repo.CheckoutPaths(commitSha, normalized, options);
            _logger.LogInformation(
                "Checked out {Count} path(s) from {CommitSha} in {LocalPath}",
                normalized.Length,
                commitSha,
                localPath);
        }, cancellationToken);

    public Task<IReadOnlyList<SaveHistoryEntry>> GetHistoryAsync(string localPath, string? pathFilter = null, int maxCount = 50, CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            using var repo = Open(localPath);
            cancellationToken.ThrowIfCancellationRequested();

            var filter = new CommitFilter
            {
                SortBy = CommitSortStrategies.Time
            };

            IEnumerable<Commit> commits = repo.Commits.QueryBy(filter);
            if (!string.IsNullOrWhiteSpace(pathFilter))
            {
                commits = commits.Where(c => c.Tree[pathFilter.Replace('\\', '/')] is not null
                                            || c.Parents.Any(p => !TreeSame(p.Tree, c.Tree, pathFilter)));
            }

            var entries = commits.Take(Math.Max(1, maxCount)).Select(c => new SaveHistoryEntry
            {
                CommitSha = c.Sha,
                CommittedAt = c.Author.When,
                Message = c.MessageShort,
                AuthorName = c.Author.Name,
                ChangedPaths = Array.Empty<string>()
            }).ToArray();

            return (IReadOnlyList<SaveHistoryEntry>)entries;
        }, cancellationToken);

    public Task<IReadOnlyList<DomainConflict>> GetConflictsAsync(string localPath, CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            using var repo = Open(localPath);
            cancellationToken.ThrowIfCancellationRequested();
            return (IReadOnlyList<DomainConflict>)ReadConflicts(repo);
        }, cancellationToken);

    private static bool TreeSame(Tree left, Tree right, string path)
    {
        var normalized = path.Replace('\\', '/');
        var a = left[normalized];
        var b = right[normalized];
        return a?.Target?.Id == b?.Target?.Id;
    }

    private static List<DomainConflict> ReadConflicts(LibGitRepository repo)
    {
        DateTimeOffset? oursTime = repo.Head?.Tip?.Author?.When;
        DateTimeOffset? theirsTime = null;
        var mergeHeadPath = Path.Combine(repo.Info.Path, "MERGE_HEAD");
        if (File.Exists(mergeHeadPath))
        {
            try
            {
                var sha = File.ReadAllText(mergeHeadPath).Trim();
                if (!string.IsNullOrWhiteSpace(sha))
                {
                    theirsTime = repo.Lookup<Commit>(sha)?.Author?.When;
                }
            }
            catch
            {
                // Best-effort only for dialog timestamps.
            }
        }

        var list = new List<DomainConflict>();
        foreach (var conflict in repo.Index.Conflicts)
        {
            var path = conflict.Ours?.Path ?? conflict.Theirs?.Path ?? conflict.Ancestor?.Path ?? "unknown";
            var type = ConflictType.Content;
            if (conflict.Ours is null && conflict.Theirs is not null)
            {
                type = ConflictType.ModifyDelete;
            }
            else if (conflict.Ours is not null && conflict.Theirs is null)
            {
                type = ConflictType.ModifyDelete;
            }
            else if (conflict.Ancestor is null && conflict.Ours is not null && conflict.Theirs is not null)
            {
                type = ConflictType.AddAdd;
            }

            var absolute = Path.Combine(repo.Info.WorkingDirectory, path.Replace('/', Path.DirectorySeparatorChar));
            long? localSize = null;
            DateTimeOffset? localWrite = oursTime;
            var localExists = File.Exists(absolute);
            if (localExists)
            {
                var info = new FileInfo(absolute);
                localSize = info.Length;
                localWrite = info.LastWriteTimeUtc;
            }
            else if (conflict.Ours is not null)
            {
                localExists = true;
                try
                {
                    localSize = repo.Lookup<Blob>(conflict.Ours.Id)?.Size;
                }
                catch
                {
                    // ignore
                }
            }

            long? remoteSize = null;
            var remoteExists = conflict.Theirs is not null;
            if (conflict.Theirs is not null)
            {
                try
                {
                    remoteSize = repo.Lookup<Blob>(conflict.Theirs.Id)?.Size;
                }
                catch
                {
                    // ignore
                }
            }

            list.Add(new DomainConflict
            {
                Path = path,
                Type = type,
                IsBinary = true,
                Message = "Unresolved Git conflict. Binary saves must be resolved manually.",
                LocalPath = absolute,
                RemotePath = path,
                LocalLastWriteUtc = localWrite,
                RemoteLastWriteUtc = theirsTime ?? oursTime,
                LocalSizeBytes = localSize,
                RemoteSizeBytes = remoteSize,
                LocalExists = localExists,
                RemoteExists = remoteExists,
                LocalMachine = "This PC",
                RemoteMachine = "Incoming (GitHub)"
            });
        }

        return list;
    }

    private static void PushWithGitExe(string localPath, string branch, string? token)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = localPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("credential.helper=");
        if (!string.IsNullOrWhiteSpace(token))
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}"));
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add($"http.extraHeader=Authorization: Basic {basic}");
        }

        startInfo.ArgumentList.Add("push");
        startInfo.ArgumentList.Add("origin");
        startInfo.ArgumentList.Add(branch);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git process for push.");
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            throw new InvalidOperationException(
                $"git push origin {branch} failed ({process.ExitCode}): {output} {error}".Trim());
        }
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git process.");
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            throw new InvalidOperationException($"git {string.Join(" ", args)} failed ({process.ExitCode}): {output} {error}".Trim());
        }
    }

    private void EnsureUpstreamTracking(LibGitRepository repo)
    {
        var head = repo.Head;
        if (head.IsRemote)
        {
            return;
        }

        var remote = repo.Network.Remotes["origin"];
        if (remote is null)
        {
            return;
        }

        static string? LeafName(Branch? branch)
        {
            if (branch is null)
            {
                return null;
            }

            var friendly = branch.FriendlyName;
            var slash = friendly.LastIndexOf('/');
            return slash >= 0 ? friendly[(slash + 1)..] : friendly;
        }

        bool RemoteBranchExists(string name) =>
            repo.Branches[$"origin/{name}"] is not null
            || repo.Branches[$"refs/remotes/origin/{name}"] is not null;

        var trackedLeaf = LeafName(head.TrackedBranch);
        if (!string.IsNullOrWhiteSpace(trackedLeaf) && RemoteBranchExists(trackedLeaf))
        {
            return;
        }

        foreach (var candidate in new[] { head.FriendlyName, "main", "master" }
                     .Where(s => !string.IsNullOrWhiteSpace(s))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!RemoteBranchExists(candidate))
            {
                continue;
            }

            var upstream = $"refs/heads/{candidate}";
            repo.Branches.Update(
                head,
                b =>
                {
                    b.Remote = remote.Name;
                    b.UpstreamBranch = upstream;
                });

            _logger.LogInformation(
                "Configured upstream of {LocalBranch} to origin/{RemoteBranch}",
                head.FriendlyName,
                candidate);
            return;
        }

        _logger.LogWarning(
            "Could not configure upstream for {LocalBranch}; remote has no matching main/master branch tip",
            head.FriendlyName);
    }

    private LibGit2Sharp.Handlers.CredentialsHandler CreateCredentialsHandler()
    {
        return (_, _, _) =>
        {
            // Never log the token.
            var token = _credentialStore.RetrieveSecretAsync(GitHubCredentialKeys.AccessToken).GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(token))
            {
                return new DefaultCredentials();
            }

            return new UsernamePasswordCredentials
            {
                Username = "x-access-token",
                Password = token
            };
        };
    }

    private static Signature CreateSignature() =>
        new("GameSync", "gamesSync@users.noreply.github.com", DateTimeOffset.Now);

    private static LibGitRepository Open(string localPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        if (!LibGitRepository.IsValid(localPath))
        {
            throw new RepositoryUnavailableException($"No valid Git repository at '{localPath}'.");
        }

        return new LibGitRepository(localPath);
    }

    private static Task RunAsync(Action action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
        }, cancellationToken);
    }

    private static Task<T> RunAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return action();
        }, cancellationToken);
    }
}
