// Ported from Client.Windows/MainWindow.PasswordStrength.cs (same estimator and thresholds).
using Avalonia.Media;
using Avalonia.Threading;
using Client.Core;
using Client.Core.Services;

namespace Client.MacOS;

public partial class MainWindow
{
    private readonly DispatcherTimer _strengthTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly SemaphoreSlim _strengthGate = new(1, 1);
    private CancellationTokenSource? _strengthCancellation;
    private bool _strengthReady;
    private int? _strengthScore;
    private bool _strengthPending;
    private bool _strengthUnavailable;

    private void InitializePasswordStrength()
    {
        _strengthReady = true;
        _strengthTimer.Tick += EvaluatePasswordStrength;
        Closed += (_, _) =>
        {
            _strengthReady = false;
            _strengthTimer.Stop();
            _strengthCancellation?.Cancel();
            _strengthCancellation?.Dispose();
        };
        QueuePasswordStrength();
    }

    private void QueuePasswordStrength()
    {
        if (!_strengthReady) return;
        _strengthTimer.Stop();
        _strengthCancellation?.Cancel();
        _strengthCancellation?.Dispose();
        _strengthCancellation = null;
        _strengthScore = null;
        _strengthUnavailable = false;
        _strengthPending = _usernameExists == false && (PasswordBox.Text ?? string.Empty).Length > 0;
        UpdatePasswordStrengthVisuals();
        if (_strengthPending)
        {
            _strengthCancellation = new CancellationTokenSource();
            _strengthTimer.Start();
        }
    }

    private async void EvaluatePasswordStrength(object? sender, EventArgs e)
    {
        _strengthTimer.Stop();
        if (!_strengthReady || _strengthCancellation is null || _usernameExists != false) return;
        var token = _strengthCancellation.Token;
        string password = (PasswordBox.Text ?? string.Empty);
        string username = (UsernameTextBox.Text ?? string.Empty).Trim();
        try
        {
            int score = await Task.Run(async () =>
            {
                await _strengthGate.WaitAsync(token);
                try
                {
                    token.ThrowIfCancellationRequested();
                    return PasswordStrengthEstimator.Estimate(password, username);
                }
                finally { _strengthGate.Release(); }
            }, token);
            if (token.IsCancellationRequested || !_strengthReady || _usernameExists != false) return;
            _strengthScore = score;
            _strengthPending = false;
            UpdatePasswordStrengthVisuals();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (token.IsCancellationRequested || !_strengthReady || _usernameExists != false) return;
            _strengthPending = false;
            _strengthUnavailable = true;
            UpdatePasswordStrengthVisuals();
        }
    }

    private void UpdatePasswordStrengthVisuals()
    {
        if (PasswordStrengthPanel is null) return;
        PasswordStrengthPanel.IsVisible = _usernameExists == false;
        int level = _strengthScore is int score ? Math.Clamp(score, 1, 4) : 0;
        PasswordStrengthBar.Value = level;
        PasswordStrengthBar.Foreground = new SolidColorBrush(level switch
        {
            1 => Color.FromRgb(245, 110, 122),
            2 => Color.FromRgb(240, 173, 78),
            3 => Color.FromRgb(212, 209, 95),
            4 => Color.FromRgb(64, 217, 119),
            _ => Color.FromRgb(170, 180, 195)
        });
        PasswordStrengthLabel.Text = _strengthPending ? Texts.T("StrengthCalculating")
            : _strengthUnavailable ? Texts.T("StrengthUnavailable")
            : level == 0 ? Texts.T("StrengthEmpty")
            : Texts.T("StrengthResult", Texts.T(level switch
            {
                1 => "StrengthWeak", 2 => "StrengthFair", 3 => "StrengthGood", _ => "StrengthStrong"
            }));
    }
}
