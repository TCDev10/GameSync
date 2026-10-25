using GameSync.App.Dialogs;
using GameSync.App.Navigation;
using GameSync.App.Services;
using GameSync.App.ViewModels;
using GameSync.Core.Abstractions.Configuration;
using GameSync.Core.Abstractions.Sync;
using GameSync.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;

namespace GameSync.App.Views;

public sealed partial class GameDetailsPage : Page
{
    public GameDetailsViewModel ViewModel { get; }

    public GameDetailsPage()
    {
        ViewModel = App.Services.GetRequiredService<GameDetailsViewModel>();
        InitializeComponent();
        ViewModel.ConflictDetected += OnConflictDetected;
        Unloaded += (_, _) => ViewModel.ConflictDetected -= OnConflictDetected;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string gameId)
        {
            await ViewModel.LoadAsync(gameId);
        }
    }

    private async void OnConflictDetected(object? sender, SyncResult result)
    {
        var conflict = result.Conflicts[0];
        var dialog = new ConflictDialog();
        dialog.XamlRoot = XamlRoot;
        dialog.Load(result.Conflicts, ViewModel.Title);
        await dialog.ShowAsync();

        if (dialog.Choice is ConflictResolutionChoice.ViewHistory)
        {
            App.Services.GetRequiredService<INavigationService>().NavigateTo(typeof(HistoryPage));
            return;
        }

        if (dialog.Choice is ConflictResolutionChoice.Cancel)
        {
            return;
        }

        try
        {
            var resolver = App.Services.GetRequiredService<IConflictResolver>();
            var resolution = resolver.ToResolution(dialog.Choice);
            SyncResult applyResult;
            if (conflict.Type == ConflictType.SaveDivergence && !string.IsNullOrWhiteSpace(conflict.GameId))
            {
                applyResult = await ViewModel.ResolveSaveDivergenceAsync(resolution);
            }
            else
            {
                var machine = await App.Services.GetRequiredService<IMachineConfigurationStore>().LoadAsync();
                var repoPath = machine.Repository?.LocalPath;
                if (string.IsNullOrWhiteSpace(repoPath))
                {
                    return;
                }

                await resolver.ApplyAllAsync(repoPath, result.Conflicts, resolution);
                applyResult = SyncResult.Success(SyncStatus.UpToDate, "Conflicts resolved.");
            }

            if (!applyResult.Succeeded)
            {
                throw applyResult.Error ?? new InvalidOperationException(applyResult.Message ?? "Could not resolve conflict.");
            }

            if (conflict.Type == ConflictType.SaveDivergence)
            {
                ViewModel.NotifyDivergenceResolved(applyResult);
            }
            else
            {
                await ViewModel.SyncCommand.ExecuteAsync(null);
            }
        }
        catch (Exception ex)
        {
            var error = new ContentDialog
            {
                Title = "Could not apply resolution",
                Content = ex.Message,
                CloseButtonText = "OK",
                XamlRoot = XamlRoot
            };
            await error.ShowAsync();
        }
    }

    private void EditableTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(CoreVirtualKeyStates.Down);

        if (ctrl && e.Key == VirtualKey.V && ClipboardText.TryGetText(out var pasted) && pasted is not null)
        {
            box.Text = pasted.Trim();
            box.SelectionStart = box.Text.Length;
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == VirtualKey.C)
        {
            var copy = !string.IsNullOrEmpty(box.SelectedText) ? box.SelectedText : box.Text;
            if (!string.IsNullOrEmpty(copy))
            {
                ClipboardText.SetText(copy);
                e.Handled = true;
            }
        }
    }

    private async void BrowseExecutable_Click(object sender, RoutedEventArgs e)
    {
        var file = await PickExecutableAsync();
        if (file is not null)
        {
            ViewModel.ApplyLaunchPath(file.Path);
        }
    }

    private async void BrowseMonitor_Click(object sender, RoutedEventArgs e)
    {
        var file = await PickExecutableAsync();
        if (file is not null)
        {
            ViewModel.ApplyMonitorPath(file.Path);
        }
    }

    private static async Task<StorageFile?> PickExecutableAsync()
    {
        var picker = new FileOpenPicker();
        PickerInterop.Attach(picker);
        picker.FileTypeFilter.Add(".exe");
        picker.FileTypeFilter.Add(".lnk");
        return await picker.PickSingleFileAsync();
    }

    private void LaunchPath_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void ExecutableTextBox_Drop(object sender, DragEventArgs e)
    {
        var path = await TryGetDroppedFilePathAsync(e);
        if (path is not null)
        {
            ViewModel.ApplyLaunchPath(path);
        }
    }

    private async void MonitorTextBox_Drop(object sender, DragEventArgs e)
    {
        var path = await TryGetDroppedFilePathAsync(e);
        if (path is not null)
        {
            ViewModel.ApplyMonitorPath(path);
        }
    }

    private static async Task<string?> TryGetDroppedFilePathAsync(DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return null;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        return items.Count > 0 && items[0] is StorageFile file ? file.Path : null;
    }

    private async void BrowseSaveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SaveLocationEditItem item })
        {
            return;
        }

        var picker = new FolderPicker();
        PickerInterop.Attach(picker);
        picker.FileTypeFilter.Add("*");
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            var paths = App.Services.GetRequiredService<GameSync.Core.Abstractions.IPathResolver>();
            item.LocalPath = paths.ToPortableTemplate(folder.Path);
            item.IsUserEdited = true;
        }
    }

    private void RemoveSaveLocation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SaveLocationEditItem item })
        {
            ViewModel.RemoveSaveLocationCommand.Execute(item);
        }
    }

    private async void RemoveGame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Remove game?",
            Content = $"Remove \"{ViewModel.Title}\" from your library on this PC and publish the change to GitHub? Save files already synced stay in the repository.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await ViewModel.RemoveGameAsync();
    }
}
