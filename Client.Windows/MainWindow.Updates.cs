using Client.Windows.Services;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Client.Windows;

public partial class MainWindow
{
    private UpdateCheckService? _updateService;
    private AvailableUpdate? _availableUpdate;
    private bool _updateDialogOpen;

    /// <summary>Checks GitHub for a newer release once, in the background, after the window is shown.</summary>
    private void InitializeUpdateCheck()
    {
        ClientVersionLabel.MouseLeftButtonUp += (_, _) => ShowUpdateDialog();
        Closed += (_, _) => _updateService?.Dispose();
        Loaded += async (_, _) => await CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        Version current = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0);
        _updateService = new UpdateCheckService(current);
        AvailableUpdate? update = await _updateService.CheckAsync();
        if (update is null || !IsLoaded) return;

        _availableUpdate = update;
        ShowUpdateOnVersionLabel();

        if (string.Equals(LoadDismissedUpdateVersion(), update.Version.ToString(), StringComparison.Ordinal)) return;
        // Pop up only on the sign-in screen: installing closes NOX and must never interrupt a session or a transfer.
        if (AuthScreen.Visibility != Visibility.Visible || _authRequestBusy) return;
        ShowUpdateDialog();
    }

    /// <summary>The version label turns accent green and opens the update prompt when clicked.</summary>
    private void ShowUpdateOnVersionLabel()
    {
        if (_availableUpdate is null) return;
        ClientVersionLabel.Foreground = (Brush)FindResource("NoxAccentBrush");
        ClientVersionLabel.Cursor = Cursors.Hand;
        ClientVersionLabel.IsHitTestVisible = true;
        ClientVersionLabel.ToolTip = Texts.T("UpdateVersionTooltip", UpdateCheckService.Display(_availableUpdate.Version));
    }

    private void ShowUpdateDialog()
    {
        if (_availableUpdate is null || _updateService is null || _updateDialogOpen) return;
        AvailableUpdate update = _availableUpdate;
        string version = update.Version.ToString();

        _updateDialogOpen = true;
        var dialog = new Views.UpdateDialog(update, _updateService,
            string.Equals(LoadDismissedUpdateVersion(), version, StringComparison.Ordinal))
        {
            Owner = this
        };
        bool? result;
        try { result = dialog.ShowDialog(); }
        finally { _updateDialogOpen = false; }

        SaveDismissedUpdateVersion(dialog.DontShowAgain ? version : string.Empty);

        if (result == true && dialog.InstallerPath is string installer)
            StartInstallerAndExit(installer);
    }

    private void StartInstallerAndExit(string installerPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            MessageBox.Show(this, Texts.T("UpdateInstallerStartFailed"), "NOX", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // Close NOX so the installer can replace NOX.exe.
        Application.Current.Shutdown();
    }

    private string LoadDismissedUpdateVersion()
    {
        try { return _appSettingsService.Load().DismissedUpdateVersion ?? string.Empty; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return string.Empty;
        }
    }

    private void SaveDismissedUpdateVersion(string version)
    {
        try
        {
            var settings = _appSettingsService.Load();
            if (string.Equals(settings.DismissedUpdateVersion, version, StringComparison.Ordinal)) return;
            settings.DismissedUpdateVersion = version;
            _appSettingsService.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // Not being able to remember the choice only means the prompt may appear again.
        }
    }
}
