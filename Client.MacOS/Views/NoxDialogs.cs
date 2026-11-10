using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Client.Core;

namespace Client.MacOS.Views;

/// <summary>
/// Modal dialogs of the client, with the same texts, choices and checks as the WPF dialogs in
/// Client.Windows/MainWindow.xaml.cs. Avalonia dialogs are asynchronous, so each returns a Task.
/// </summary>
internal static class NoxDialogs
{
    internal enum IdentityChoice { Cancel, RestoreVaultIdentity, CreateNewIdentity }

    private static IBrush Brush(string key) =>
        Application.Current!.TryFindResource(key, out object? value) && value is IBrush brush ? brush : Brushes.Transparent;

    private static FontFamily Mono => (FontFamily)Application.Current!.FindResource("NoxMonoFontFamily")!;

    private static Window CreateWindow(string title, double width) => new()
    {
        Title = title,
        Width = width,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Background = Brush("NoxWindowBrush"),
    };

    private static TextBlock Text(string text, double size = 15, IBrush? foreground = null, FontWeight weight = FontWeight.Normal) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        TextWrapping = TextWrapping.Wrap,
        LineHeight = size + 6,
        Foreground = foreground ?? Brush("NoxTextBrush"),
    };

    private static StackPanel ButtonRow(params Button[] buttons)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 16, 0, 0)
        };
        foreach (var button in buttons) row.Children.Add(button);
        return row;
    }

    public static async Task ShowInfoAsync(Window owner, string message, string title)
    {
        var dialog = CreateWindow(title, 400);
        var close = new Button { Content = Texts.T("Close"), IsDefault = true, IsCancel = true };
        close.Click += (_, _) => dialog.Close();
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(Text(message));
        panel.Children.Add(ButtonRow(close));
        dialog.Content = panel;
        dialog.Opened += (_, _) => close.Focus();
        await dialog.ShowDialog(owner);
    }

    public static async Task<bool> ConfirmAsync(Window owner, string message, string title, string action)
    {
        var dialog = CreateWindow(title, 400);
        var cancel = new Button { Content = Texts.T("Cancel"), IsCancel = true };
        var confirm = new Button { Content = action, Classes = { "primary" } };
        cancel.Click += (_, _) => dialog.Close(false);
        confirm.Click += (_, _) => dialog.Close(true);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(Text(message));
        panel.Children.Add(ButtonRow(cancel, confirm));
        dialog.Content = panel;
        dialog.Opened += (_, _) => cancel.Focus();
        return await dialog.ShowDialog<bool>(owner);
    }

    /// <summary>
    /// Trust on first use / changed keys: the fingerprint is shown with its last 4 characters masked,
    /// and the user must type them as received from the contact out of band.
    /// </summary>
    public static async Task<bool> FingerprintTrustAsync(
        Window owner,
        string windowTitle,
        string heading,
        string message,
        string fingerprintLabel,
        Func<string, string> formatRows,
        string formattedFingerprint,
        string verificationText,
        bool isWarning)
    {
        string expectedSuffix = formattedFingerprint.Replace(" ", string.Empty)[^4..];
        string maskedFingerprint = formattedFingerprint[..^4] + "****";

        var dialog = CreateWindow(windowTitle, 400);
        var root = new StackPanel { Margin = new Thickness(20) };

        root.Children.Add(Text(heading, 21, isWarning ? Brushes.IndianRed : Brush("NoxAccentBrush"), FontWeight.SemiBold));
        var warning = Text(message);
        warning.Margin = new Thickness(0, 8, 0, 12);
        root.Children.Add(warning);
        root.Children.Add(Text(fingerprintLabel, 15, Brush("NoxTextMutedBrush"), FontWeight.SemiBold));

        root.Children.Add(new Border
        {
            Margin = new Thickness(0, 6, 0, 12),
            Padding = new Thickness(12, 8),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush("NoxBorderBrush"),
            Background = Brush("NoxSidebarBrush"),
            Child = new SelectableTextBlock
            {
                Text = formatRows(maskedFingerprint),
                FontFamily = Mono,
                FontSize = 17,
                LineHeight = 26,
                Foreground = Brush("NoxTextBrush"),
            }
        });

        root.Children.Add(Text(verificationText));
        var prompt = Text(Texts.T("EnterTheLast4CharactersProvided"), 14);
        prompt.Margin = new Thickness(0, 8, 0, 6);
        root.Children.Add(prompt);

        var suffixInput = new TextBox
        {
            MaxLength = 4,
            Width = 96,
            HorizontalAlignment = HorizontalAlignment.Left,
            FontFamily = Mono,
            FontSize = 20,
        };
        var verificationError = Text(Texts.T("TheCharactersDoNotMatchCheck"), 15, Brushes.IndianRed);
        verificationError.IsVisible = false;
        verificationError.Margin = new Thickness(0, 8, 0, 0);

        var reject = new Button { Content = Texts.T("Cancel"), IsCancel = true };
        var trust = new Button { Content = Texts.T("Check"), IsDefault = true, Classes = { "primary" } };
        reject.Click += (_, _) => dialog.Close(false);
        suffixInput.TextChanged += (_, _) => verificationError.IsVisible = false;
        trust.Click += (_, _) =>
        {
            if (!string.Equals((suffixInput.Text ?? string.Empty).Trim(), expectedSuffix, StringComparison.OrdinalIgnoreCase))
            {
                verificationError.IsVisible = true;
                suffixInput.Focus();
                suffixInput.SelectAll();
                return;
            }
            dialog.Close(true);
        };

        var actions = new DockPanel();
        var buttons = ButtonRow(reject, trust);
        buttons.Margin = new Thickness(0);
        buttons.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(buttons, Dock.Right);
        actions.Children.Add(buttons);
        actions.Children.Add(suffixInput);
        root.Children.Add(actions);
        root.Children.Add(verificationError);

        dialog.Content = root;
        dialog.Opened += (_, _) => suffixInput.Focus();
        return await dialog.ShowDialog<bool>(owner);
    }

    /// <summary>The server's public keys differ from the vault: cancel, restore from the vault or create a new identity.</summary>
    public static async Task<IdentityChoice> OwnIdentityMismatchAsync(Window owner, string vaultFingerprint, string serverFingerprint)
    {
        var dialog = CreateWindow(Texts.T("PossibleMITMAttack"), 480);
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(Text(Texts.T("YourPublicKeysHaveChanged"), 21, Brushes.IndianRed, FontWeight.SemiBold));
        var message = Text(Texts.T("OwnIdentityMismatch"));
        message.Margin = new Thickness(0, 8, 0, 8);
        root.Children.Add(message);
        root.Children.Add(Text(Texts.T("RecoveryExplanation"), 14, Brush("NoxTextMutedBrush")));
        root.Children.Add(new Border
        {
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(12, 10),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush("NoxBorderBrush"),
            Background = Brush("NoxSidebarBrush"),
            Child = new SelectableTextBlock
            {
                Text = Texts.T("RecoveryFingerprints", vaultFingerprint, serverFingerprint),
                FontFamily = Mono,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("NoxTextBrush"),
            }
        });

        var choice = IdentityChoice.Cancel;
        var cancel = new Button { Content = Texts.T("CancelSignIn"), IsCancel = true };
        var create = new Button { Content = Texts.T("NewIdentity"), Classes = { "danger" } };
        var restore = new Button { Content = Texts.T("RestoreFromVault"), IsDefault = true, Classes = { "primary" } };
        cancel.Click += (_, _) => dialog.Close();
        restore.Click += (_, _) =>
        {
            choice = IdentityChoice.RestoreVaultIdentity;
            dialog.Close();
        };
        create.Click += async (_, _) =>
        {
            bool confirmed = await ConfirmAsync(dialog,
                Texts.T("ConfirmIdentityReplacement"),
                Texts.T("CreateANewCryptographicIdentity"),
                Texts.T("NewIdentity"));
            if (!confirmed)
                return;
            choice = IdentityChoice.CreateNewIdentity;
            dialog.Close();
        };
        root.Children.Add(ButtonRow(cancel, create, restore));
        dialog.Content = root;
        await dialog.ShowDialog(owner);
        return choice;
    }

    public static async Task DataTamperingAsync(Window owner, bool removedFromServer)
    {
        var dialog = CreateWindow(Texts.T("FileIntegrityCompromised"), 420);
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(Text(Texts.T("PossibleTamperingDetected"), 21, Brushes.IndianRed, FontWeight.SemiBold));
        var message = Text(Texts.T("AuthenticationFailed"));
        message.Margin = new Thickness(0, 8, 0, 8);
        root.Children.Add(message);
        root.Children.Add(Text(removedFromServer ? Texts.T("TamperedRemoved") : Texts.T("TamperedNotConfirmed"), 15, Brushes.IndianRed));
        var close = new Button { Content = Texts.T("Close"), IsDefault = true, IsCancel = true };
        close.Click += (_, _) => dialog.Close();
        root.Children.Add(ButtonRow(close));
        dialog.Content = root;
        dialog.Opened += (_, _) => close.Focus();
        await dialog.ShowDialog(owner);
    }
}
