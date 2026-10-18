using System.Windows;
using System.Windows.Controls;

namespace XStoreManager.App;

internal sealed class DlcDialog : Window
{
    readonly TextBox id = new();
    readonly TextBox name = new();
    readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.OrangeRed, Margin = new Thickness(0, 10, 0, 0) };
    public string StoreId => id.Text.Trim();
    public string DlcName => name.Text.Trim();
    public DlcDialog()
    {
        Title = "Add downloadable content"; Width = 450; Height = 340; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 33, 42));
        var panel = new StackPanel { Margin = new Thickness(25) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "Add downloadable content", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 18) });
        panel.Children.Add(new TextBlock { Text = "Microsoft Store ID", Margin = new Thickness(0, 0, 0, 5) }); panel.Children.Add(id);
        panel.Children.Add(new TextBlock { Text = "DLC name", Margin = new Thickness(0, 12, 0, 5) }); panel.Children.Add(name); panel.Children.Add(error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 15, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true }; var add = new Button { Content = "Add DLC", IsDefault = true };
        add.Click += (_, _) => { if (!XStoreManager.Core.IniService.IsStoreId(StoreId) || DlcName.Length == 0) { error.Text = "Enter a valid 12-character Store ID and a DLC name."; return; } DialogResult = true; };
        actions.Children.Add(cancel); actions.Children.Add(add); panel.Children.Add(actions);
        Loaded += (_, _) => id.Focus();
    }
}
