using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Client.Windows;

public partial class MainWindow
{
    private const double AuthHeaderHeight = 100;
    private const double AuthMaximumContentHeight = 720;
    private const double AuthWindowChromeHeight = 34;
    private const double AuthFooterHeight = 38;
    private bool _authLayoutReady;
    private bool _authResizeScheduled;
    private bool _authMeasuring;
    private bool _authWindowAnimating;
    private bool _authRequestBusy;
    private int _authAnimationVersion;
    private double _authTargetHeight;
    private Size _authWorkSize;

    private void InitializeAuthWindowLayout()
    {
        _authLayoutReady = true;
        AuthCardContent.SizeChanged += (_, _) => RequestAuthWindowResize(true);
        var messageProperty = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
            System.Windows.Controls.TextBlock.TextProperty, typeof(System.Windows.Controls.TextBlock));
        messageProperty.AddValueChanged(AuthErrorTextBlock, AuthMessageChanged);
        Closed += (_, _) =>
        {
            messageProperty.RemoveValueChanged(AuthErrorTextBlock, AuthMessageChanged);
            _authLayoutReady = false;
            StopAuthWindowAnimation();
        };
        ResizeAuthWindow(false);
    }

    private void AuthMessageChanged(object? sender, EventArgs e)
    {
        PrepareAuthTransition();
        AuthNotice.Visibility = string.IsNullOrWhiteSpace(AuthErrorTextBlock.Text)
            ? Visibility.Collapsed : Visibility.Visible;
        RequestAuthWindowResize(true);
    }

    private void RequestAuthWindowResize(bool animate)
    {
        if (!_authLayoutReady || _authMeasuring || _authResizeScheduled ||
            AuthScreen.Visibility != Visibility.Visible) return;

        _authResizeScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _authResizeScheduled = false;
            if (_authLayoutReady && AuthScreen.Visibility == Visibility.Visible)
                ResizeAuthWindow(animate);
        }));
    }

    private void PrepareAuthTransition()
    {
        if (!_authLayoutReady || !IsLoaded || _authWindowAnimating ||
            AuthScreen.Visibility != Visibility.Visible || AuthCardPreview.Source != null ||
            !SystemParameters.ClientAreaAnimation || AuthCard.ActualHeight <= 0) return;

        // Keep the old panel on screen while measuring the next phase. This image
        // is only held in memory and is released as soon as the transition ends.
        var size = AuthCard.RenderSize;
        var dpi = VisualTreeHelper.GetDpi(this);
        var preview = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(size.Width * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(size.Height * dpi.DpiScaleY)),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        preview.Render(AuthCard);
        preview.Freeze();
        AuthCardPreview.Source = preview;
        AuthCardPreview.Width = size.Width;
        AuthCardPreview.Height = size.Height;
        AuthCardPreview.Opacity = 1;
        AuthCardPreview.Visibility = Visibility.Visible;
        AuthCard.Opacity = 0;
        RequestAuthWindowResize(true);
    }

    private void ResizeAuthWindow(bool animate)
    {
        if (!_authLayoutReady || _authRequestBusy || AuthScreen.Visibility != Visibility.Visible) return;
        _authMeasuring = true;
        try
        {
            Rect work = Views.WindowLayout.WorkBounds(this);
            _authWorkSize = work.Size;
            // Use one scale for every phase on this monitor: revealing a password
            // field must never change the logo or font sizes.
            double availableWidth = Math.Max(1, work.Width - WindowScreenPadding);
            double availableHeight = Math.Max(1, work.Height - WindowScreenPadding);
            double scale = Math.Min(1, Math.Min(availableWidth / AuthDesignWidth,
                Math.Max(1, availableHeight - AuthWindowChromeHeight - AuthFooterHeight)
                / AuthMaximumContentHeight));

            // Measure the content independently of the animated window height.
            // In particular, a ScrollViewer must not feed a constrained height back
            // into the next animation frame.
            double frameWidth = AuthCard.Padding.Left + AuthCard.Padding.Right +
                AuthCard.BorderThickness.Left + AuthCard.BorderThickness.Right;
            double frameHeight = AuthCard.Padding.Top + AuthCard.Padding.Bottom +
                AuthCard.BorderThickness.Top + AuthCard.BorderThickness.Bottom;
            AuthCardContent.Measure(new Size(376 - frameWidth, double.PositiveInfinity));
            double cardHeight = Math.Ceiling(AuthCardContent.DesiredSize.Height + frameHeight);
            AuthCard.Height = cardHeight;
            double contentHeight = AuthHeaderHeight + cardHeight;
            AuthSurface.Height = contentHeight;
            double target = Math.Ceiling(AuthWindowChromeHeight + AuthFooterHeight + contentHeight * scale);
            target = Math.Min(availableHeight, target);
            double width = Math.Floor(AuthDesignWidth * scale);
            if (Math.Abs(target - _authTargetHeight) < .5 && Math.Abs(Width - width) < .5 &&
                (_authWindowAnimating || Math.Abs(Height - target) < .5))
            {
                if (!_authWindowAnimating) ClearAuthPreview();
                return;
            }

            var preview = AuthCardPreview.Source;
            double previewWidth = AuthCardPreview.Width, previewHeight = AuthCardPreview.Height;
            StopAuthWindowAnimation();
            _authTargetHeight = target;
            _designWidth = AuthDesignWidth;
            _designHeight = target;
            SizeToContent = SizeToContent.Manual;
            ResizeMode = ResizeMode.NoResize;
            MinHeight = 1;
            MaxHeight = availableHeight;
            MinWidth = 1;
            MaxWidth = availableWidth;
            Width = width;
            MinWidth = MaxWidth = width;
            if (AuthSurface.LayoutTransform is not ScaleTransform transform || Math.Abs(transform.ScaleX - scale) > .001)
                AuthSurface.LayoutTransform = new ScaleTransform(scale, scale);

            double start = double.IsFinite(Height) ? Height : target;
            bool transition = animate && IsLoaded && WindowState == WindowState.Normal &&
                SystemParameters.ClientAreaAnimation && Math.Abs(start - target) > 1;
            if (!transition)
            {
                Height = target;
                if (IsLoaded) Views.WindowLayout.KeepOnMonitor(this);
                return;
            }

            _authWindowAnimating = true;
            int version = _authAnimationVersion;
            bool shrinking = target < start;
            double delay = shrinking ? 90 : 0;
            if (preview != null)
            {
                AuthCardPreview.Source = preview;
                AuthCardPreview.Width = previewWidth;
                AuthCardPreview.Height = previewHeight;
                AuthCardPreview.Visibility = Visibility.Visible;
                AuthCardPreview.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0,
                    TimeSpan.FromMilliseconds(90)) { BeginTime = TimeSpan.FromMilliseconds(shrinking ? 0 : 220) });
            }

            AuthCard.Opacity = 0;
            AuthCard.IsHitTestVisible = false;
            var reveal = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(110))
            {
                BeginTime = TimeSpan.FromMilliseconds(shrinking ? 90 : 220)
            };
            reveal.Completed += (_, _) =>
            {
                if (version != _authAnimationVersion) return;
                ClearAuthPreview();
                if (!shrinking) _authWindowAnimating = false;
            };
            AuthCard.BeginAnimation(OpacityProperty, reveal);

            var resize = new DoubleAnimation(start, target, TimeSpan.FromMilliseconds(220))
            {
                BeginTime = TimeSpan.FromMilliseconds(delay),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            resize.Completed += (_, _) =>
            {
                if (version != _authAnimationVersion) return;
                Height = target;
                BeginAnimation(HeightProperty, null);
                double top = Top;
                BeginAnimation(TopProperty, null);
                Top = top;
                if (shrinking) _authWindowAnimating = false;
                Views.WindowLayout.KeepOnMonitor(this);
            };
            BeginAnimation(HeightProperty, resize);
            if (double.IsFinite(Top))
            {
                double top = Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - target));
                if (Math.Abs(top - Top) > .5)
                    BeginAnimation(TopProperty, new DoubleAnimation(Top, top, resize.Duration)
                    { BeginTime = resize.BeginTime, EasingFunction = resize.EasingFunction });
            }
        }
        finally { _authMeasuring = false; }
    }

    private void ClearAuthPreview()
    {
        AuthCard.BeginAnimation(OpacityProperty, null);
        AuthCard.Opacity = 1;
        AuthCard.IsHitTestVisible = true;
        AuthCardPreview.BeginAnimation(OpacityProperty, null);
        AuthCardPreview.Source = null;
        AuthCardPreview.Visibility = Visibility.Collapsed;
    }

    private void StopAuthWindowAnimation()
    {
        ++_authAnimationVersion;
        double height = Height, top = Top;
        BeginAnimation(HeightProperty, null);
        BeginAnimation(TopProperty, null);
        if (double.IsFinite(height)) Height = height;
        if (double.IsFinite(top)) Top = top;
        _authWindowAnimating = false;
        if (AuthCard != null) ClearAuthPreview();
    }
}
