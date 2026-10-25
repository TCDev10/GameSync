using FluentAssertions;
using GameSync.Core.Abstractions.Sync;
using GameSync.Core.Models;
using GameSync.Infrastructure.Paths;
using GameSync.Infrastructure.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace GameSync.Infrastructure.Tests.Sync;

public sealed class SaveServiceTests
{
    [Fact]
    public async Task CopyLocalToRepository_CopiesDirectoryAndFile()
    {
        var root = CreateRoot();
        try
        {
            var localDir = Path.Combine(root, "local", "saves");
            Directory.CreateDirectory(localDir);
            File.WriteAllText(Path.Combine(localDir, "slot1.sav"), "save");
            var localFile = Path.Combine(root, "local", "settings.dat");
            File.WriteAllText(localFile, "cfg");

            var repo = Path.Combine(root, "repo");
            Directory.CreateDirectory(repo);

            var backup = Substitute.For<IBackupService>();
            var service = new SaveService(new PathResolver(), backup, NullLogger<SaveService>.Instance);
            var game = CreateGame(localDir, localFile);

            await service.CopyLocalToRepositoryAsync(game, repo);

            File.Exists(Path.Combine(repo, "saves", "demo_game", "main", "slot1.sav")).Should().BeTrue();
            File.Exists(Path.Combine(repo, "saves", "demo_game", "settings.dat")).Should().BeTrue();
            File.ReadAllText(Path.Combine(repo, "saves", "demo_game", "settings.dat")).Should().Be("cfg");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RestoreRepositoryToLocal_CreatesBackupAndOverwrites()
    {
        var root = CreateRoot();
        try
        {
            var localDir = Path.Combine(root, "local", "saves");
            Directory.CreateDirectory(localDir);
            File.WriteAllText(Path.Combine(localDir, "slot1.sav"), "old");
            var localFile = Path.Combine(root, "local", "settings.dat");
            File.WriteAllText(localFile, "oldcfg");

            var repo = Path.Combine(root, "repo");
            Directory.CreateDirectory(Path.Combine(repo, "saves", "demo_game", "main"));
            File.WriteAllText(Path.Combine(repo, "saves", "demo_game", "main", "slot1.sav"), "new");
            Directory.CreateDirectory(Path.Combine(repo, "saves", "demo_game"));
            File.WriteAllText(Path.Combine(repo, "saves", "demo_game", "settings.dat"), "newcfg");

            var backup = Substitute.For<IBackupService>();
            backup.CreateBackupAsync(Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
                .Returns(Path.Combine(root, "backup"));

            var service = new SaveService(new PathResolver(), backup, NullLogger<SaveService>.Instance);
            var game = CreateGame(localDir, localFile);

            await service.RestoreRepositoryToLocalAsync(game, repo, createBackup: true);

            await backup.Received(1).CreateBackupAsync("demo_game", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
            File.ReadAllText(Path.Combine(localDir, "slot1.sav")).Should().Be("new");
            File.ReadAllText(localFile).Should().Be("newcfg");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DetectChanges_ReportsAddedChangedDeleted()
    {
        var root = CreateRoot();
        try
        {
            var localDir = Path.Combine(root, "local", "saves");
            Directory.CreateDirectory(localDir);
            File.WriteAllText(Path.Combine(localDir, "added.sav"), "a");
            File.WriteAllText(Path.Combine(localDir, "changed.sav"), "v2");
            var localFile = Path.Combine(root, "local", "settings.dat");
            File.WriteAllText(localFile, "same");

            var repo = Path.Combine(root, "repo");
            Directory.CreateDirectory(Path.Combine(repo, "saves", "demo_game", "main"));
            File.WriteAllText(Path.Combine(repo, "saves", "demo_game", "main", "changed.sav"), "v1");
            File.WriteAllText(Path.Combine(repo, "saves", "demo_game", "main", "deleted.sav"), "gone");
            Directory.CreateDirectory(Path.Combine(repo, "saves", "demo_game"));
            File.WriteAllText(Path.Combine(repo, "saves", "demo_game", "settings.dat"), "same");

            var service = new SaveService(new PathResolver(), Substitute.For<IBackupService>(), NullLogger<SaveService>.Instance);
            var changes = await service.DetectChangesAsync(CreateGame(localDir, localFile), repo);

            changes.AddedFiles.Should().Contain(x => x.Contains("added.sav", StringComparison.Ordinal));
            changes.ChangedFiles.Should().Contain(x => x.Contains("changed.sav", StringComparison.Ordinal));
            changes.DeletedFiles.Should().Contain(x => x.Contains("deleted.sav", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task CopyLocalToRepository_RejectsUnsafeRemotePath()
    {
        var root = CreateRoot();
        try
        {
            var service = new SaveService(new PathResolver(), Substitute.For<IBackupService>(), NullLogger<SaveService>.Instance);
            var game = new Game
            {
                Id = "demo_game",
                Title = "Demo",
                SaveLocations =
                [
                    new SaveLocation
                    {
                        Id = "bad",
                        Type = SaveLocationType.File,
                        LocalPath = Path.Combine(root, "x.dat"),
                        RemotePath = "../escape.dat"
                    }
                ]
            };
            File.WriteAllText(Path.Combine(root, "x.dat"), "x");

            var act = async () => await service.CopyLocalToRepositoryAsync(game, Path.Combine(root, "repo"));
            await act.Should().ThrowAsync<GameSync.Core.Errors.PathTraversalException>();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }


    [Fact]
    public async Task MergeNonConflictingSaves_UnionsDistinctSlots()
    {
        var root = CreateRoot();
        try
        {
            var localDir = Path.Combine(root, "local", "saves");
            Directory.CreateDirectory(localDir);
            File.WriteAllText(Path.Combine(localDir, "slotA.sav"), "run-a");

            var repo = Path.Combine(root, "repo");
            var remoteDir = Path.Combine(repo, "saves", "demo_game", "main");
            Directory.CreateDirectory(remoteDir);
            File.WriteAllText(Path.Combine(remoteDir, "slotB.sav"), "run-b");

            var service = new SaveService(new PathResolver(), Substitute.For<IBackupService>(), NullLogger<SaveService>.Instance);
            var game = CreateDirectoryOnlyGame(localDir);

            await service.MergeNonConflictingSavesAsync(game, repo);

            File.ReadAllText(Path.Combine(localDir, "slotA.sav")).Should().Be("run-a");
            File.ReadAllText(Path.Combine(localDir, "slotB.sav")).Should().Be("run-b");
            File.ReadAllText(Path.Combine(remoteDir, "slotA.sav")).Should().Be("run-a");
            File.ReadAllText(Path.Combine(remoteDir, "slotB.sav")).Should().Be("run-b");

            var changes = await service.DetectChangesAsync(game, repo);
            changes.HasChanges.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MergeNonConflictingSaves_DoesNotOverwriteSamePathConflict()
    {
        var root = CreateRoot();
        try
        {
            var localDir = Path.Combine(root, "local", "saves");
            Directory.CreateDirectory(localDir);
            File.WriteAllText(Path.Combine(localDir, "slot.sav"), "local-v2");
            File.WriteAllText(Path.Combine(localDir, "onlyLocal.sav"), "L");

            var repo = Path.Combine(root, "repo");
            var remoteDir = Path.Combine(repo, "saves", "demo_game", "main");
            Directory.CreateDirectory(remoteDir);
            File.WriteAllText(Path.Combine(remoteDir, "slot.sav"), "remote-v1");
            File.WriteAllText(Path.Combine(remoteDir, "onlyRemote.sav"), "R");

            var service = new SaveService(new PathResolver(), Substitute.For<IBackupService>(), NullLogger<SaveService>.Instance);
            var game = CreateDirectoryOnlyGame(localDir);

            await service.MergeNonConflictingSavesAsync(game, repo);

            File.ReadAllText(Path.Combine(localDir, "slot.sav")).Should().Be("local-v2");
            File.ReadAllText(Path.Combine(remoteDir, "slot.sav")).Should().Be("remote-v1");
            File.Exists(Path.Combine(localDir, "onlyRemote.sav")).Should().BeTrue();
            File.Exists(Path.Combine(remoteDir, "onlyLocal.sav")).Should().BeTrue();

            var changes = await service.DetectChangesAsync(game, repo);
            changes.ChangedFiles.Should().Contain(x => x.Contains("slot.sav", StringComparison.Ordinal));
            changes.HasContentConflicts.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ResolveContentConflicts_KeepLocal_PreservesUniqueRemoteSlot()
    {
        var root = CreateRoot();
        try
        {
            var localDir = Path.Combine(root, "local", "saves");
            Directory.CreateDirectory(localDir);
            File.WriteAllText(Path.Combine(localDir, "slot.sav"), "local-v2");
            File.WriteAllText(Path.Combine(localDir, "onlyLocal.sav"), "L");

            var repo = Path.Combine(root, "repo");
            var remoteDir = Path.Combine(repo, "saves", "demo_game", "main");
            Directory.CreateDirectory(remoteDir);
            File.WriteAllText(Path.Combine(remoteDir, "slot.sav"), "remote-v1");
            File.WriteAllText(Path.Combine(remoteDir, "onlyRemote.sav"), "R");

            var service = new SaveService(new PathResolver(), Substitute.For<IBackupService>(), NullLogger<SaveService>.Instance);
            var game = CreateDirectoryOnlyGame(localDir);

            await service.ResolveContentConflictsAsync(game, repo, ConflictResolution.Local);

            File.ReadAllText(Path.Combine(localDir, "slot.sav")).Should().Be("local-v2");
            File.ReadAllText(Path.Combine(remoteDir, "slot.sav")).Should().Be("local-v2");
            File.ReadAllText(Path.Combine(localDir, "onlyRemote.sav")).Should().Be("R");
            File.ReadAllText(Path.Combine(remoteDir, "onlyLocal.sav")).Should().Be("L");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DetectChanges_OneSideEmptyDirectory_ReportsLocalOnlyAsAdded()
    {
        var root = CreateRoot();
        try
        {
            var localDir = Path.Combine(root, "local", "saves");
            Directory.CreateDirectory(localDir);
            File.WriteAllText(Path.Combine(localDir, "slotA.sav"), "a");

            var repo = Path.Combine(root, "repo");
            Directory.CreateDirectory(Path.Combine(repo, "saves", "demo_game", "main"));

            var service = new SaveService(new PathResolver(), Substitute.For<IBackupService>(), NullLogger<SaveService>.Instance);
            var game = CreateDirectoryOnlyGame(localDir);

            var changes = await service.DetectChangesAsync(game, repo);
            changes.AddedFiles.Should().Contain(x => x.Contains("slotA.sav", StringComparison.Ordinal));
            changes.ChangedFiles.Should().BeEmpty();
            service.HasRepositorySaveContent(game, repo).Should().BeFalse();
            service.HasLocalSaveContent(game).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static Game CreateDirectoryOnlyGame(string localDir) => new()
    {
        Id = "demo_game",
        Title = "Demo Game",
        SaveLocations =
        [
            new SaveLocation
            {
                Id = "main",
                Type = SaveLocationType.Directory,
                LocalPath = localDir,
                RemotePath = "saves/demo_game/main"
            }
        ]
    };

    private static Game CreateGame(string localDir, string localFile) => new()
    {
        Id = "demo_game",
        Title = "Demo Game",
        SaveLocations =
        [
            new SaveLocation
            {
                Id = "main",
                Type = SaveLocationType.Directory,
                LocalPath = localDir,
                RemotePath = "saves/demo_game/main"
            },
            new SaveLocation
            {
                Id = "settings",
                Type = SaveLocationType.File,
                LocalPath = localFile,
                RemotePath = "saves/demo_game/settings.dat"
            }
        ]
    };

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "GameSyncSaveTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
