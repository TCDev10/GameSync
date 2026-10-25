using GameSync.App.ViewModels;
using GameSync.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace GameSync.App.Dialogs;

public sealed partial class ConflictDialog : ContentDialog
{
    public ConflictDialogViewModel ViewModel { get; }

    public ConflictDialog()
    {
        ViewModel = App.Services.GetRequiredService<ConflictDialogViewModel>();
        InitializeComponent();
        ViewModel.CloseRequested += (_, _) => Hide();
    }

    public void Load(Conflict conflict, string? gameTitle = null, int totalConflicts = 1) =>
        ViewModel.LoadFromConflict(conflict, gameTitle, totalConflicts);

    public void Load(IReadOnlyList<Conflict> conflicts, string? gameTitle = null) =>
        ViewModel.LoadFromConflicts(conflicts, gameTitle);

    public ConflictResolutionChoice Choice => ViewModel.Result;
}
