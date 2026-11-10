using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Client.Core;
using Client.MacOS.Services;

namespace Client.MacOS.Views;

/// <summary>
/// "New version available" prompt, same texts and choices as Client.Windows/Views/UpdateDialog.xaml:
/// "Don't show again for this version", Later and Update. Update downloads and verifies the installer
/// and the app package; on success the dialog closes with true and <see cref="Downloaded"/> set.
/// </summary>
internal sealed class UpdateDialog : Window
{
    private readonly AvailableUpdate _update;
    private readonly UpdateCheckService _service;
    private readonly CheckBox _dontShowAgain;
    private readonly StackPanel _progressPanel;
    private readonly ProgressBar _progressBar;
    private readonly TextBlock _progressText;
    private readonly TextBlock _errorText;
    private readonly Button _laterButton;
    private readonly Button _updateButton;
    private readonly bool _canInstall;
    private CancellationTokenSource? _download;
    private bool _closing;

    /// <param name="canInstall">False when NOX does not run from a replaceable NOX.app: Update then just
    /// closes the prompt with true, and the caller opens the release page.</param>
    public UpdateDialog(AvailableUpdate update, UpdateCheckService service, bool dismissedForThisVersion, bool canInstall)
    {
        _update = update;
        _service = service;
        _canInstall = canInstall;

        Title = Texts.T("UpdateAvailableTitle");
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("NoxWindowBrush");

        var header = new DockPanel();
        var badge = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(20),
            Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brush("NoxAccentMutedBrush"),
            Child = new Viewbox
            {
                Width = 20,
                Height = 20,
                Child = new Avalonia.Controls.Shapes.Path
                {
                    Classes = { "icon" },
                    Data = (Geometry)Application.Current!.FindResource("NoxIconArrowDown")!,
                    Stroke = Brush("NoxAccentBrush"),
                }
            }
        };
        DockPanel.SetDock(badge, Dock.Left);
        header.Children.Add(badge);
        header.Children.Add(new TextBlock
        {
            Text = Texts.T("UpdateAvailableTitle"),
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("NoxTextBrush"),
        });

        var message = new TextBlock
        {
            Text = Texts.T("UpdateAvailableMessage",
                UpdateCheckService.Display(update.Version), UpdateCheckService.Display(service.CurrentVersion)),
            Margin = new Thickness(0, 16, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Foreground = Brush("NoxTextMutedBrush"),
        };

        _progressBar = new ProgressBar { Height = 8, Minimum = 0, Maximum = 100, Value = 0 };
        _progressText = new TextBlock { Margin = new Thickness(0, 8, 0, 0), FontSize = 13, Foreground = Brush("NoxTextMutedBrush") };
        _progressPanel = new StackPanel { Margin = new Thickness(0, 18, 0, 0), IsVisible = false };
        _progressPanel.Children.Add(_progressBar);
        _progressPanel.Children.Add(_progressText);

        _errorText = new TextBlock
        {
            Margin = new Thickness(0, 14, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
            Foreground = Brush("NoxWarningBrush"),
        };

        _dontShowAgain = new CheckBox
        {
            Margin = new Thickness(0, 18, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = Texts.T("UpdateDontShowAgain"),
            IsChecked = dismissedForThisVersion,
        };

        _laterButton = new Button { Content = Texts.T("UpdateLater"), MinWidth = 120, Height = 46 };
        _updateButton = new Button
        {
            Content = Texts.T("UpdateNow"),
            MinWidth = 140,
            Height = 46,
            IsDefault = true,
            Classes = { "primary" },
        };
        _laterButton.Click += (_, _) => Later();
        _updateButton.Click += async (_, _) => await UpdateAsync();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 22, 0, 0),
        };
        buttons.Children.Add(_laterButton);
        buttons.Children.Add(_updateButton);

        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(header);
        root.Children.Add(message);
        root.Children.Add(_progressPanel);
        root.Children.Add(_errorText);
        root.Children.Add(_dontShowAgain);
        root.Children.Add(buttons);
        Content = root;

        Opened += (_, _) => _updateButton.Focus();
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Later();
        };
    }

    public bool DontShowAgain => _dontShowAgain.IsChecked == true;

    /// <summary>Verified installer script and app package, set when the download succeeded.</summary>
    public (string Installer, string Package)? Downloaded { get; private set; }

    private bool IsDownloading => _download is not null;

    private static IBrush Brush(string key) =>
        Application.Current!.TryFindResource(key, out object? value) && value is IBrush brush ? brush : Brushes.Transparent;

    private void Later()
    {
        if (IsDownloading)
        {
            _download!.Cancel();
            return;
        }
        Close(false);
    }

    private async Task UpdateAsync()
    {
        if (IsDownloading) return;
        if (!_canInstall)
        {
            Close(true);
            return;
        }

        _download = new CancellationTokenSource();
        _errorText.IsVisible = false;
        _progressPanel.IsVisible = true;
        _progressBar.Value = 0;
        _progressText.Text = Texts.T("UpdateDownloading", 0);
        _updateButton.IsEnabled = false;
        _dontShowAgain.IsEnabled = false;
        _laterButton.Content = Texts.T("Cancel");

        var progress = new Progress<double>(fraction =>
        {
            if (!IsDownloading) return;
            int percent = (int)Math.Round(fraction * 100);
            _progressBar.Value = percent;
            _progressText.Text = Texts.T("UpdateDownloading", percent);
        });

        try
        {
            Downloaded = await _service.DownloadAsync(_update, progress, _download.Token);
            if (_closing) return;
            _progressText.Text = Texts.T("UpdateStartingInstaller");
            Close(true);
        }
        catch (OperationCanceledException) when (_download.IsCancellationRequested)
        {
            ResetToPrompt();
            if (!_closing) Close(false);
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
        _progressPanel.IsVisible = false;
        _updateButton.IsEnabled = true;
        _dontShowAgain.IsEnabled = true;
        _laterButton.Content = Texts.T("UpdateLater");
    }

    private void ShowError(string message)
    {
        _errorText.Text = message;
        _errorText.IsVisible = true;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Closing the prompt (red button, Cmd+W) while downloading cancels the download.
        _closing = true;
        _download?.Cancel();
        base.OnClosing(e);
    }
}
