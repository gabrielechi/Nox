using System.Diagnostics;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Client.Core;
using Client.MacOS.Services;
using Client.MacOS.Views;

namespace Client.MacOS;

/// <summary>
/// Update check, as in Client.Windows/MainWindow.Updates.cs: once per start the client asks GitHub for the
/// latest release; if it is newer, the version label turns green and, on the sign-in screen, the prompt opens.
/// "Update" downloads install-nox.sh and NOX-macOS-arm64.zip (both checked against GitHub's SHA-256),
/// starts the script and closes NOX: the script waits for this process to end, replaces the app bundle
/// and opens the new version.
/// </summary>
public partial class MainWindow
{
    private UpdateCheckService? _updateService;
    private AvailableUpdate? _availableUpdate;
    private bool _updateDialogOpen;

    private void InitializeUpdateCheck()
    {
        ClientVersionLabel.PointerReleased += async (_, e) =>
        {
            if (_availableUpdate is null || e.InitialPressMouseButton != MouseButton.Left) return;
            await ShowUpdateDialogAsync();
        };
        Closed += (_, _) => _updateService?.Dispose();
        Opened += async (_, _) => await CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_updateService is not null) return;
        Version current = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0);
        _updateService = new UpdateCheckService(current);
        AvailableUpdate? update = await _updateService.CheckAsync();
        if (update is null || !IsVisible) return;

        _availableUpdate = update;
        ShowUpdateOnVersionLabel();

        if (string.Equals(LoadDismissedUpdateVersion(), update.Version.ToString(), StringComparison.Ordinal)) return;
        // Pop up only on the sign-in screen: installing closes NOX and must never interrupt a session or a transfer.
        if (!AuthScreen.IsVisible || _authRequestBusy) return;
        await ShowUpdateDialogAsync();
    }

    /// <summary>The version label turns accent green and opens the update prompt when clicked.</summary>
    private void ShowUpdateOnVersionLabel()
    {
        if (_availableUpdate is null) return;
        ClientVersionLabel.Foreground = Res("NoxAccentBrush");
        ClientVersionLabel.Cursor = new Cursor(StandardCursorType.Hand);
        RefreshUpdateTooltip();
    }

    private void RefreshUpdateTooltip()
    {
        if (_availableUpdate is null) return;
        ToolTip.SetTip(ClientVersionLabel, Texts.T("UpdateVersionTooltip", UpdateCheckService.Display(_availableUpdate.Version)));
    }

    private async Task ShowUpdateDialogAsync()
    {
        if (_availableUpdate is null || _updateService is null || _updateDialogOpen) return;
        AvailableUpdate update = _availableUpdate;
        string version = update.Version.ToString();

        // Outside an app bundle (e.g. "dotnet run") there is nothing to replace: open the release page instead.
        string? bundle = CurrentAppBundle();

        _updateDialogOpen = true;
        var dialog = new UpdateDialog(update, _updateService,
            string.Equals(LoadDismissedUpdateVersion(), version, StringComparison.Ordinal),
            canInstall: bundle is not null);
        bool result;
        try { result = await dialog.ShowDialog<bool>(this); }
        finally { _updateDialogOpen = false; }

        SaveDismissedUpdateVersion(dialog.DontShowAgain ? version : string.Empty);

        if (!result) return;
        if (bundle is null || dialog.Downloaded is not { } files)
        {
            OpenReleasePage(update.ReleasePageUrl);
            return;
        }
        await StartInstallerAndExitAsync(files.Installer, files.Package, update, bundle);
    }

    /// <summary>Path of the running NOX.app, or null when NOX does not run from a bundle that can be replaced.</summary>
    private static string? CurrentAppBundle()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        // AppContext.BaseDirectory is ".../NOX.app/Contents/MacOS/".
        var macOS = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('/'));
        DirectoryInfo? bundle = macOS.Parent?.Parent;
        if (macOS.Name != "MacOS" || macOS.Parent?.Name != "Contents" || bundle is null || bundle.Name != "NOX.app") return null;
        // A translocated copy (app opened from a quarantined download) lives in a read-only random folder.
        if (bundle.FullName.Contains("/AppTranslocation/", StringComparison.Ordinal)) return null;
        return bundle.FullName;
    }

    private async Task StartInstallerAndExitAsync(string installer, string package, AvailableUpdate update, string bundle)
    {
        string logFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "NOX");
        try
        {
            Directory.CreateDirectory(logFolder);
            var start = new ProcessStartInfo("/bin/bash") { UseShellExecute = false };
            start.ArgumentList.Add(installer);
            start.Environment["NOX_ZIP_FILE"] = package;
            start.Environment["NOX_ZIP_SHA256"] = update.Package.Sha256;
            start.Environment["NOX_INSTALL_DIR"] = Path.GetDirectoryName(bundle)!;
            start.Environment["NOX_WAIT_PID"] = Environment.ProcessId.ToString();
            start.Environment["NOX_GUI"] = "1";
            start.Environment["NOX_LOG"] = Path.Combine(logFolder, "update.log");
            using Process? process = Process.Start(start);
            if (process is null) throw new InvalidOperationException();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await ShowInfoAsync(Texts.T("UpdateInstallerStartFailed"), "NOX");
            return;
        }
        // Close NOX: the script waits for this process to end before replacing NOX.app.
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
        else
            Close();
    }

    private static void OpenReleasePage(string url)
    {
        try { Process.Start(new ProcessStartInfo("open") { ArgumentList = { url }, UseShellExecute = false })?.Dispose(); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
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
