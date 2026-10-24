using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameSync.Core.Models;
using GameSync.Core.Services;

namespace GameSync.App.ViewModels;

public sealed partial class ConflictDialogViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string GameTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Headline { get; set; } = "Cloud conflict";

    [ObservableProperty]
    public partial string Explanation { get; set; } =
        "Local and cloud saves differ. Pick which copy to keep — like Steam Cloud.";

    [ObservableProperty]
    public partial string SaveLocation { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FileSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasMultipleFiles { get; set; }

    [ObservableProperty]
    public partial string LocalVersion { get; set; } = "This PC";

    [ObservableProperty]
    public partial string RemoteVersion { get; set; } = "Cloud (GitHub)";

    [ObservableProperty]
    public partial string? LocalTimestamp { get; set; }

    [ObservableProperty]
    public partial string? RemoteTimestamp { get; set; }

    [ObservableProperty]
    public partial string? LocalSizeText { get; set; }

    [ObservableProperty]
    public partial string? RemoteSizeText { get; set; }

    [ObservableProperty]
    public partial string? LocalMachine { get; set; }

    [ObservableProperty]
    public partial string? RemoteMachine { get; set; }

    [ObservableProperty]
    public partial string? LocalBadge { get; set; }

    [ObservableProperty]
    public partial string? RemoteBadge { get; set; }

    [ObservableProperty]
    public partial bool LocalIsNewer { get; set; }

    [ObservableProperty]
    public partial bool RemoteIsNewer { get; set; }

    [ObservableProperty]
    public partial bool DetailsExpanded { get; set; }

    [ObservableProperty]
    public partial string TechnicalDetails { get; set; } = string.Empty;

    public ObservableCollection<ConflictFileRow> Files { get; } = [];

    public ConflictResolutionChoice Result { get; private set; } = ConflictResolutionChoice.Cancel;

    public event EventHandler? CloseRequested;

    public void LoadFromConflict(Conflict conflict, string? gameTitle = null, int totalConflicts = 1) =>
        LoadFromConflicts([conflict], gameTitle, totalConflicts);

    public void LoadFromConflicts(IReadOnlyList<Conflict> conflicts, string? gameTitle = null, int? totalConflicts = null)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        if (conflicts.Count == 0)
        {
            throw new ArgumentException("At least one conflict is required.", nameof(conflicts));
        }

        var count = totalConflicts ?? conflicts.Count;
        var primary = conflicts[0];
        GameTitle = gameTitle ?? primary.GameId ?? "Game";
        Headline = primary.Type == ConflictType.SaveDivergence
            ? "Cloud save conflict"
            : "Sync conflict";
        Explanation = count > 1
            ? $"GameSync found {count} files that differ between this PC and the cloud. Choose which side to keep for all of them."
            : "GameSync found different save data on this PC and in the cloud. Choose which copy to keep.";

        SaveLocation = primary.LocalPath ?? primary.RemotePath ?? primary.Path;
        FileSummary = count > 1
            ? $"{count} files differ · your choice applies to all"
            : Path.GetFileName(primary.LocalPath ?? primary.Path) is { Length: > 0 } name
                ? name
                : primary.Path;

        var localTimes = conflicts
            .Where(c => c.LocalLastWriteUtc is not null)
            .Select(c => c.LocalLastWriteUtc!.Value)
            .ToList();
        var remoteTimes = conflicts
            .Where(c => c.RemoteLastWriteUtc is not null)
            .Select(c => c.RemoteLastWriteUtc!.Value)
            .ToList();
        DateTimeOffset? newestLocal = localTimes.Count > 0 ? localTimes.Max() : null;
        DateTimeOffset? newestRemote = remoteTimes.Count > 0 ? remoteTimes.Max() : null;
        var localSize = conflicts.Sum(c => c.LocalSizeBytes ?? 0);
        var remoteSize = conflicts.Sum(c => c.RemoteSizeBytes ?? 0);
        var anyLocal = conflicts.Any(c => c.LocalExists || c.LocalLastWriteUtc is not null);
        var anyRemote = conflicts.Any(c => c.RemoteExists || c.RemoteLastWriteUtc is not null);
        HasMultipleFiles = count > 1;

        LocalMachine = primary.LocalMachine ?? "This PC";
        RemoteMachine = primary.RemoteMachine ?? "GitHub";
        LocalVersion = $"This PC ({LocalMachine})";
        RemoteVersion = $"Cloud ({RemoteMachine})";
        LocalTimestamp = anyLocal
            ? ConflictSideInfo.FormatTimestamp(newestLocal)
            : "No local copy";
        RemoteTimestamp = anyRemote
            ? ConflictSideInfo.FormatTimestamp(newestRemote)
            : "No cloud copy";
        LocalSizeText = anyLocal ? ConflictSideInfo.FormatSize(localSize) : "—";
        RemoteSizeText = anyRemote ? ConflictSideInfo.FormatSize(remoteSize) : "—";

        LocalIsNewer = newestLocal is not null
                       && (newestRemote is null || newestLocal > newestRemote);
        RemoteIsNewer = newestRemote is not null
                        && (newestLocal is null || newestRemote > newestLocal);
        LocalBadge = LocalIsNewer ? "Newer" : null;
        RemoteBadge = RemoteIsNewer ? "Newer" : null;

        Files.Clear();
        foreach (var conflict in conflicts.Take(12))
        {
            Files.Add(new ConflictFileRow(
                Path.GetFileName(conflict.LocalPath ?? conflict.Path) is { Length: > 0 } n ? n : conflict.Path,
                ConflictSideInfo.DescribeExistence(conflict.LocalExists, conflict.LocalLastWriteUtc, conflict.LocalSizeBytes),
                ConflictSideInfo.DescribeExistence(conflict.RemoteExists, conflict.RemoteLastWriteUtc, conflict.RemoteSizeBytes)));
        }

        if (count > 12)
        {
            Files.Add(new ConflictFileRow($"…and {count - 12} more", string.Empty, string.Empty));
        }

        var detailLines = conflicts.Take(20).Select(c =>
            $"{c.Path}\n  local:  {ConflictSideInfo.DescribeExistence(c.LocalExists, c.LocalLastWriteUtc, c.LocalSizeBytes)}\n  remote: {ConflictSideInfo.DescribeExistence(c.RemoteExists, c.RemoteLastWriteUtc, c.RemoteSizeBytes)}\n  type: {c.Type}");
        TechnicalDetails = string.Join("\n\n", detailLines);
        if (count > 20)
        {
            TechnicalDetails += $"\n\n…and {count - 20} more files.";
        }

        Result = ConflictResolutionChoice.Cancel;
    }

    [RelayCommand]
    private void UseLocal()
    {
        Result = ConflictResolutionChoice.UseLocal;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void UseRemote()
    {
        Result = ConflictResolutionChoice.UseRemote;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ViewHistory()
    {
        Result = ConflictResolutionChoice.ViewHistory;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = ConflictResolutionChoice.Cancel;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}

public sealed record ConflictFileRow(string Name, string LocalInfo, string RemoteInfo);
