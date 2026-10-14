using System.Windows;
using System.Windows.Controls;

namespace Grimmcair.Desktop.Views;

public sealed record DesktopCloudLinkChoice(string Id, string Label);

public partial class DesktopCloudLinkWindow : Window
{
    public string? SelectedSaveId { get; private set; }
    public DesktopCloudLinkWindow(string localHeading, IReadOnlyList<DesktopCloudLinkChoice> choices)
    {
        InitializeComponent();
        LocalHeading.Text = localHeading;
        CloudChoices.ItemsSource = choices;
        CloudChoices.SelectedIndex = choices.Count > 0 ? 0 : -1;
    }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e) => LinkButton.IsEnabled = CloudChoices.SelectedItem is DesktopCloudLinkChoice;
    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (CloudChoices.SelectedItem is not DesktopCloudLinkChoice choice) return;
        SelectedSaveId = choice.Id; DialogResult = true;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
