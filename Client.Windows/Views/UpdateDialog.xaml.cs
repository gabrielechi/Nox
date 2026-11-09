using Client.Windows.Services;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;

namespace Client.Windows.Views;

/// <summary>
/// "New version available" prompt. "Update" downloads and verifies the installer; on success the dialog
/// closes with DialogResult = true and <see cref="InstallerPath"/> set.
/// </summary>
internal partial class UpdateDialog : Window
{
    private readonly AvailableUpdate _update;
    private readonly UpdateCheckService _service;
    private CancellationTokenSource? _download;
    private bool _closing;

    public UpdateDialog(AvailableUpdate update, UpdateCheckService service, bool dismissedForThisVersion)
    {
        _update = update;
        _service = service;
        InitializeComponent();
        MessageText.Text = Texts.T("UpdateAvailableMessage",
            UpdateCheckService.Display(update.Version), UpdateCheckService.Display(service.CurrentVersion));
        DontShowAgainCheckBox.IsChecked = dismissedForThisVersion;
        Loaded += (_, _) => UpdateButton.Focus();
    }

    public bool DontShowAgain => DontShowAgainCheckBox.IsChecked == true;

    public string? InstallerPath { get; private set; }

    private bool IsDownloading => _download is not null;

    private void Frame_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource == Frame) DragMove();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        LaterButton_Click(sender, e);
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsDownloading)
        {
            _download!.Cancel();
            return;
        }
        DialogResult = false;
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsDownloading) return;

        _download = new CancellationTokenSource();
        ErrorText.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;
        ProgressText.Text = Texts.T("UpdateDownloading", 0);
        UpdateButton.IsEnabled = false;
        DontShowAgainCheckBox.IsEnabled = false;
        LaterButton.Content = Texts.T("Cancel");

        var progress = new Progress<double>(fraction =>
        {
            int percent = (int)Math.Round(fraction * 100);
            DownloadProgress.Value = percent;
            ProgressText.Text = Texts.T("UpdateDownloading", percent);
        });

        try
        {
            string path = await _service.DownloadInstallerAsync(_update, progress, _download.Token);
            InstallerPath = path;
            if (_closing) return;
            ProgressText.Text = Texts.T("UpdateStartingInstaller");
            DialogResult = true;
        }
        catch (OperationCanceledException) when (_download.IsCancellationRequested)
        {
            ResetToPrompt();
            if (!_closing) DialogResult = false;
        }
        catch (UpdateIntegrityException)
        {
            ResetToPrompt();
            ShowError(Texts.T("UpdateIntegrityFailed"));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                                       or InvalidDataException or OperationCanceledException)
        {
            ResetToPrompt();
            ShowError(Texts.T("UpdateDownloadFailed"));
        }
    }

    private void ResetToPrompt()
    {
        _download?.Dispose();
        _download = null;
        ProgressPanel.Visibility = Visibility.Collapsed;
        UpdateButton.IsEnabled = true;
        DontShowAgainCheckBox.IsEnabled = true;
        LaterButton.Content = Texts.T("UpdateLater");
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing the prompt (Alt+F4) while downloading cancels the download.
        _closing = true;
        _download?.Cancel();
        base.OnClosing(e);
    }
}
