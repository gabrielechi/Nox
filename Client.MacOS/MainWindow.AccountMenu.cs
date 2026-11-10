using Avalonia;
using Avalonia.Input;
using Avalonia.Media;

namespace Client.MacOS;

public partial class MainWindow
{
    private bool _accountMenuOpen;

    /// <summary>
    /// The account card in the sidebar shows its menu (language, logout) while the pointer is over it
    /// or keyboard focus is inside it. One state drives the menu, as in the Windows client.
    /// </summary>
    private void InitializeAccountMenu()
    {
        AccountCard.PropertyChanged += (_, e) =>
        {
            if (e.Property == InputElement.IsPointerOverProperty || e.Property == InputElement.IsKeyboardFocusWithinProperty)
                UpdateAccountMenu();
        };
    }

    private void UpdateAccountMenu()
    {
        bool open = AccountCard.IsPointerOver || AccountCard.IsKeyboardFocusWithin;
        if (open == _accountMenuOpen) return;
        _accountMenuOpen = open;

        AccountMenu.IsVisible = open;
        AccountMenu.Opacity = open ? 1 : 0;
        if (AccountChevron.RenderTransform is RotateTransform chevron)
            chevron.Angle = open ? 0 : 180;
    }
}
