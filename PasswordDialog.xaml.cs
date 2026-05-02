using System.Windows;
using System.Windows.Input;

namespace NetClipboard;

public partial class PasswordDialog : Window
{
    public string Password => PasswordInput.Password;

    public string Prompt
    {
        get => PromptText.Text;
        set => PromptText.Text = value;
    }

    public PasswordDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => PasswordInput.Focus();
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(PasswordInput.Password))
            return;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void PasswordInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !string.IsNullOrEmpty(PasswordInput.Password))
            DialogResult = true;
    }
}
