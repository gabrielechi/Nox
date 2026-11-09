using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Client.Windows;

public partial class MainWindow
{
    private bool _accountMenuOpen;

    /// <summary>
    /// The account card in the sidebar expands its menu (language, logout) while the mouse is over it,
    /// or while keyboard focus moved into it with the keyboard. One state drives one animation.
    /// </summary>
    private void InitializeAccountMenu()
    {
        AccountCard.MouseEnter += (_, _) => UpdateAccountMenu();
        AccountCard.MouseLeave += (_, _) => UpdateAccountMenu();
        AccountCard.IsKeyboardFocusWithinChanged += (_, _) => UpdateAccountMenu();
    }

    private void UpdateAccountMenu()
    {
        bool focusedWithKeyboard = AccountCard.IsKeyboardFocusWithin &&
            InputManager.Current.MostRecentInputDevice is KeyboardDevice;
        bool open = AccountCard.IsMouseOver || focusedWithKeyboard;
        if (open == _accountMenuOpen) return;
        _accountMenuOpen = open;

        var duration = TimeSpan.FromMilliseconds(open ? 180 : 150);
        IEasingFunction easing = new CubicEase { EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn };
        double to = open ? 1 : 0;

        if (AccountMenu.LayoutTransform is ScaleTransform scale)
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, duration) { EasingFunction = easing });
        AccountMenu.BeginAnimation(OpacityProperty, new DoubleAnimation(to, duration));
        if (AccountChevron.RenderTransform is RotateTransform chevron)
            chevron.Angle = open ? 0 : 180;
    }
}
