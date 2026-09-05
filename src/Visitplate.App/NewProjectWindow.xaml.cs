using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Visitplate.App;

public partial class NewProjectWindow : Window
{
    public string? ProjectDirectory { get; private set; }

    public NewProjectWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void ChooseParentClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Где создать новую папку выезда" };
        if (dialog.ShowDialog(this) == true)
            ParentBox.Text = dialog.FolderName;
    }

    private void CreateClick(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text;
        if (string.IsNullOrWhiteSpace(ParentBox.Text) || string.IsNullOrWhiteSpace(name)
            || name != name.Trim() || name is "." or ".." || name.EndsWith('.')
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ErrorText.Text = "Выберите родительскую папку и введите простое имя без разделителей пути.";
            return;
        }
        string path = Path.Combine(ParentBox.Text, name);
        ProjectDirectory = path;
        DialogResult = true;
    }
}
