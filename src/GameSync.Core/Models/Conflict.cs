namespace GameSync.Core.Models;

/// <summary>
/// Represents a Git or save-divergence conflict. Binary saves are not mergeable.
/// Side metadata (timestamps / sizes) powers the Steam-style conflict dialog.
/// </summary>
public sealed class Conflict
{
    public required string Path { get; init; }

    public ConflictType Type { get; init; } = ConflictType.Content;

    public string? GameId { get; init; }

    public string? SaveLocationId { get; init; }

    /// <summary>Absolute local save path when known.</summary>
    public string? LocalPath { get; init; }

    /// <summary>Repository-relative remote path when known.</summary>
    public string? RemotePath { get; init; }

    public string? Message { get; init; }

    public bool IsBinary { get; init; } = true;

    /// <summary>Last write time of the local side (UTC), when the file/folder exists.</summary>
    public DateTimeOffset? LocalLastWriteUtc { get; init; }

    /// <summary>Last write time of the remote/repository side (UTC).</summary>
    public DateTimeOffset? RemoteLastWriteUtc { get; init; }

    /// <summary>Local file size in bytes (sum for folders), when known.</summary>
    public long? LocalSizeBytes { get; init; }

    /// <summary>Remote file size in bytes (sum for folders), when known.</summary>
    public long? RemoteSizeBytes { get; init; }

    /// <summary>Friendly label for the local side (e.g. this PC's machine id).</summary>
    public string? LocalMachine { get; init; }

    /// <summary>Friendly label for the remote side (e.g. GitHub / other machine).</summary>
    public string? RemoteMachine { get; init; }

    /// <summary>True when the local path exists on disk.</summary>
    public bool LocalExists { get; init; }

    /// <summary>True when the remote/repository path exists.</summary>
    public bool RemoteExists { get; init; }
}
