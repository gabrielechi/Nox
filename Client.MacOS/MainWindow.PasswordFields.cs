using Avalonia.Interactivity;

namespace Client.MacOS;

/// <summary>
/// Password fields of the sign-in screen: "Repeat password" (registration only) and "Show password",
/// which toggles RevealPassword on both fields through a binding in MainWindow.axaml.
/// </summary>
public partial class MainWindow
{
    private void ConfirmPassword_Changed(object? sender, RoutedEventArgs e) => UpdateRegistrationHint();

    /// <summary>Empties both password fields and hides the passwords again.</summary>
    private void ClearPasswordFields()
    {
        PasswordBox.Text = string.Empty;
        ConfirmPasswordBox.Text = string.Empty;
        ShowPasswordCheckBox.IsChecked = false;
    }
}
