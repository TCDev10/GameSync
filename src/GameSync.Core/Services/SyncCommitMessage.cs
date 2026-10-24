using GameSync.Core.Models;

namespace GameSync.Core.Services;

public static class SyncCommitMessage
{
    public static string ForGameUpdate(string gameTitle, string? machineId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameTitle);
        var title = gameTitle.Trim();
        if (string.IsNullOrWhiteSpace(machineId))
        {
            return $"GameSync: Update {title} saves";
        }

        return $"GameSync: Update {title} saves from {machineId.Trim()}";
    }

    public static string ForLibraryConfiguration(string? machineId = null)
    {
        if (string.IsNullOrWhiteSpace(machineId))
        {
            return "GameSync: Update game library";
        }

        return $"GameSync: Update game library from {machineId.Trim()}";
    }

    public static string ForGameRemoved(string gameTitle, string? machineId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameTitle);
        var title = gameTitle.Trim();
        if (string.IsNullOrWhiteSpace(machineId))
        {
            return $"GameSync: Remove {title} from library";
        }

        return $"GameSync: Remove {title} from library on {machineId.Trim()}";
    }

    public static string ForRepositoryInitialize() => "GameSync: Initialize repository structure";

    public static string ForConflictResolution(string? machineId = null)
    {
        if (string.IsNullOrWhiteSpace(machineId))
        {
            return "GameSync: Resolve save conflicts";
        }

        return $"GameSync: Resolve save conflicts on {machineId.Trim()}";
    }
}

