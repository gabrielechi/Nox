using Avalonia.Threading;
using Client.Core.Services;

namespace Client.MacOS;

public partial class MainWindow
{
    /// <summary>Refreshes a progress bar and its status text a few times per second while a transfer runs.</summary>
    private sealed class ProgressTicker : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private readonly Action _tick;
        private bool _stopped;

        public ProgressTicker(Action tick)
        {
            _tick = tick;
            _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(150) };
            _timer.Tick += (_, _) => { if (!_stopped) _tick(); };
            _tick();
            _timer.Start();
        }

        public void Dispose()
        {
            if (_stopped) return;
            _stopped = true;
            _timer.Stop();
        }
    }

    /// <summary>"Caricamento file 1/3 · 42%" or, with a byte counter, "… · 42% (105 MB / 250 MB)".</summary>
    private static string WithProgress(string phase, TransferProgress overall, TransferProgress? bytes)
    {
        string text = phase.TrimEnd('.', '…', ' ') + $" · {overall.Percent}%";
        return bytes is null ? text : text + $" ({FormatFileSize(bytes.Completed)} / {FormatFileSize(bytes.Total)})";
    }
}
