using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HeadStartVR.Views;

public partial class Dialog : Window
{
    public string? Result { get; private set; }

    private Dialog(string title, string message, (string label, string key, bool primary)[] buttons)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        foreach (var (label, key, primary) in buttons)
        {
            var b = new Button
            {
                Content = label,
                Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)FindResource(primary ? "PrimaryButton" : "GhostButton"),
                IsDefault = primary,
            };
            var k = key;
            b.Click += (_, _) =>
            {
                Result = k;
                DialogResult = true;
            };
            ButtonsHost.Items.Add(b);
        }
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    private void Drag(object sender, MouseButtonEventArgs e) => DragMove();

    /// <summary>Show a themed message box; returns the key of the clicked button, or null if closed.</summary>
    public static string? Show(Window? owner, string title, string message, params (string label, string key, bool primary)[] buttons)
    {
        var d = new Dialog(title, message, buttons.Length == 0 ? new[] { ("OK", "ok", true) } : buttons);
        if (owner != null && owner.IsVisible) d.Owner = owner;
        else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        d.ShowDialog();
        return d.Result;
    }
}
