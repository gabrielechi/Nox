namespace Client.MacOS;

// The window is split across partial files:
//   MainWindow.Platform.cs        state, layout, language, pickers, drag and drop, navigation, dialogs
//   MainWindow.Flows.cs           sign-in, registration, recipient check, send, download (ported from WPF)
//   MainWindow.PasswordStrength.cs, MainWindow.AccountMenu.cs
public partial class MainWindow;
