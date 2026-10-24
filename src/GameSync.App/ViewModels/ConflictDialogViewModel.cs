using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameSync.Core.Models;

namespace GameSync.App.ViewModels;

public sealed partial class ConflictDialogViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string GameTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SaveLocation { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LocalVersion { get; set; } = "Local changes on this PC";

    [ObservableProperty]
    public partial string RemoteVersion { get; set; } = "Changes from the repository";

    [ObservableProperty]
    public partial string? LocalTimestamp { get; set; }

    [ObservableProperty]
    public partial string? RemoteTimestamp { get; set; }

    [ObservableProperty]
    public partial string? LocalMachine { get; set; }

    [ObservableProperty]
    public partial string? RemoteMachine { get; set; }

    [ObservableProperty]
    public partial bool DetailsExpanded { get; set; }

    [ObservableProperty]
    public partial string TechnicalDetails { get; set; } = string.Empty;

    public ConflictResolutionChoice Result { get; private set; } = ConflictResolutionChoice.Cancel;

    public event EventHandler? CloseRequested;

    public void LoadFromConflict(Conflict conflict, string? gameTitle = null, int totalConflicts = 1)
    {
        GameTitle = gameTitle ?? conflict.GameId ?? "Game";
        SaveLocation = conflict.LocalPath ?? conflict.RemotePath ?? conflict.Path;
        var multi = totalConflicts > 1
            ? $"\nThis choice will be applied to all {totalConflicts} conflicted files."
            : string.Empty;
        TechnicalDetails =
            $"Path: {conflict.Path}\nType: {conflict.Type}\nBinary: {conflict.IsBinary}\n{conflict.Message}{multi}";
        LocalVersion = totalConflicts > 1
            ? "Keep the version on this PC (all conflicted files)"
            : "Keep the version on this PC";
        RemoteVersion = totalConflicts > 1
            ? "Keep the version from GitHub (all conflicted files)"
            : "Keep the version from GitHub";
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
