using GameSync.App.Services;
using GameSync.App.ViewModels;
using GameSync.Core.Abstractions.Configuration;
using GameSync.Core.Abstractions.Sync;
using GameSync.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GameSync.App;

public sealed partial class LauncherWindow : Window
{
    public LauncherViewModel ViewModel { get; }
    private readonly TaskbarProgressService _taskbarProgress;

    public LauncherWindow(string gameId)
    {
        ViewModel = App.Services.GetRequiredService<LauncherViewModel>();
        _taskbarProgress = App.Services.GetRequiredService<TaskbarProgressService>();
        ViewModel.GameId = gameId;
        ViewModel.GameTitle = gameId;

        InitializeComponent();
        _taskbarProgress.Register(this);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(480, 360));
        ViewModel.CloseRequested += OnCloseRequested;
        ViewModel.ConflictDetected += OnConflictDetected;
        Closed += (_, _) =>
        {
            _taskbarProgress.Unregister(this);
            ViewModel.CloseRequested -= OnCloseRequested;
            ViewModel.ConflictDetected -= OnConflictDetected;
            ViewModel.Cancel();
        };
        Activated += LauncherWindow_Activated;
    }

    private async void LauncherWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= LauncherWindow_Activated;
        if (ViewModel.StartCommand.CanExecute(null))
        {
            await ViewModel.StartCommand.ExecuteAsync(null);
        }
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    private async void OnConflictDetected(object? sender, SyncResult result)
    {
        if (result.Conflicts.Count == 0)
        {
            return;
        }

        var conflict = result.Conflicts[0];
        var dialog = new Dialogs.ConflictDialog
        {
            XamlRoot = Content.XamlRoot
        };
        dialog.Load(result.Conflicts, ViewModel.GameTitle);
        await dialog.ShowAsync();

        if (dialog.Choice is ConflictResolutionChoice.Cancel)
        {
            ViewModel.Cancel();
            Close();
            return;
        }

        if (dialog.Choice is ConflictResolutionChoice.ViewHistory)
        {
            var info = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "History is in main window",
                Content = "Open the main GameSync window and go to History to inspect previous saves.",
                CloseButtonText = "OK"
            };
            await info.ShowAsync();
            return;
        }

        try
        {
            var resolver = App.Services.GetRequiredService<IConflictResolver>();
            var resolution = resolver.ToResolution(dialog.Choice);
            SyncResult applyResult;
            if (conflict.Type == ConflictType.SaveDivergence && !string.IsNullOrWhiteSpace(conflict.GameId))
            {
                var syncWorkflow = App.Services.GetRequiredService<ISyncWorkflow>();
                applyResult = await syncWorkflow.ResolveSaveDivergenceAsync(conflict.GameId, resolution);
            }
            else
            {
                var machine = await App.Services.GetRequiredService<IMachineConfigurationStore>().LoadAsync();
                var repoPath = machine.Repository?.LocalPath;
                if (string.IsNullOrWhiteSpace(repoPath))
                {
                    return;
                }

                // Apply the choice to every conflicted path and finish the merge commit.
                // Resolving only Conflicts[0] left the repo stuck and blocked every launch.
                await resolver.ApplyAllAsync(repoPath, result.Conflicts, resolution);
                applyResult = SyncResult.Success(SyncStatus.UpToDate, "Conflicts resolved.");
            }

            if (!applyResult.Succeeded)
            {
                throw applyResult.Error ?? new InvalidOperationException(applyResult.Message ?? "Could not resolve conflict.");
            }

            await ViewModel.RetryLaunchAfterConflictAsync();
        }
        catch (Exception ex)
        {
            var error = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "Could not apply resolution",
                Content = ex.Message,
                CloseButtonText = "OK"
            };
            await error.ShowAsync();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Cancel();
        Close();
    }
}
