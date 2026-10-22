using System.Windows;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop;

public partial class MainWindow
{
    private MaterialPlanner? _materialPlanner;
    private readonly Dictionary<(string OwnerId, string SaveId), Views.MaterialAnalysisWindow> _materialAnalysisWindows = [];

    private void AnalyzeTargets_Click(object sender, RoutedEventArgs e)
    {
        if (TargetSaveBox.SelectedItem is TargetSaveChoice choice)
            OpenMaterialAnalysis(choice.OwnerId, choice.Id, (EquipmentList.SelectedItem as CatalogItem)?.Id);
    }

    private void AnalyzeSaveTargets_Click(object sender, RoutedEventArgs e)
    {
        if (SaveList.SelectedItem is SavedDocument doc && FindLinkedSave(doc) is { } save)
            OpenMaterialAnalysis(save.OwnerId, save.Id);
    }

    private Views.MaterialAnalysisWindow? PrepareMaterialAnalysis(string ownerId, string saveId, string? targetItemId = null)
    {
        var key = (ownerId, saveId);
        var save = _simulation?.FindSave(ownerId, saveId);
        if (!Views.MaterialAnalysisWindow.CanAnalyze(save))
        {
            CloseMaterialAnalysisWindows(ownerId, saveId); return null;
        }
        if (_materialAnalysisWindows.TryGetValue(key, out var existing))
        {
            existing.RefreshIfChanged();
            if (!string.IsNullOrEmpty(targetItemId)) existing.OpenSynthesis(targetItemId);
            return existing;
        }
        if (save!.Targets.Count == 0) return null;
        _materialPlanner ??= MaterialPlanner.Load();
        // Capture this exact identity; changing the main window's selected save
        // must never replace this window's inventory or equipment targets.
        var window = CreateMaterialAnalysisWindow(_materialPlanner, () => _simulation?.FindSave(ownerId, saveId));
        window.Closed += (_, _) =>
        {
            if (_materialAnalysisWindows.GetValueOrDefault(key) == window) _materialAnalysisWindows.Remove(key);
        };
        _materialAnalysisWindows.Add(key, window);
        window.OpenSynthesis(targetItemId);
        return window;
    }

    private Views.MaterialAnalysisWindow CreateMaterialAnalysisWindow(MaterialPlanner planner, Func<CloudSave?> source)
    {
        // An owned window always stays above its owner. Keep this window
        // independent so selecting an item can reveal the main item details.
        var window = new Views.MaterialAnalysisWindow(planner, _guide, source);
        if (IsVisible)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = Left + Math.Max(0, (ActualWidth - window.Width) / 2);
            window.Top = Top + Math.Max(0, (ActualHeight - window.Height) / 2);
        }
        EventHandler mainClosed = (_, _) => window.Close();
        Closed += mainClosed;
        window.ItemRequested += OpenMaterialItem;
        window.Closed += (_, _) =>
        {
            Closed -= mainClosed;
            window.ItemRequested -= OpenMaterialItem;
        };
        return window;
    }

    private void OpenMaterialItem(string itemId)
    {
        if (_closed || _closing) return;
        OpenRelatedItem(itemId);
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void OpenMaterialAnalysis(string ownerId, string saveId, string? targetItemId = null)
    {
        var window = PrepareMaterialAnalysis(ownerId, saveId, targetItemId);
        if (window is null) return;
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    internal void RefreshMaterialAnalysisWindows(string? ownerId = null, string? saveId = null)
    {
        foreach (var entry in _materialAnalysisWindows.ToArray())
            if ((ownerId is null || entry.Key.OwnerId == ownerId) && (saveId is null || entry.Key.SaveId == saveId))
            {
                var save = _simulation?.FindSave(entry.Key.OwnerId, entry.Key.SaveId);
                if (save is null || save.IsIgnored) entry.Value.Close();
                else entry.Value.RefreshIfChanged();
            }
    }

    internal void CloseMaterialAnalysisWindows(string? ownerId = null, string? saveId = null)
    {
        foreach (var entry in _materialAnalysisWindows.ToArray())
            if ((ownerId is null || entry.Key.OwnerId == ownerId) && (saveId is null || entry.Key.SaveId == saveId))
                entry.Value.Close();
    }
}
