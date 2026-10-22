using System.Windows;
using Grimmcair.Desktop.Services;

namespace Grimmcair.Desktop;

public partial class MainWindow
{
    private GuideCatalog _guide = new();
    private Dictionary<string, string> _badgeBonuses = [];

    private void InitializeGuides(GuideCatalog? guide = null, ConceptCatalog? concepts = null)
    {
        _guide = guide ?? GuideCatalog.Load();
        _badgeBonuses = _guide.BadgeBonuses.ToDictionary(b => b.ItemId, b => b.Text, StringComparer.Ordinal);
        HeroGuide.Load(_guide);
        BossGuide.Load(_guide);
        ConceptGuide.Load(concepts ?? ConceptCatalog.Load());
        BossGuide.ItemRequested += OpenRelatedItem;
        BossGuide.DropRulesRequested += () =>
        {
            MainPages.SelectedIndex = 5;
            ConceptGuide.SelectSection("drop");
        };
    }

    private void OpenBossDrops(string bossId, string itemId)
    {
        MainPages.SelectedIndex = 3;
        BossGuide.ShowDrops(bossId, itemId);
    }

    private void About_Click(object sender, RoutedEventArgs e) => new Views.AboutWindow { Owner = this }.ShowDialog();
}
