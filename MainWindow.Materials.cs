using System.Windows;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop;

public partial class MainWindow
{
    private MaterialPlanner? _materialPlanner;

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
        var save = _simulation?.FindSave(ownerId, saveId);
        if (!Views.MaterialAnalysisWindow.CanAnalyze(save) || save!.Targets.Count == 0) return null;
        _materialPlanner ??= MaterialPlanner.Load();
        var window = new Views.MaterialAnalysisWindow(_materialPlanner, _guide, () => _simulation!.FindSave(ownerId, saveId)) { Owner = this };
        window.ItemRequested += OpenRelatedItem;
        window.OpenSynthesis(targetItemId);
        return window;
    }

    private void OpenMaterialAnalysis(string ownerId, string saveId, string? targetItemId = null) =>
        PrepareMaterialAnalysis(ownerId, saveId, targetItemId)?.ShowDialog();
}
