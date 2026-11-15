using System.Windows;
using System.Windows.Controls;

namespace Client.Windows;

/// <summary>
/// Password fields of the sign-in screen: "Repeat password" (registration only) and "Show password".
/// A PasswordBox cannot show its text, so each one has a TextBox on top that is shown instead while
/// "Show password" is checked; the two are kept in sync in both directions. The rest of the code keeps
/// reading PasswordBox.Password / ConfirmPasswordBox.Password.
/// </summary>
public partial class MainWindow
{
    private bool _syncingPasswordFields;

    private void InitializePasswordFields()
    {
        Pair(PasswordBox, PasswordRevealBox);
        Pair(ConfirmPasswordBox, ConfirmPasswordRevealBox);
    }

    private void Pair(PasswordBox hidden, TextBox shown)
    {
        hidden.PasswordChanged += (_, _) => Sync(() => { if (shown.Text != hidden.Password) shown.Text = hidden.Password; });
        shown.TextChanged += (_, _) => Sync(() => { if (hidden.Password != shown.Text) hidden.Password = shown.Text; });
    }

    private void Sync(Action copy)
    {
        if (_syncingPasswordFields) return;
        _syncingPasswordFields = true;
        try { copy(); }
        finally { _syncingPasswordFields = false; }
    }

    private void ShowPassword_Changed(object sender, RoutedEventArgs e)
    {
        bool reveal = ShowPasswordCheckBox.IsChecked == true;
        Swap(PasswordBox, PasswordRevealBox, reveal);
        Swap(ConfirmPasswordBox, ConfirmPasswordRevealBox, reveal);
    }

    /// <summary>Shows one of the two controls of a field and moves the keyboard focus with it.</summary>
    private static void Swap(PasswordBox hidden, TextBox shown, bool reveal)
    {
        bool hadFocus = hidden.IsKeyboardFocusWithin || shown.IsKeyboardFocusWithin;
        hidden.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
        shown.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
        if (!hadFocus) return;
        if (reveal)
        {
            shown.Focus();
            shown.CaretIndex = shown.Text.Length;
        }
        else hidden.Focus();
    }

    private void ConfirmPassword_Changed(object sender, RoutedEventArgs e) => UpdateRegistrationHint();

    /// <summary>Empties both password fields and hides the passwords again.</summary>
    private void ClearPasswordFields()
    {
        PasswordBox.Clear();
        ConfirmPasswordBox.Clear();
        ShowPasswordCheckBox.IsChecked = false;
    }

    /// <summary>The field the user types the password in (the visible one of the pair).</summary>
    private void FocusPasswordField()
    {
        if (ShowPasswordCheckBox.IsChecked == true) PasswordRevealBox.Focus();
        else PasswordBox.Focus();
    }
}
