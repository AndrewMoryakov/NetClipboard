using System.Windows;
using System.Windows.Input;

namespace NetClipboard;

public partial class PasswordDialog : Window
{
    public string Password => ShowPassword.IsChecked == true ? PasswordPlain.Text : PasswordInput.Password;

    public string Prompt
    {
        get => PromptText.Text;
        set => PromptText.Text = value;
    }

    public PasswordDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            PasswordInput.Focus();
            UpdateCapsWarning();
        };
    }

    private void ShowPassword_Checked(object sender, RoutedEventArgs e)
    {
        PasswordPlain.Text = PasswordInput.Password;
        PasswordInput.Visibility = Visibility.Collapsed;
        PasswordPlain.Visibility = Visibility.Visible;
        PasswordPlain.Focus();
        PasswordPlain.CaretIndex = PasswordPlain.Text.Length;
    }

    private void ShowPassword_Unchecked(object sender, RoutedEventArgs e)
    {
        PasswordInput.Password = PasswordPlain.Text;
        PasswordPlain.Visibility = Visibility.Collapsed;
        PasswordInput.Visibility = Visibility.Visible;
        PasswordInput.Focus();
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(Password))
            return;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void PasswordInput_KeyDown(object sender, KeyEventArgs e)
    {
        UpdateCapsWarning();
        if (e.Key == Key.Enter && !string.IsNullOrEmpty(Password))
            DialogResult = true;
    }

    private void UpdateCapsWarning()
    {
        CapsWarning.Visibility = Keyboard.IsKeyToggled(Key.CapsLock)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}
