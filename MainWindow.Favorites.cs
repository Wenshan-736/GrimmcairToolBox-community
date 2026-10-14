using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;

namespace Grimmcair.Desktop;

public partial class MainWindow
{
    private void SaveFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (SaveList.SelectedItem is not SavedDocument document) return;
        try
        {
            SetSaveFavorite(document, SaveFavoriteButton.IsChecked == true);
            StatusText.Text = document.IsFavorite ? "已收藏此存档，优先显示在列表顶部。" : "已取消收藏此存档。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText.Text = "收藏未能保存：" + error.Message;
        }
        finally { SaveFavoriteButton.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget(); }
    }
}
