using System.Windows;

namespace Marco.App.Views;

/// <summary>A minimal single-line text prompt (WPF has no built-in InputBox).</summary>
public partial class TextPromptDialog : Window
{
    public string Value => InputBox.Text;

    public TextPromptDialog(string title, string prompt, string initial = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = initial;
        Loaded += (_, _) => { InputBox.SelectAll(); InputBox.Focus(); };
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
