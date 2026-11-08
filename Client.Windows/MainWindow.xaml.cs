using Client.Windows.Interfaces;
using Client.Windows.Models;
using Client.Windows.Services;
using CryptoEngine.Interfaces;
using CryptoEngine.Models;
using CryptoEngine.Services;
using Microsoft.Win32;
using Shared.DTO;
using Shared.DTO.Auth;
using Shared.DTO.Transfers;
using Shared.DTO.X3DH;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Client.Windows
{

    public partial class MainWindow : Window
    {
        private const int DwmwaCaptionColor = 35;
        private const int DwmwaTextColor = 36;

        private readonly DispatcherTimer _inboxPollingTimer = new();
        private bool _isInboxPollingRefreshRunning;
        private readonly SemaphoreSlim _inboxRefreshGate = new(1, 1);
        private bool _isDownloading;
        private bool _isManualInboxRefresh;
        private int _inboxRevision;

        private static string DropZoneDisabledText => Texts.T("SelectARecipient");
        private static string DropZoneReadyText => Texts.T("DragFilesHereOrClickTo");
        private static string DropZoneSelectedFilesText => Texts.T("SelectedFiles");
        private const int MaximumFilesPerTransfer = 20;
        private static string RecipientPlaceholderText => Texts.T("SearchForAUser");
        private static string SendToSelfDisplayText => Texts.T("Me");
        private static string DirectoryPlaceholder => Texts.T("DefaultFolder");
        private const double AuthDesignWidth = 432;
        private const double DashboardDesignWidth = 776;
        private const double DashboardDesignHeight = 582;
        private const double WindowScreenPadding = 32;

        private readonly ApiClient _apiClient = new();
        private readonly ClientAppSettingsService _appSettingsService;
        private ClientSession? _session;

        private readonly IUserVaultService _vaultService = new UserVaultService(new ArgonKeyDerivationService(), new AesGcmSymmetricService());
        private readonly IArgonKeyDerivationService _argonService = new ArgonKeyDerivationService();
        private readonly IKeyPairService _keyPairService = new KeyPairService();
        private readonly IPreKeyBootstrapService _preKeyBootstrapService = new PreKeyBootstrapService(new KeyPairService());
        private readonly IIdentityFingerprintService _fingerprintService = new IdentityFingerprintService();

        private PreKeyBundleResponse? _currentRecipientBundle;
        private bool _recipientBundleMustBeRefreshed;
        private int _recipientCheckVersion;
        private string? _sessionPassword;
        private readonly List<string> _selectedFilePaths = [];
        private UserKeyPayload? _vault;
        private bool _isRefreshingTrustedRecipients;
        private bool _isServerConnectionVerified;
        private bool? _usernameExists;
        private bool _suppressRememberServerChange = true;
        private bool _isApplyingSendToSelfMode;
        private bool _suppressSendToSelfAutoCheck;
        private bool _recipientLocked;

        private readonly IX3dhService _x3dhService = new X3dhService(new KeyPairService(), new HkdfService());
        private readonly IX3dhHeaderSerializer _x3dhHeaderSerializer = new X3dhHeaderJsonSerializer();

        private readonly IFileKeyDerivationService _fileKeyDerivationService = new FileKeyDerivationService(new HkdfService());
        private readonly IFileMetadataProtector _fileMetadataProtector = new FileMetadataProtector(new AesGcmSymmetricService());
        private readonly IFileContentEncryptionService _fileContentEncryptionService = new FileContentEncryptionService(new AesGcmSymmetricService());

        private readonly List<TransferSummaryResponse> _inboxTransfers = new List<TransferSummaryResponse>();

        private CheckBox? SendToSelfCheckBoxControl => FindName("SendToSelfCheckBox") as CheckBox;

        private enum OwnIdentityRecoveryAction
        {
            Cancel,
            RestoreVaultIdentity,
            CreateNewIdentity
        }

        public MainWindow() : this(new ClientAppSettingsService()) { }

        internal MainWindow(ClientAppSettingsService settingsService)
        {
            _appSettingsService = settingsService;
            var settings = _appSettingsService.Load();
            Texts.Apply(settings.Language);
            InitializeComponent();
            var version = typeof(MainWindow).Assembly.GetName().Version!;
            ClientVersionLabel.Text = "v." + version.ToString(version.Build > 0 ? 3 : 2);
            ApplyRecipientPlaceholderIfEmpty();
            SourceInitialized += MainWindow_SourceInitialized;
            LocationChanged += (_, _) => UpdateWorkspaceLayout();
            SizeChanged += (_, _) => ArrangeDashboardHeader();
            DpiChanged += (_, _) => Dispatcher.InvokeAsync(UpdateWorkspaceLayout);

            _inboxPollingTimer.Interval = TimeSpan.FromSeconds(5);
            _inboxPollingTimer.Tick += InboxPollingTimer_Tick;

            SetSendButtonEnabled(false);
            SetDownloadButtonEnabled(false);
            LoadClientAppSettings(settings);
            _suppressRememberServerChange = false;
            InitializeAuthWindowLayout();
            InitializePasswordStrength();
        }

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            (PresentationSource.FromVisual(this) as HwndSource)?.AddHook(MonitorConfigurationChanged);
            UpdateWorkspaceLayout();
        }

        private IntPtr MonitorConfigurationChanged(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message is 0x007E or 0x001A) // WM_DISPLAYCHANGE, WM_SETTINGCHANGE
                Dispatcher.InvokeAsync(UpdateWorkspaceLayout);
            return IntPtr.Zero;
        }

        private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

        private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

        private static void ApplyNativeTitleBarColors(Window window)
        {
            IntPtr windowHandle = new WindowInteropHelper(window).Handle;

            if (windowHandle == IntPtr.Zero)
                return;

            int captionColor = ToColorRef(15, 23, 42);
            int textColor = ToColorRef(243, 246, 251);

            DwmSetWindowAttribute(windowHandle, DwmwaCaptionColor, ref captionColor, Marshal.SizeOf<int>());
            DwmSetWindowAttribute(windowHandle, DwmwaTextColor, ref textColor, Marshal.SizeOf<int>());
        }

        private static int ToColorRef(byte red, byte green, byte blue)
        {
            return red | (green << 8) | (blue << 16);
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd,
            int dwAttribute,
            ref int pvAttribute,
            int cbAttribute);

        private async void InboxPollingTimer_Tick(object? sender, EventArgs e)
        {
            if (_isInboxPollingRefreshRunning || _isDownloading || _isManualInboxRefresh || _inboxRefreshGate.CurrentCount == 0)
                return;

            if (_session is null)
                return;

            try
            {
                _isInboxPollingRefreshRunning = true;
                await RefreshInboxAsync();
            }
            catch
            {
                // Polling must not interrupt the user. Manual refresh will still show errors.
            }
            finally
            {
                _isInboxPollingRefreshRunning = false;
            }
        }

        private void LoadClientAppSettings(ClientAppSettings settings)
        {
            LoginLanguageBox.SelectedValue = DashboardLanguageBox.SelectedValue = Texts.Language;

            if (settings.RememberServer && !string.IsNullOrWhiteSpace(settings.RememberedServerUrl))
            {
                RememberServerCheckBox.IsChecked = true;
                ServerUrlTextBox.Text = settings.RememberedServerUrl;
            }

            UseDefaultDownloadDirectoryCheckBox.IsChecked = settings.UseDefaultDownloadDirectory;
            SetDefaultDownloadDirectoryText(settings.DefaultDownloadDirectory);
        }

        private void Language_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || sender is not ComboBox box || box.SelectedValue is not string code || code == Texts.Language)
                return;

            bool placeholder = IsRecipientPlaceholderActive();
            string directory = GetDefaultDownloadDirectoryFromTextBox();
            var settings = _appSettingsService.Load();
            settings.Language = code;
            try { _appSettingsService.Save(settings); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                box.SelectedValue = Texts.Language;
                MessageBox.Show(this, Texts.T("CannotSaveLanguage"), "NOX", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Texts.Apply(code);
            LoginLanguageBox.SelectedValue = DashboardLanguageBox.SelectedValue = code;
            if (placeholder) RecipientUsernameTextBox.Text = RecipientPlaceholderText;
            SetDefaultDownloadDirectoryText(directory);
            foreach (var label in new[] { AuthErrorTextBlock, SignedInUserTextBlock, RecipientStatusTextBlock,
                         DropZonePromptTextBlock, SelectedFilesStatusTextBlock, InboxStatusTextBlock, ContactsStatus })
            {
                string next = Texts.Refresh(label.Text);
                if (next != label.Text) label.Text = next;
            }
            foreach (var button in new[] { SignInButton, CreateAccountButton, SendEncryptedButton, RefreshInboxButton, DownloadDecryptButton, CheckRecipientButton })
                if (button.Content is string old)
                {
                    string next = Texts.Refresh(old);
                    if (next != old) button.Content = next;
                }
            UpdateRegistrationHint();
            UpdatePasswordStrengthVisuals();
            int selection = InboxListBox.SelectedIndex;
            for (int i = 0; i < _inboxTransfers.Count; i++) InboxListBox.Items[i] = FormatInboxTransfer(_inboxTransfers[i]);
            InboxListBox.SelectedIndex = selection;
        }

        private double _designWidth;
        private double _designHeight;

        private void ApplyScaledWindowSize(double designWidth, double designHeight)
        {
            StopAuthWindowAnimation();
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
            _designWidth = designWidth;
            _designHeight = designHeight;
            SizeToContent = SizeToContent.Manual;
            ResizeMode = ResizeMode.NoResize;
            UpdateWorkspaceLayout();
            Width = Math.Clamp(designWidth, MinWidth, MaxWidth);
            Height = Math.Clamp(designHeight, MinHeight, MaxHeight);
            Dispatcher.InvokeAsync(() => Views.WindowLayout.KeepOnMonitor(this), DispatcherPriority.Loaded);
        }

        private void UpdateWorkspaceLayout()
        {
            if (AuthScreen.Visibility == Visibility.Visible && _authLayoutReady)
            {
                if (Views.WindowLayout.WorkSize(this) != _authWorkSize)
                    RequestAuthWindowResize(false);
                return;
            }
            if (_designWidth == 0) return;
            Size area = Views.WindowLayout.WorkSize(this);
            double maxWidth = Math.Min(_designWidth, Math.Max(1, area.Width - WindowScreenPadding));
            double maxHeight = Math.Min(_designHeight, Math.Max(1, area.Height - WindowScreenPadding));
            MinWidth = maxWidth;
            MinHeight = maxHeight;
            MaxWidth = maxWidth;
            MaxHeight = maxHeight;
        }

        private void ArrangeDashboardHeader()
        {
            if (HeaderActions is null) return;
            bool compact = ActualWidth < 540;
            Grid.SetRow(HeaderActions, compact ? 1 : 0);
            Grid.SetColumn(HeaderActions, compact ? 0 : 1);
            Grid.SetColumnSpan(HeaderActions, compact ? 2 : 1);
            HeaderActions.Margin = compact ? new Thickness(0, 6, 0, 0) : new Thickness(0);
        }

        private async void SignInButton_Click(object sender, RoutedEventArgs e)
        {
            AuthErrorTextBlock.Text = string.Empty;
            AuthErrorTextBlock.Foreground = System.Windows.Media.Brushes.IndianRed;

            if (!TryGetServerAddress(out string serverUrl))
            {
                AuthErrorTextBlock.Text = Texts.T("EnterAValidServerAddressFor");
                return;
            }

            if (!_isServerConnectionVerified)
            {
                await CheckServerConnectionAsync(serverUrl);
                return;
            }

            string username = UsernameTextBox.Text.Trim();
            string password = PasswordBox.Password;
            if (_usernameExists == false) return;

            if (string.IsNullOrWhiteSpace(username))
            {
                AuthErrorTextBlock.Text = Texts.T("UsernameIsRequired");
                return;
            }

            if (_usernameExists is null)
            {
                SetLoginInputsBusy(true);
                SignInButton.Content = Texts.T("Checking");
                try
                {
                    await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
                    bool exists = await _apiClient.UsernameExistsAsync(username);
                    SetUsernameChoice(exists ? true : null);
                    if (exists) PasswordBox.Focus();
                    else AuthErrorTextBlock.Text = Texts.T("LoginError");
                }
                catch (Exception ex)
                {
                    SignInButton.Content = Texts.T("Continue");
                    AuthErrorTextBlock.Text = GetUserFacingError(ex);
                }
                finally { SetLoginInputsBusy(false); }
                return;
            }

            if (string.IsNullOrWhiteSpace(password))
            {
                AuthErrorTextBlock.Text = Texts.T("PasswordIsRequired");
                return;
            }

            try
            {
                SetLoginInputsBusy(true);
                SignInButton.Content = Texts.T("SigningIn");

                _session = await _apiClient.LoginAsync(serverUrl, username, password);

                try
                {
                    _vault = _vaultService.DecryptVault(
                        password,
                        _session.PayloadSalt,
                        _session.EncryptedKeyPayload);

                }
                catch (Exception ex)
                {
                    AuthErrorTextBlock.Text = Texts.T("SignedInButTheVaultCould", GetUserFacingError(ex));
                    return;
                }

                if (_vault is null)
                    throw new InvalidOperationException(Texts.T("CouldNotLoadTheCryptographicVault"));

                if (ServerIdentityKeysDifferFromVault(_session, _vault))
                {
                    OwnIdentityRecoveryAction recoveryAction = ShowOwnIdentityMismatchWarning(
                        _session,
                        _vault);

                    if (recoveryAction == OwnIdentityRecoveryAction.Cancel)
                    {
                        _apiClient.ClearAuthentication();
                        _session = null;
                        _vault = null;
                        AuthErrorTextBlock.Text = Texts.T("SignInCancelledCryptographicIdentityNot");
                        return;
                    }

                    await RecoverOwnIdentityAsync(recoveryAction, password);
                }

                _sessionPassword = password;

                try
                {
                    await _preKeyBootstrapService.EnsurePreKeysAsync(_apiClient, _vault);
                }
                catch (PreKeySynchronizationException)
                {
                    await RecoverOwnIdentityAsync(
                        OwnIdentityRecoveryAction.RestoreVaultIdentity,
                        password,
                        showConfirmation: false);
                }

                byte[] updatedEncryptedKeyPayload = _vaultService.EncryptVault(password, _session.PayloadSalt, _vault);

                await _apiClient.UpdateVaultAsync(
                    new UpdateVaultRequest
                    {
                        EncryptedKeyPayload = updatedEncryptedKeyPayload
                    });

                _session.EncryptedKeyPayload = updatedEncryptedKeyPayload;

                CurrentUserResponse currentUser = await _apiClient.GetCurrentUserAsync();

                SignedInUserTextBlock.Text = Texts.T("Hello", currentUser.Username);
                RefreshTrustedRecipientsComboBox();

                AuthScreen.Visibility = Visibility.Collapsed;
                Title = "NOX | End-to-End Encrypted File Transfer";
                MainShell.Visibility = Visibility.Visible;
                ApplyScaledWindowSize(DashboardDesignWidth, DashboardDesignHeight);

                await RefreshInboxAsync();
                _inboxPollingTimer.Start();
            }
            catch (Exception ex)
            {
                AuthErrorTextBlock.Text = GetUserFacingError(ex);
            }
            finally
            {
                SetLoginInputsBusy(false);
                SignInButton.Content = Texts.T("SignIn");
            }
        }

        private bool TryGetServerAddress(out string address)
        {
            address = string.Empty;
            string host = ServerUrlTextBox.Text.Trim();
            string scheme = ServerSchemeBox.SelectedIndex == 1 ? "http" : "https";
            if (host.Length == 0 || host.Contains("://") ||
                !Uri.TryCreate($"{scheme}://{host}", UriKind.Absolute, out var uri) ||
                uri.Host.Length == 0 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                return false;
            address = uri.AbsoluteUri.TrimEnd('/');
            return true;
        }

        private void SetLoginInputsBusy(bool busy)
        {
            _authRequestBusy = busy;
            if (busy) StopAuthWindowAnimation();
            if (!busy) RequestAuthWindowResize(true);
            SignInButton.IsEnabled = ServerSchemeBox.IsEnabled = ChangeServerButton.IsEnabled =
                SwitchAuthModeButton.IsEnabled = !busy;
            ServerUrlTextBox.IsReadOnly = UsernameTextBox.IsReadOnly = busy;
            PasswordBox.IsEnabled = !busy && _usernameExists.HasValue;
            UpdateRegistrationHint();
        }

        private void SetUsernameChoice(bool? exists)
        {
            if (_usernameExists != exists) PrepareAuthTransition();
            _usernameExists = exists;
            PasswordBox.Clear();
            PasswordLabelTextBlock.Visibility = PasswordBox.Visibility = exists.HasValue ? Visibility.Visible : Visibility.Collapsed;
            PasswordBox.IsEnabled = exists.HasValue;
            SignInButton.Visibility = exists == false ? Visibility.Collapsed : Visibility.Visible;
            CreateAccountButton.Visibility = exists == false ? Visibility.Visible : Visibility.Collapsed;
            CreateAccountButton.IsDefault = exists == false;
            SwitchAuthModeButton.Visibility = _isServerConnectionVerified ? Visibility.Visible : Visibility.Collapsed;
            SwitchAuthModeLabel.SetResourceReference(TextBlock.TextProperty,
                exists == false ? "NoxText.AlreadyHaveAccount" : "NoxText.NoAccount");
            AuthHeading.SetResourceReference(TextBlock.TextProperty, !_isServerConnectionVerified
                ? "NoxText.Server" : exists == false ? "NoxText.Registration" : "NoxText.SignInAlt");
            SignInButton.IsDefault = exists != false;
            SignInButton.Content = !_isServerConnectionVerified ? Texts.T("Connect") : exists == true ? Texts.T("SignIn") : Texts.T("Continue");
            UpdateRegistrationHint();
            QueuePasswordStrength();
        }

        private void SwitchAuthMode_Click(object sender, RoutedEventArgs e)
        {
            if (!_isServerConnectionVerified || !SwitchAuthModeButton.IsEnabled) return;
            SetUsernameChoice(_usernameExists == false ? null : false);
            AuthErrorTextBlock.Text = string.Empty;
            if (string.IsNullOrWhiteSpace(UsernameTextBox.Text) || _usernameExists is null)
                UsernameTextBox.Focus();
            else PasswordBox.Focus();
        }

        private void RegistrationPassword_Changed(object sender, RoutedEventArgs e)
        {
            UpdateRegistrationHint();
            QueuePasswordStrength();
        }

        private void UpdateRegistrationHint()
        {
            if (RegistrationPasswordHint is null || CreateAccountButton is null) return;
            if (_usernameExists != false)
            {
                RegistrationPasswordHint.Visibility = Visibility.Collapsed;
                RegistrationPasswordHint.Text = string.Empty;
                CreateAccountButton.IsEnabled = false;
                return;
            }

            string missing = MissingPasswordRequirements(PasswordBox.Password);
            bool valid = missing.Length == 0;
            RegistrationPasswordHint.Text = valid ? Texts.T("PasswordMeetsTheRequirements") : Texts.T("StillNeeded", missing);
            RegistrationPasswordHint.Foreground = valid ? System.Windows.Media.Brushes.LightGreen
                : (System.Windows.Media.Brush)FindResource("TextMutedBrush");
            RegistrationPasswordHint.Visibility = Visibility.Visible;
            CreateAccountButton.IsEnabled = valid && ServerSchemeBox.IsEnabled;
        }

        private static string MissingPasswordRequirements(string password)
        {
            List<string> missing = [];
            int remaining = 12 - password.Length;
            if (remaining > 0)
                missing.Add(remaining == 1 ? Texts.T("1MoreCharacterToReach12") : Texts.T("MoreCharactersToReach12", remaining));
            if (!password.Any(char.IsAsciiLetterUpper)) missing.Add(Texts.T("AnUppercaseLetterAZ"));
            if (!password.Any(char.IsAsciiLetterLower)) missing.Add(Texts.T("ALowercaseLetterAZ"));
            if (!password.Any(char.IsAsciiDigit)) missing.Add(Texts.T("ADigit09"));
            if (!password.Any(ch => !char.IsLetterOrDigit(ch))) missing.Add(Texts.T("ASpecialCharacter"));
            return string.Join(", ", missing);
        }

        private async Task CheckServerConnectionAsync(string serverUrl)
        {
            SetLoginInputsBusy(true);
            SignInButton.Content = Texts.T("Connecting");
            try
            {
                await _apiClient.CheckHealthAsync(serverUrl);
                SaveRememberedServerIfRequested(serverUrl);
                PrepareAuthTransition();
                _isServerConnectionVerified = true;
                UsernameTextBox.IsEnabled = true;
                UsernameLabelTextBlock.Visibility = UsernameTextBox.Visibility = Visibility.Visible;
                SetUsernameChoice(null);
                AuthHeading.SetResourceReference(TextBlock.TextProperty, "NoxText.SignInAlt");
                ConnectedServerUrl.Text = serverUrl;
                ServerConnectionFields.Visibility = Visibility.Collapsed;
                ConnectedServerSummary.Visibility = Visibility.Visible;
                AuthErrorTextBlock.Text = ServerSchemeBox.SelectedIndex == 1
                    ? Texts.T("HTTPDoesNotEncryptCredentialsIn") : string.Empty;
                UsernameTextBox.Focus();
            }
            catch (Exception ex)
            {
                ResetServerConnectionState();
                AuthErrorTextBlock.Foreground = System.Windows.Media.Brushes.IndianRed;
                AuthErrorTextBlock.Text = GetUserFacingError(ex);
            }
            finally
            {
                if (!_isServerConnectionVerified)
                    SignInButton.Content = Texts.T("Connect");
                SetLoginInputsBusy(false);
            }
        }

        private void ServerScheme_Changed(object sender, SelectionChangedEventArgs e) => ResetServerConnectionState();

        private void ChangeServer_Click(object sender, RoutedEventArgs e)
        {
            ResetServerConnectionState();
            ServerUrlTextBox.Focus();
        }

        private void ServerUrlTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Accept full URLs from the clipboard and previously saved preferences.
            var field = (TextBox)sender;
            string text = field.Text.Trim();
            int prefix = text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? 8 :
                text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? 7 : 0;
            if (prefix != 0 && ServerSchemeBox != null)
            {
                ServerSchemeBox.SelectedIndex = prefix == 7 ? 1 : 0;
                field.Text = text[prefix..];
                field.CaretIndex = field.Text.Length;
            }
            ResetServerConnectionState();
        }

        private void UsernameTextBox_Changed(object sender, TextChangedEventArgs e)
        {
            if (!_isServerConnectionVerified) return;
            if (_usernameExists != false) SetUsernameChoice(null);
            else QueuePasswordStrength();
            AuthErrorTextBlock.Text = string.Empty;
        }

        private void RememberServerCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_suppressRememberServerChange)
                return;

            _appSettingsService.ClearRememberedServer();
        }

        private void SaveRememberedServerIfRequested(string serverUrl)
        {
            if (RememberServerCheckBox.IsChecked != true)
            {
                _appSettingsService.ClearRememberedServer();
                return;
            }

            ClientAppSettings settings = _appSettingsService.Load();
            settings.RememberServer = true;
            settings.RememberedServerUrl = serverUrl.Trim();
            _appSettingsService.Save(settings);
        }

        private void UseDefaultDownloadDirectoryCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressRememberServerChange)
                return;

            ClientAppSettings settings = _appSettingsService.Load();
            settings.UseDefaultDownloadDirectory = UseDefaultDownloadDirectoryCheckBox.IsChecked == true;
            settings.DefaultDownloadDirectory = GetDefaultDownloadDirectoryFromTextBox();
            _appSettingsService.Save(settings);
        }

        private void ChooseDefaultDownloadDirectoryButton_Click(object sender, RoutedEventArgs e)
        {
            var folderDialog = new OpenFolderDialog
            {
                Title = Texts.T("ChooseTheDefaultFolder")
            };

            string currentDirectory = GetDefaultDownloadDirectoryFromTextBox();

            if (Directory.Exists(currentDirectory))
                folderDialog.InitialDirectory = currentDirectory;

            if (folderDialog.ShowDialog() != true)
                return;

            SetDefaultDownloadDirectoryText(folderDialog.FolderName);
            UseDefaultDownloadDirectoryCheckBox.IsChecked = true;

            ClientAppSettings settings = _appSettingsService.Load();
            settings.UseDefaultDownloadDirectory = true;
            settings.DefaultDownloadDirectory = folderDialog.FolderName;
            _appSettingsService.Save(settings);
        }

        private void SetDefaultDownloadDirectoryText(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                DefaultDownloadDirectoryTextBox.Text = DirectoryPlaceholder;
                DefaultDownloadDirectoryTextBox.Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush");
                return;
            }

            DefaultDownloadDirectoryTextBox.Text = directory;
            DefaultDownloadDirectoryTextBox.Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
        }

        private string GetDefaultDownloadDirectoryFromTextBox()
        {
            string directory = DefaultDownloadDirectoryTextBox.Text.Trim();

            return string.Equals(directory, DirectoryPlaceholder, StringComparison.Ordinal)
                ? string.Empty
                : directory;
        }

        private void ResetServerConnectionState()
        {
            if (!_isServerConnectionVerified && UsernameTextBox?.Visibility != Visibility.Visible)
            {
                if (AuthErrorTextBlock != null) AuthErrorTextBlock.Text = string.Empty;
                return;
            }
            PrepareAuthTransition();
            _isServerConnectionVerified = false;

            if (UsernameTextBox is null ||
                PasswordBox is null ||
                UsernameLabelTextBlock is null ||
                PasswordLabelTextBlock is null ||
                CreateAccountButton is null ||
                SignInButton is null)
                return;

            UsernameTextBox.IsEnabled = false;
            AuthHeading.SetResourceReference(TextBlock.TextProperty, "NoxText.Server");
            ServerConnectionFields.Visibility = Visibility.Visible;
            ConnectedServerSummary.Visibility = Visibility.Collapsed;
            ConnectedServerUrl.Text = string.Empty;
            UsernameLabelTextBlock.Visibility = Visibility.Collapsed;
            UsernameTextBox.Visibility = Visibility.Collapsed;
            SetUsernameChoice(null);
            _apiClient.ClearAuthentication();
            AuthErrorTextBlock.Text = string.Empty;
            SignInButton.Content = Texts.T("Connect");
        }

        private async void CreateAccountButton_Click(object sender, RoutedEventArgs e)
        {
            AuthErrorTextBlock.Text = string.Empty;

            if (!TryGetServerAddress(out string serverUrl) || _usernameExists != false)
                return;
            string username = UsernameTextBox.Text.Trim();
            string password = PasswordBox.Password;

            if (!_isServerConnectionVerified)
            {
                AuthErrorTextBlock.Text = Texts.T("CheckTheServerConnectionFirst");
                return;
            }

            if (string.IsNullOrWhiteSpace(serverUrl))
            {
                AuthErrorTextBlock.Text = Texts.T("ServerAddressIsRequired");
                return;
            }

            if (string.IsNullOrWhiteSpace(username))
            {
                AuthErrorTextBlock.Text = Texts.T("EnterAUsername");
                return;
            }

            if (MissingPasswordRequirements(password).Length != 0)
            {
                UpdateRegistrationHint();
                return;
            }

            try
            {
                SetLoginInputsBusy(true);
                CreateAccountButton.Content = Texts.T("Registering");
                await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);

                var x25519Identity = _keyPairService.GenerateX25519KeyPair();
                var ed25519Identity = _keyPairService.GenerateEd25519KeyPair();

                byte[] payloadSalt = _argonService.GeneratePayloadSalt();

                var payload = new UserKeyPayload
                {
                    Version = 1,
                    X25519IdentityPublicKey = x25519Identity.PublicKey,
                    X25519IdentityPrivateKey = x25519Identity.PrivateKey,
                    Ed25519IdentityPublicKey = ed25519Identity.PublicKey,
                    Ed25519IdentityPrivateKey = ed25519Identity.PrivateKey,
                    NextOneTimePreKeyId = 1
                };

                byte[] encryptedKeyPayload = _vaultService.EncryptVault(
                    password,
                    payloadSalt,
                    payload);

                var request = new RegisterRequest
                {
                    Username = username,
                    Password = password,
                    PayloadSalt = payloadSalt,
                    EncryptedKeyPayload = encryptedKeyPayload,
                    X25519PublicKey = x25519Identity.PublicKey,
                    Ed25519PublicKey = ed25519Identity.PublicKey
                };

                await _apiClient.RegisterAsync(serverUrl, request);

                AuthErrorTextBlock.Foreground = System.Windows.Media.Brushes.LightGreen;
                SetUsernameChoice(null);
                AuthErrorTextBlock.Text = Texts.T("AccountCreatedSelectContinueToSign");
                UsernameTextBox.Focus();
            }
            catch (Exception ex)
            {
                AuthErrorTextBlock.Foreground = System.Windows.Media.Brushes.IndianRed;
                AuthErrorTextBlock.Text = GetUserFacingError(ex);
            }
            finally
            {
                SetLoginInputsBusy(false);
                CreateAccountButton.Content = Texts.T("Register");
            }
        }

        private void RecipientUsername_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            e.Handled = true;
            if (!e.IsRepeat && !_recipientLocked && CheckRecipientButton.IsEnabled && !TrustedRecipientsComboBox.IsDropDownOpen)
                CheckRecipientButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        private async void CheckRecipientButton_Click(object sender, RoutedEventArgs e)
        {
            // After a successful check the button becomes "edit": it only unlocks the recipient field.
            if (_recipientLocked)
            {
                SetRecipientLocked(false);
                RecipientUsernameTextBox.Focus();
                RecipientUsernameTextBox.SelectAll();
                return;
            }

            int checkVersion = ++_recipientCheckVersion;
            _currentRecipientBundle = null;
            _recipientBundleMustBeRefreshed = false;
            RecipientStatusTextBlock.Text = string.Empty;

            DropZoneBorder.IsEnabled = false;
            SetSendButtonEnabled(false);
            ResetProgress(SendProgressBar);
            SelectedFilesListBox.Items.Clear();
            SelectedFilesItemsControl.Items.Clear();
            DropZonePromptTextBlock.Text = DropZoneDisabledText;
            SelectedFilesStatusTextBlock.Text = string.Empty;
            _selectedFilePaths.Clear();

            if (_session is null || _vault is null)
            {
                RecipientStatusTextBlock.Text = Texts.T("SignInBeforeContactingARecipient");
                return;
            }

            string recipientUsername = GetRecipientUsername();

            if (string.IsNullOrWhiteSpace(recipientUsername))
            {
                RecipientStatusTextBlock.Text = Texts.T("EnterTheRecipientSUsername");
                return;
            }

            try
            {
                ApplySendToSelfModeIfRecipientIsCurrentUser(recipientUsername);
                recipientUsername = GetRecipientUsername();

                CheckRecipientButton.IsEnabled = false;
                CheckRecipientButton.Content = "...";

                PreKeyBundleResponse bundle = await _apiClient.GetPreKeyBundleAsync(recipientUsername);
                if (checkVersion != _recipientCheckVersion ||
                    !string.Equals(GetRecipientUsername(), recipientUsername, StringComparison.OrdinalIgnoreCase))
                    return;

                byte[] fingerprint = _fingerprintService.ComputeFingerprint(
                    bundle.Username,
                    bundle.X25519IdentityPublicKey,
                    bundle.Ed25519IdentityPublicKey);

                string formattedFingerprint = _fingerprintService.FormatFingerprint(fingerprint, bytesToShow: fingerprint.Length);

                bool hasOneTimePreKey =
                    bundle.OneTimePreKeyId is not null &&
                    bundle.OneTimePreKeyPublicKey is not null;

                bool wasAlreadyTrusted = IsRecipientAlreadyTrusted(bundle);

                bool trusted = EnsureRecipientTrusted(
                    bundle,
                    formattedFingerprint);


                if (!trusted)
                {
                    RecipientStatusTextBlock.Foreground = System.Windows.Media.Brushes.IndianRed;
                    RecipientStatusTextBlock.Text = Texts.T("RecipientNotTrustedTransferBlocked");
                    _currentRecipientBundle = null;
                    _recipientBundleMustBeRefreshed = false;
                    return;
                }

                RecipientStatusTextBlock.Foreground = System.Windows.Media.Brushes.LightGreen;

                if (IsCurrentUserBundle(bundle))
                {
                    RecipientStatusTextBlock.Text = Texts.T("YourAccountIsReadyToReceive");
                }
                else if (wasAlreadyTrusted)
                {
                    RecipientStatusTextBlock.Text = hasOneTimePreKey
                        ? Texts.T("UserIsAvailable", bundle.Username)
                        : Texts.T("UserIsReadyButNoOne", bundle.Username);
                }
                else
                {
                    RecipientStatusTextBlock.Text = hasOneTimePreKey
                        ? Texts.T("UserVerifiedAndAddedToYour", bundle.Username)
                        : Texts.T("UserVerifiedButNoOneTime", bundle.Username);
                }

                if (_sessionPassword is null)
                    throw new InvalidOperationException(Texts.T("TheSessionPasswordIsUnavailable"));

                await SaveVaultAsync(_sessionPassword);

                if (checkVersion != _recipientCheckVersion ||
                    !string.Equals(GetRecipientUsername(), bundle.Username, StringComparison.OrdinalIgnoreCase))
                    return;
                _currentRecipientBundle = bundle;
                DropZoneBorder.IsEnabled = true;
                DropZonePromptTextBlock.Text = DropZoneReadyText;
                if (!IsSendToSelfEnabled()) SetRecipientLocked(true);
            }
            catch (Exception ex)
            {
                RecipientStatusTextBlock.Foreground = System.Windows.Media.Brushes.IndianRed;
                RecipientStatusTextBlock.Text = GetUserFacingError(ex);
            }
            finally
            {
                CheckRecipientButton.IsEnabled = true;
                CheckRecipientButton.Content = Texts.T("Check");
            }
        }


        private void SetRecipientLocked(bool locked)
        {
            _recipientLocked = locked;
            RecipientUsernameTextBox.IsReadOnly = locked;
            TrustedRecipientsComboBox.IsEnabled = !locked && !IsSendToSelfEnabled();
        }

        private bool EnsureRecipientTrusted(
            PreKeyBundleResponse bundle,
            string formattedFingerprint)
        {
            if (_vault is null)
                throw new InvalidOperationException(Texts.T("TheCryptographicVaultHasNotBeen"));

            if (IsSendToSelfEnabled() &&
                _session is not null &&
                string.Equals(bundle.Username, _session.Username, StringComparison.OrdinalIgnoreCase))
            {
                TrustedContact? selfContact = _vault.TrustedContacts
                    .SingleOrDefault(contact =>
                        string.Equals(contact.Username, bundle.Username, StringComparison.OrdinalIgnoreCase));

                if (selfContact is null)
                {
                    _vault.TrustedContacts.Add(new TrustedContact
                    {
                        Username = bundle.Username,
                        X25519IdentityPublicKey = bundle.X25519IdentityPublicKey,
                        Ed25519IdentityPublicKey = bundle.Ed25519IdentityPublicKey,
                        Fingerprint = formattedFingerprint,
                        TrustedAtUtc = DateTime.UtcNow
                    });

                    RefreshTrustedRecipientsComboBox();
                    RefreshTrustedContactsList();
                }

                return true;
            }

            TrustedContact? existingContact = _vault.TrustedContacts
                .SingleOrDefault(contact =>
                    string.Equals(contact.Username, bundle.Username, StringComparison.OrdinalIgnoreCase));

            if (existingContact is null)
            {
                bool trusted = ShowFingerprintTrustDialog(
                    windowTitle: Texts.T("TrustOnFirstUse"),
                    heading: Texts.T("VerifyRecipient"),
                    message: Texts.T("ThisIsTheFirstTimeYou", bundle.Username),
                    fingerprintLabel: Texts.T("RecipientFingerprint"),
                    formattedFingerprint: formattedFingerprint,
                    verificationText: Texts.T("CompareTheFingerprintWithTheRecipient"),
                    isWarning: false);

                if (!trusted)
                    return false;

                _vault.TrustedContacts.Add(new TrustedContact
                {
                    Username = bundle.Username,
                    X25519IdentityPublicKey = bundle.X25519IdentityPublicKey,
                    Ed25519IdentityPublicKey = bundle.Ed25519IdentityPublicKey,
                    Fingerprint = formattedFingerprint,
                    TrustedAtUtc = DateTime.UtcNow
                });

                RefreshTrustedRecipientsComboBox();
                RefreshTrustedContactsList();

                return true;
            }

            bool x25519Matches = existingContact.X25519IdentityPublicKey.SequenceEqual(
                bundle.X25519IdentityPublicKey);

            bool ed25519Matches = existingContact.Ed25519IdentityPublicKey.SequenceEqual(
                bundle.Ed25519IdentityPublicKey);

            if (!x25519Matches || !ed25519Matches)
            {
                bool trusted = ShowFingerprintTrustDialog(
                    windowTitle: Texts.T("TransferBlocked"),
                    heading: Texts.T("TheTransferHasBeenBlocked"),
                    message: Texts.T("RecipientKeysChanged", bundle.Username),
                    fingerprintLabel: Texts.T("NewFingerprintDetected"),
                    formattedFingerprint: formattedFingerprint,
                    verificationText: Texts.T("VerifyTheNewFingerprintWithThe"),
                    isWarning: true);

                if (!trusted)
                    return false;

                existingContact.X25519IdentityPublicKey = bundle.X25519IdentityPublicKey;
                existingContact.Ed25519IdentityPublicKey = bundle.Ed25519IdentityPublicKey;
                existingContact.Fingerprint = formattedFingerprint;
                existingContact.TrustedAtUtc = DateTime.UtcNow;

                RefreshTrustedRecipientsComboBox();
                RefreshTrustedContactsList();

                return true;
            }

            return true;
        }

        private const int FingerprintGroupsPerRow = 4;

        private static string FormatFingerprintRows(string fingerprint) =>
            string.Join(Environment.NewLine, fingerprint.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Chunk(FingerprintGroupsPerRow).Select(row => string.Join("  ", row)));

        private bool ShowFingerprintTrustDialog(
            string windowTitle,
            string heading,
            string message,
            string fingerprintLabel,
            string formattedFingerprint,
            string verificationText,
            bool isWarning)
        {
            string expectedSuffix = formattedFingerprint.Replace(" ", string.Empty)[^4..];
            string maskedFingerprint = formattedFingerprint[..^4] + "****";
            string fingerprintRows = FormatFingerprintRows(maskedFingerprint);

            var dialog = new Window
            {
                Title = windowTitle,
                Owner = this,
                Width = 384,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = (System.Windows.Media.Brush)FindResource("AppBackgroundBrush"),
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush")
            };
            dialog.SourceInitialized += (_, _) => ApplyNativeTitleBarColors(dialog);

            var root = new Grid
            {
                Margin = new Thickness(16)
            };

            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = new TextBlock
            {
                Text = heading,
                FontSize = 21,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.SemiBold,
                Foreground = isWarning
                    ? System.Windows.Media.Brushes.IndianRed
                    : (System.Windows.Media.Brush)FindResource("AccentBrush")
            };

            var warning = new TextBlock
            {
                Text = message,
                Margin = new Thickness(0, 8, 0, 12),
                FontSize = 15,
                LineHeight = 21,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush")
            };

            var fingerprintLabelTextBlock = new TextBlock
            {
                Text = fingerprintLabel,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush")
            };

            var fingerprintBox = new TextBox
            {
                Text = fingerprintRows,
                Height = 102,
                Margin = new Thickness(0, 5, 0, 10),
                Padding = new Thickness(12, 6, 12, 6),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 17,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalContentAlignment = VerticalAlignment.Top,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(17, 24, 39)),
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush")
            };

            var verification = new TextBlock
            {
                Text = verificationText,
                FontSize = 15,
                LineHeight = 21,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush")
            };

            var verificationPanel = new StackPanel();
            verificationPanel.Children.Add(verification);
            verificationPanel.Children.Add(new TextBlock
            {
                Text = Texts.T("EnterTheLast4CharactersProvided"),
                Margin = new Thickness(0, 8, 0, 4),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap
            });

            var suffixInput = new TextBox
            {
                MaxLength = 4,
                Width = 88,
                Height = 42,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(10, 4, 10, 4),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 20,
                Background = fingerprintBox.Background,
                Foreground = fingerprintBox.Foreground,
                BorderBrush = fingerprintBox.BorderBrush
            };
            var actions = new DockPanel();
            verificationPanel.Children.Add(actions);

            var verificationError = new TextBlock
            {
                Text = Texts.T("TheCharactersDoNotMatchCheck"),
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 8, 0, 0),
                FontSize = 15,
                TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.IndianRed
            };
            verificationPanel.Children.Add(verificationError);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };

            var rejectButton = new Button
            {
                Content = Texts.T("Cancel"),
                Width = 82,
                Height = 40,
                FontSize = 15,
                IsCancel = true,
                Background = System.Windows.Media.Brushes.Transparent,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush")
            };

            var trustButton = new Button
            {
                Content = Texts.T("Check"),
                IsDefault = true,
                Width = 78,
                Height = 40,
                Margin = new Thickness(6, 0, 0, 0),
                FontSize = 15,
                Background = (System.Windows.Media.Brush)FindResource("PrimaryBrush"),
                Foreground = System.Windows.Media.Brushes.White,
                BorderBrush = (System.Windows.Media.Brush)FindResource("PrimaryBrush")
            };

            rejectButton.Click += (_, _) => dialog.DialogResult = false;
            suffixInput.TextChanged += (_, _) => verificationError.Visibility = Visibility.Collapsed;
            trustButton.Click += (_, _) =>
            {
                if (!string.Equals(suffixInput.Text.Trim(), expectedSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    verificationError.Visibility = Visibility.Visible;
                    suffixInput.Focus();
                    suffixInput.SelectAll();
                    return;
                }

                dialog.DialogResult = true;
            };

            buttons.Children.Add(rejectButton);
            buttons.Children.Add(trustButton);
            DockPanel.SetDock(buttons, Dock.Right);
            actions.Children.Add(buttons);
            actions.Children.Add(suffixInput);

            Grid.SetRow(title, 0);
            Grid.SetRow(warning, 1);
            Grid.SetRow(fingerprintLabelTextBlock, 2);
            Grid.SetRow(fingerprintBox, 3);
            Grid.SetRow(verificationPanel, 4);

            root.Children.Add(title);
            root.Children.Add(warning);
            root.Children.Add(fingerprintLabelTextBlock);
            root.Children.Add(fingerprintBox);
            root.Children.Add(verificationPanel);

            Views.WindowLayout.FitDialog(dialog, root);
            dialog.Loaded += (_, _) => suffixInput.Focus();

            return dialog.ShowDialog() == true;
        }

        private static bool ServerIdentityKeysDifferFromVault(
            ClientSession session,
            UserKeyPayload vault)
        {
            return !session.X25519PublicKey.SequenceEqual(vault.X25519IdentityPublicKey) ||
                   !session.Ed25519PublicKey.SequenceEqual(vault.Ed25519IdentityPublicKey);
        }

        private OwnIdentityRecoveryAction ShowOwnIdentityMismatchWarning(
            ClientSession session,
            UserKeyPayload vault)
        {
            string serverFingerprint = _fingerprintService.FormatFingerprint(
                _fingerprintService.ComputeFingerprint(
                    session.Username,
                    session.X25519PublicKey,
                    session.Ed25519PublicKey), bytesToShow: 32);

            string vaultFingerprint = _fingerprintService.FormatFingerprint(
                _fingerprintService.ComputeFingerprint(
                    session.Username,
                    vault.X25519IdentityPublicKey,
                    vault.Ed25519IdentityPublicKey), bytesToShow: 32);

            OwnIdentityRecoveryAction selectedAction = OwnIdentityRecoveryAction.Cancel;

            var dialog = new Window
            {
                Title = Texts.T("PossibleMITMAttack"),
                Owner = this,
                Width = 790,
                Height = 620,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = (System.Windows.Media.Brush)FindResource("AppBackgroundBrush"),
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush")
            };
            dialog.SourceInitialized += (_, _) => ApplyNativeTitleBarColors(dialog);

            var root = new Grid
            {
                Margin = new Thickness(36)
            };

            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = new TextBlock
            {
                Text = Texts.T("YourPublicKeysHaveChanged"),
                FontSize = 27,
                FontWeight = FontWeights.SemiBold,
                Foreground = System.Windows.Media.Brushes.IndianRed
            };

            var message = new TextBlock
            {
                Text = Texts.T("OwnIdentityMismatch"),
                Margin = new Thickness(0, 20, 0, 0),
                FontSize = 18,
                LineHeight = 27,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush")
            };

            var action = new TextBlock
            {
                Text = Texts.T("RecoveryExplanation"),
                Margin = new Thickness(0, 18, 0, 0),
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.IndianRed
            };

            var fingerprints = new TextBlock
            {
                Text = Texts.T("RecoveryFingerprints", vaultFingerprint, serverFingerprint),
                Margin = new Thickness(0, 20, 0, 0),
                Padding = new Thickness(14),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 14,
                LineHeight = 21,
                TextWrapping = TextWrapping.Wrap,
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(18, 23, 34)),
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush")
            };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0)
            };

            var cancelButton = new Button
            {
                Content = Texts.T("CancelSignIn"),
                Width = 150,
                Height = 44,
                FontSize = 16,
                IsCancel = true,
                Background = System.Windows.Media.Brushes.Transparent,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush")
            };

            var createIdentityButton = new Button
            {
                Content = Texts.T("NewIdentity"),
                Width = 150,
                Height = 44,
                Margin = new Thickness(12, 0, 0, 0),
                FontSize = 16,
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(185, 56, 56)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(185, 56, 56))
            };

            var restoreButton = new Button
            {
                Content = Texts.T("RestoreFromVault"),
                Width = 180,
                Height = 44,
                Margin = new Thickness(12, 0, 0, 0),
                FontSize = 16,
                IsDefault = true,
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(34, 197, 94)),
                Foreground = System.Windows.Media.Brushes.White,
                BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(34, 197, 94))
            };

            cancelButton.Click += (_, _) => dialog.Close();

            restoreButton.Click += (_, _) =>
            {
                selectedAction = OwnIdentityRecoveryAction.RestoreVaultIdentity;
                dialog.DialogResult = true;
            };

            createIdentityButton.Click += (_, _) =>
            {
                bool confirmed = ConfirmAction(dialog,
                    Texts.T("ConfirmIdentityReplacement"),
                    Texts.T("CreateANewCryptographicIdentity"),
                    Texts.T("NewIdentity"));

                if (!confirmed)
                    return;

                selectedAction = OwnIdentityRecoveryAction.CreateNewIdentity;
                dialog.DialogResult = true;
            };

            buttons.Children.Add(cancelButton);
            buttons.Children.Add(createIdentityButton);
            buttons.Children.Add(restoreButton);

            Grid.SetRow(title, 0);
            Grid.SetRow(message, 1);
            Grid.SetRow(action, 2);
            Grid.SetRow(fingerprints, 3);
            Grid.SetRow(buttons, 5);

            root.Children.Add(title);
            root.Children.Add(message);
            root.Children.Add(action);
            root.Children.Add(fingerprints);
            root.Children.Add(buttons);

            Views.WindowLayout.FitDialog(dialog, root);
            dialog.ShowDialog();

            return selectedAction;
        }

        private async Task RecoverOwnIdentityAsync(
            OwnIdentityRecoveryAction recoveryAction,
            string password,
            bool showConfirmation = true)
        {
            if (_session is null || _vault is null)
                throw new InvalidOperationException(Texts.T("TheSessionOrVaultIsUnavailable"));

            bool createNewIdentity = recoveryAction == OwnIdentityRecoveryAction.CreateNewIdentity;

            if (createNewIdentity)
            {
                DestroyPrivateIdentityMaterial(_vault);

                var x25519Identity = _keyPairService.GenerateX25519KeyPair();
                var ed25519Identity = _keyPairService.GenerateEd25519KeyPair();

                _vault.X25519IdentityPublicKey = x25519Identity.PublicKey;
                _vault.X25519IdentityPrivateKey = x25519Identity.PrivateKey;
                _vault.Ed25519IdentityPublicKey = ed25519Identity.PublicKey;
                _vault.Ed25519IdentityPrivateKey = ed25519Identity.PrivateKey;
            }

            _preKeyBootstrapService.PrepareFreshRecoveryBundle(
                _vault,
                preservePreviousDecryptionKeys: !createNewIdentity);

            ClientSignedPreKeyState signedPreKey = _vault.SignedPreKey
                ?? throw new InvalidOperationException(Texts.T("CouldNotGenerateASignedPre"));

            List<ClientOneTimePreKeyState> activeOneTimePreKeys = _vault.OneTimePreKeys
                .Where(preKey => !preKey.IsUsed)
                .ToList();

            byte[] encryptedKeyPayload = _vaultService.EncryptVault(
                password,
                _session.PayloadSalt,
                _vault);

            var request = new RecoverIdentityBundleRequest
            {
                Password = password,
                EncryptedKeyPayload = encryptedKeyPayload,
                X25519IdentityPublicKey = _vault.X25519IdentityPublicKey,
                Ed25519IdentityPublicKey = _vault.Ed25519IdentityPublicKey,
                SignedPreKeyId = signedPreKey.KeyId,
                SignedPreKeyPublicKey = signedPreKey.PublicKey,
                SignedPreKeySignature = signedPreKey.Signature,
                OneTimePreKeys = activeOneTimePreKeys
                    .Select(preKey => new OneTimePreKeyDto
                    {
                        KeyId = preKey.KeyId,
                        PublicKey = preKey.PublicKey
                    })
                    .ToList()
            };

            await _apiClient.RecoverIdentityBundleAsync(request);

            _session.X25519PublicKey = _vault.X25519IdentityPublicKey;
            _session.Ed25519PublicKey = _vault.Ed25519IdentityPublicKey;
            _session.EncryptedKeyPayload = encryptedKeyPayload;

            string operation = createNewIdentity
                ? Texts.T("ANewCryptographicIdentityHasBeen")
                : Texts.T("TheVaultIdentityHasBeenRepublished");

            if (showConfirmation)
            {
                MessageBox.Show(
                    this,
                    operation + Texts.T("TheServerNowHasANew"),
                    Texts.T("IdentityRestored"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        private static void DestroyPrivateIdentityMaterial(UserKeyPayload vault)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(
                vault.X25519IdentityPrivateKey);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(
                vault.Ed25519IdentityPrivateKey);

            if (vault.SignedPreKey is not null)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(
                    vault.SignedPreKey.PrivateKey);
            }

            foreach (ClientSignedPreKeyState signedPreKey in vault.PreviousSignedPreKeys)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(
                    signedPreKey.PrivateKey);
            }

            foreach (ClientOneTimePreKeyState oneTimePreKey in vault.OneTimePreKeys)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(
                    oneTimePreKey.PrivateKey);
            }
        }

        private void ShowDataTamperingWarning(bool removedFromServer)
        {
            var dialog = new Window
            {
                Title = Texts.T("FileIntegrityCompromised"),
                Owner = this,
                Width = 660,
                Height = 390,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = (System.Windows.Media.Brush)FindResource("AppBackgroundBrush"),
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush")
            };
            dialog.SourceInitialized += (_, _) => ApplyNativeTitleBarColors(dialog);

            var root = new Grid
            {
                Margin = new Thickness(36)
            };

            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = new TextBlock
            {
                Text = Texts.T("PossibleTamperingDetected"),
                FontSize = 27,
                FontWeight = FontWeights.SemiBold,
                Foreground = System.Windows.Media.Brushes.IndianRed
            };

            var message = new TextBlock
            {
                Text = Texts.T("AuthenticationFailed"),
                Margin = new Thickness(0, 20, 0, 0),
                FontSize = 18,
                LineHeight = 27,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush")
            };

            var action = new TextBlock
            {
                Text = removedFromServer
                    ? Texts.T("TamperedRemoved")
                    : Texts.T("TamperedNotConfirmed"),
                Margin = new Thickness(0, 18, 0, 0),
                FontSize = 19,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.IndianRed
            };

            var closeButton = new Button
            {
                Content = Texts.T("Close"),
                Width = 140,
                Height = 44,
                FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Right,
                IsDefault = true,
                IsCancel = true,
                Background = (System.Windows.Media.Brush)FindResource("PrimaryBrush"),
                Foreground = System.Windows.Media.Brushes.White,
                BorderBrush = (System.Windows.Media.Brush)FindResource("PrimaryBrush")
            };

            closeButton.Click += (_, _) => dialog.Close();

            Grid.SetRow(title, 0);
            Grid.SetRow(message, 1);
            Grid.SetRow(action, 2);
            Grid.SetRow(closeButton, 3);

            root.Children.Add(title);
            root.Children.Add(message);
            root.Children.Add(action);
            root.Children.Add(closeButton);

            Views.WindowLayout.FitDialog(dialog, root);
            dialog.ShowDialog();
        }

        private void SelectFilesButton_Click(object sender, RoutedEventArgs e)
        {
            SelectFilesFromDialog();
        }

        private void DropZoneBorder_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!DropZoneBorder.IsEnabled)
                return;

            for (var node = e.OriginalSource as System.Windows.Media.Visual; node != null && node != DropZoneBorder;
                 node = System.Windows.Media.VisualTreeHelper.GetParent(node) as System.Windows.Media.Visual)
                if (node is System.Windows.Controls.Primitives.ScrollBar)
                    return;

            SelectFilesFromDialog();
        }

        private void DropZoneBorder_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (!e.IsRepeat && e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space)
            {
                e.Handled = true;
                SelectFilesFromDialog();
            }
        }

        private void DropZoneBorder_DragEnter(object sender, DragEventArgs e)
        {
            if (!DropZoneBorder.IsEnabled)
                return;

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                DropZoneBorder.Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(21, 55, 43));
            }
        }

        private void DropZoneBorder_DragLeave(object sender, DragEventArgs e)
        {
            ResetDropZoneBackground();
        }

        private void DropZoneBorder_Drop(object sender, DragEventArgs e)
        {
            ResetDropZoneBackground();

            if (!DropZoneBorder.IsEnabled)
                return;

            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                return;

            string[] filePaths = (string[])e.Data.GetData(DataFormats.FileDrop);

            SetSelectedFiles(filePaths);
        }

        private void SelectFilesFromDialog()
        {
            var dialog = new OpenFileDialog
            {
                Title = Texts.T("SelectFilesToSendUpTo", MaximumFilesPerTransfer),
                Multiselect = true,
                CheckFileExists = true
            };

            bool? result = dialog.ShowDialog();

            if (result != true)
                return;

            SetSelectedFiles(dialog.FileNames);
        }

        private void FileStrip_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (sender is ScrollViewer strip && strip.ScrollableWidth > 0)
            {
                strip.ScrollToHorizontalOffset(strip.HorizontalOffset - e.Delta);
                e.Handled = true;
            }
        }

        private void SetSelectedFiles(IEnumerable<string> filePaths)
        {
            _selectedFilePaths.Clear();
            SelectedFilesListBox.Items.Clear();
            SelectedFilesItemsControl.Items.Clear();

            foreach (string filePath in filePaths.Where(File.Exists))
            {
                _selectedFilePaths.Add(filePath);

                var fileInfo = new FileInfo(filePath);
                var preview = new Views.SelectedFilePreview(fileInfo.Name, FormatFileSize(fileInfo.Length));
                SelectedFilesListBox.Items.Add($"{preview.Name} - {preview.Size}");
                SelectedFilesItemsControl.Items.Add(preview);
            }

            bool exceedsLimit = _selectedFilePaths.Count > MaximumFilesPerTransfer;
            SelectedFilesStatusTextBlock.Foreground = exceedsLimit
                ? System.Windows.Media.Brushes.IndianRed
                : System.Windows.Media.Brushes.LightGreen;
            SelectedFilesStatusTextBlock.Text = exceedsLimit
                ? Texts.T("YouSelectedFilesTheLimitIs", _selectedFilePaths.Count, MaximumFilesPerTransfer)
                : Texts.T("FilesSelected", _selectedFilePaths.Count, MaximumFilesPerTransfer);
            DropZonePromptTextBlock.Text = _selectedFilePaths.Count == 0
                ? DropZoneReadyText
                : DropZoneSelectedFilesText;
            SetSendButtonEnabled(_selectedFilePaths.Count > 0 && !exceedsLimit);
            ResetProgress(SendProgressBar);
        }

        private void SetSendButtonEnabled(bool isEnabled)
        {
            isEnabled = isEnabled && _selectedFilePaths.Count is > 0 and <= MaximumFilesPerTransfer
                && IsCurrentRecipientTrustedForSend();
            SendEncryptedButton.IsEnabled = isEnabled;
            ApplyActionButtonVisual(SendEncryptedButton, isEnabled);
        }

        private void SetDownloadButtonEnabled(bool isEnabled)
        {
            isEnabled &= !_isDownloading;
            DownloadDecryptButton.IsEnabled = isEnabled;
            ApplyActionButtonVisual(DownloadDecryptButton, isEnabled);
        }

        private void ApplyActionButtonVisual(Button button, bool isEnabled)
        {
            if (isEnabled)
            {
                var greenBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(64, 217, 119));

                button.Background = greenBrush;
                button.BorderBrush = greenBrush;
                button.Foreground = System.Windows.Media.Brushes.Black;
                return;
            }

            button.Background = System.Windows.Media.Brushes.Transparent;
            button.BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush");
            button.Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush");
        }

        private void ResetDropZoneBackground()
        {
            DropZoneBorder.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(17, 24, 39));
        }

        private void RefreshTrustedRecipientsComboBox()
        {
            _isRefreshingTrustedRecipients = true;
            TrustedRecipientsComboBox.Items.Clear();

            if (_vault is null)
            {
                _isRefreshingTrustedRecipients = false;
                return;
            }

            foreach (TrustedContact contact in GetVisibleTrustedContacts())
            {
                TrustedRecipientsComboBox.Items.Add(contact.Username);
            }

            TrustedRecipientsComboBox.SelectedIndex = -1;
            _isRefreshingTrustedRecipients = false;
        }

        private void TrustedRecipientsComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isRefreshingTrustedRecipients)
                return;

            if (TrustedRecipientsComboBox.SelectedItem is not string selectedUsername)
                return;

            TrustedRecipientsComboBox.SelectedIndex = -1;

            CheckBox? sendToSelfCheckBox = SendToSelfCheckBoxControl;
            if (sendToSelfCheckBox is not null)
                sendToSelfCheckBox.IsChecked = false;

            ClearRecipientPlaceholder();
            RecipientUsernameTextBox.Text = selectedUsername;
            CheckRecipientButton_Click(sender, new RoutedEventArgs());
        }

        private void RecipientUsernameTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_isApplyingSendToSelfMode)
                return;

            if (IsSendToSelfEnabled())
                return;

            if (IsRecipientPlaceholderActive())
                return;

            string typedRecipientUsername = RecipientUsernameTextBox.Text.Trim();

            if (_currentRecipientBundle is null)
                return;

            if (string.Equals(typedRecipientUsername, _currentRecipientBundle.Username, StringComparison.OrdinalIgnoreCase))
                return;

            _currentRecipientBundle = null;
            _recipientBundleMustBeRefreshed = false;
            DropZoneBorder.IsEnabled = false;
            DropZonePromptTextBlock.Text = DropZoneDisabledText;
            SetSendButtonEnabled(false);
            RecipientStatusTextBlock.Foreground = System.Windows.Media.Brushes.IndianRed;
            RecipientStatusTextBlock.Text = Texts.T("RecipientChangedCheckTheRecipientAgain");
        }

        private void RecipientUsernameTextBox_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (IsSendToSelfEnabled())
                return;

            ClearRecipientPlaceholder();
        }

        private void RecipientUsernameTextBox_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
        {
            if (IsSendToSelfEnabled())
                return;

            ClearRecipientPlaceholder();
        }

        private void RecipientUsernameTextBox_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
        {
            if (IsSendToSelfEnabled())
                return;

            ApplyRecipientPlaceholderIfEmpty();
        }

        private string GetRecipientUsername()
        {
            if (IsSendToSelfEnabled())
                return _session?.Username ?? string.Empty;

            if (IsRecipientPlaceholderActive())
                return string.Empty;

            return RecipientUsernameTextBox.Text.Trim();
        }

        private bool IsRecipientPlaceholderActive()
        {
            return string.Equals(RecipientUsernameTextBox.Text, RecipientPlaceholderText, StringComparison.Ordinal);
        }

        private void ClearRecipientPlaceholder()
        {
            if (!IsRecipientPlaceholderActive())
                return;

            RecipientUsernameTextBox.Text = string.Empty;
            RecipientUsernameTextBox.Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
        }

        private void ApplyRecipientPlaceholderIfEmpty()
        {
            if (!string.IsNullOrWhiteSpace(RecipientUsernameTextBox.Text))
                return;

            RecipientUsernameTextBox.Text = RecipientPlaceholderText;
            RecipientUsernameTextBox.Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush");
        }

        private async void SendToSelfCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            bool sendToSelf = IsSendToSelfEnabled();
            SetRecipientLocked(false);

            try
            {
                _isApplyingSendToSelfMode = true;

                RecipientUsernameTextBox.IsEnabled = !sendToSelf;
                TrustedRecipientsComboBox.IsEnabled = !sendToSelf;
                _currentRecipientBundle = null;
                _recipientBundleMustBeRefreshed = false;
                DropZoneBorder.IsEnabled = false;
                DropZonePromptTextBlock.Text = DropZoneDisabledText;
                SetSendButtonEnabled(false);
                RecipientStatusTextBlock.Text = string.Empty;

                if (!sendToSelf)
                {
                    RecipientUsernameTextBox.Text = string.Empty;
                    RecipientUsernameTextBox.Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
                    ApplyRecipientPlaceholderIfEmpty();
                    return;
                }

                RecipientUsernameTextBox.Text = SendToSelfDisplayText;
                RecipientUsernameTextBox.Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
                SelectedFilesListBox.Items.Clear();
                SelectedFilesItemsControl.Items.Clear();
                SelectedFilesStatusTextBlock.Text = string.Empty;
                _selectedFilePaths.Clear();
            }
            finally
            {
                _isApplyingSendToSelfMode = false;
            }

            if (_session is null)
            {
                RecipientStatusTextBlock.Foreground = System.Windows.Media.Brushes.IndianRed;
                RecipientStatusTextBlock.Text = Texts.T("SignInBeforeSendingFilesTo");
                return;
            }

            if (!_suppressSendToSelfAutoCheck && sendToSelf)
                await Dispatcher.InvokeAsync(() => CheckRecipientButton_Click(sender, new RoutedEventArgs()));
        }

        private bool IsSendToSelfEnabled()
        {
            return SendToSelfCheckBoxControl?.IsChecked == true;
        }

        private bool IsCurrentRecipientStillSelected()
        {
            if (_currentRecipientBundle is null)
                return false;

            return string.Equals(
                GetRecipientUsername(),
                _currentRecipientBundle.Username,
                StringComparison.OrdinalIgnoreCase);
        }

        private bool IsCurrentRecipientTrustedForSend() =>
            _currentRecipientBundle is { } bundle && IsCurrentRecipientStillSelected() &&
            (IsCurrentUserBundle(bundle) || IsRecipientAlreadyTrusted(bundle));

        private void ApplySendToSelfModeIfRecipientIsCurrentUser(string recipientUsername)
        {
            if (_session is null ||
                IsSendToSelfEnabled() ||
                !string.Equals(recipientUsername, _session.Username, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            CheckBox? sendToSelfCheckBox = SendToSelfCheckBoxControl;
            if (sendToSelfCheckBox is not null)
            {
                try
                {
                    _suppressSendToSelfAutoCheck = true;
                    sendToSelfCheckBox.IsChecked = true;
                }
                finally
                {
                    _suppressSendToSelfAutoCheck = false;
                }
            }
        }

        private bool IsCurrentUserBundle(PreKeyBundleResponse bundle)
        {
            return _session is not null &&
                string.Equals(bundle.Username, _session.Username, StringComparison.OrdinalIgnoreCase);
        }

        private bool IsRecipientAlreadyTrusted(PreKeyBundleResponse bundle)
        {
            if (_vault is null)
                return false;

            TrustedContact? existingContact = _vault.TrustedContacts
                .SingleOrDefault(contact =>
                    string.Equals(contact.Username, bundle.Username, StringComparison.OrdinalIgnoreCase));

            if (existingContact is null)
                return false;

            return existingContact.X25519IdentityPublicKey.SequenceEqual(bundle.X25519IdentityPublicKey)
                && existingContact.Ed25519IdentityPublicKey.SequenceEqual(bundle.Ed25519IdentityPublicKey);
        }

        private async Task RefreshRecipientBundleIfNeededAsync()
        {
            if (!_recipientBundleMustBeRefreshed || _currentRecipientBundle is null)
                return;

            if (_vault is null)
                throw new InvalidOperationException(Texts.T("TheCryptographicVaultHasNotBeen"));

            var previousBundle = _currentRecipientBundle;
            PreKeyBundleResponse freshBundle = await _apiClient.GetPreKeyBundleAsync(previousBundle.Username);
            if (!ReferenceEquals(previousBundle, _currentRecipientBundle) || !IsCurrentRecipientTrustedForSend())
                throw new InvalidOperationException(Texts.T("CheckAndVerifyTheRecipientFirst"));

            byte[] fingerprint = _fingerprintService.ComputeFingerprint(
                freshBundle.Username,
                freshBundle.X25519IdentityPublicKey,
                freshBundle.Ed25519IdentityPublicKey);

            string formattedFingerprint = _fingerprintService.FormatFingerprint(fingerprint, bytesToShow: fingerprint.Length);

            if (IsCurrentUserBundle(freshBundle))
            {
                bool matchesVault =
                    freshBundle.X25519IdentityPublicKey.SequenceEqual(_vault.X25519IdentityPublicKey) &&
                    freshBundle.Ed25519IdentityPublicKey.SequenceEqual(_vault.Ed25519IdentityPublicKey);

                if (!matchesVault)
                    throw new InvalidOperationException(
                        Texts.T("ThePublicKeysForYourAccountAlt"));
            }
            else if (!EnsureRecipientTrusted(freshBundle, formattedFingerprint))
            {
                throw new InvalidOperationException(
                    Texts.T("TheRecipientSNewIdentityHas"));
            }

            _currentRecipientBundle = freshBundle;
            _recipientBundleMustBeRefreshed = false;

            if (_sessionPassword is not null)
                await SaveVaultAsync(_sessionPassword);
        }

        private static X3dhPreKeyBundle ToX3dhPreKeyBundle(PreKeyBundleResponse response)
        {
            return new X3dhPreKeyBundle(
                RecipientUsername: response.Username,
                RecipientX25519IdentityPublicKey: response.X25519IdentityPublicKey,
                RecipientEd25519IdentityPublicKey: response.Ed25519IdentityPublicKey,
                SignedPreKeyId: response.SignedPreKeyId,
                SignedPreKeyPublicKey: response.SignedPreKeyPublicKey,
                SignedPreKeySignature: response.SignedPreKeySignature,
                OneTimePreKeyId: response.OneTimePreKeyId,
                OneTimePreKeyPublicKey: response.OneTimePreKeyPublicKey);
        }

        private async void SendEncryptedButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedFilesStatusTextBlock.Foreground = System.Windows.Media.Brushes.IndianRed;
            var pendingEncryptedFiles = new List<PendingEncryptedFile>();

            if (_session is null)
            {
                SelectedFilesStatusTextBlock.Text = Texts.T("SignInFirst");
                return;
            }

            if (_vault is null)
            {
                SelectedFilesStatusTextBlock.Text = Texts.T("TheCryptographicVaultHasNotBeen");
                return;
            }

            if (!IsCurrentRecipientTrustedForSend())
            {
                SelectedFilesStatusTextBlock.Text = Texts.T("CheckAndVerifyTheRecipientFirst");
                SetSendButtonEnabled(false);
                return;
            }

            if (_selectedFilePaths.Count == 0)
            {
                SelectedFilesStatusTextBlock.Text = Texts.T("SelectAtLeastOneFile");
                return;
            }

            if (_selectedFilePaths.Count > MaximumFilesPerTransfer)
            {
                SelectedFilesStatusTextBlock.Text = Texts.T("YouCanSendUpToFiles", MaximumFilesPerTransfer);
                SetSendButtonEnabled(false);
                return;
            }

            try
            {
                SetSendButtonEnabled(false);
                SendEncryptedButton.Content = Texts.T("Encrypting");
                SelectedFilesStatusTextBlock.Foreground = System.Windows.Media.Brushes.LightGreen;
                SelectedFilesStatusTextBlock.Text = Texts.T("UpdatingTheRecipientSKeys");

                await RefreshRecipientBundleIfNeededAsync();

                if (_currentRecipientBundle is not { } verifiedBundle || !IsCurrentRecipientTrustedForSend())
                    throw new InvalidOperationException(Texts.T("CheckAndVerifyTheRecipientFirst"));
                SelectedFilesStatusTextBlock.Text = Texts.T("DerivingTheX3DHSecret");
                int totalSendSteps = (_selectedFilePaths.Count * 2) + 1;
                int completedSendSteps = 0;
                ShowProgress(SendProgressBar, totalSendSteps);

                X3dhPreKeyBundle recipientBundle = ToX3dhPreKeyBundle(verifiedBundle);

                X3dhSecretResult x3dhResult = _x3dhService.CreateSenderSecret(
                    new X3dhSenderSecretInput(
                        SenderUsername: _session.Username,
                        SenderX25519IdentityPrivateKey: _vault.X25519IdentityPrivateKey,
                        SenderX25519IdentityPublicKey: _vault.X25519IdentityPublicKey,
                        SenderEd25519IdentityPublicKey: _vault.Ed25519IdentityPublicKey,
                        RecipientBundle: recipientBundle));

                // The bundle may contain an OPK and must never be reused for another transfer.
                _recipientBundleMustBeRefreshed = true;

                byte[] serializedX3dhHeader = _x3dhHeaderSerializer.Serialize(x3dhResult.Header);

                for (int fileIndex = 0; fileIndex < _selectedFilePaths.Count; fileIndex++)
                {
                    string filePath = _selectedFilePaths[fileIndex];
                    var fileInfo = new FileInfo(filePath);

                    SelectedFilesStatusTextBlock.Text = Texts.T("EncryptingAlt", fileInfo.Name);

                    FileDerivedKeys fileKeys = _fileKeyDerivationService.DeriveFileKeys(
                        x3dhResult.RootKey,
                        x3dhResult.Header.TransferContextId,
                        fileIndex);

                    var metadata = new FileMetadata
                    {
                        OriginalFileName = fileInfo.Name,
                        PlaintextLength = fileInfo.Length,
                        ContentType = null,
                        LastModifiedUtc = fileInfo.LastWriteTimeUtc
                    };

                    byte[] protectedMetadata = _fileMetadataProtector.Protect(
                        metadata,
                        fileKeys.MetadataKey);

                    string tempEncryptedFilePath = Path.GetTempFileName();

                    await using (var plaintextStream = fileInfo.OpenRead())
                    await using (var encryptedStream = new FileStream(
                        tempEncryptedFilePath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 1024 * 64,
                        useAsync: true))
                    {
                        await _fileContentEncryptionService.EncryptAsync(
                            plaintextStream,
                            encryptedStream,
                            fileKeys.FileKey);
                    }

                    long ciphertextLength = new FileInfo(tempEncryptedFilePath).Length;

                    pendingEncryptedFiles.Add(new PendingEncryptedFile
                    {
                        FileIndex = fileIndex,
                        FileHeader = protectedMetadata,
                        TempEncryptedFilePath = tempEncryptedFilePath,
                        CiphertextLength = ciphertextLength
                    });

                    completedSendSteps++;
                    UpdateProgress(SendProgressBar, completedSendSteps);
                }

                SelectedFilesStatusTextBlock.Text = Texts.T("StartingTransfer");

                if (!ReferenceEquals(verifiedBundle, _currentRecipientBundle) || !IsCurrentRecipientTrustedForSend())
                    throw new InvalidOperationException(Texts.T("CheckAndVerifyTheRecipientFirst"));
                var createTransferRequest = new CreateTransferRequest
                {
                    RecipientUsername = verifiedBundle.Username,
                    X3dhHeader = serializedX3dhHeader,
                    Files = pendingEncryptedFiles
                        .Select(file => new CreateTransferFileRequest
                        {
                            FileIndex = file.FileIndex,
                            FileHeader = file.FileHeader,
                            CiphertextLength = file.CiphertextLength
                        })
                        .ToList()
                };

                CreateTransferResponse createTransferResponse = await _apiClient.CreateTransferAsync(
                    createTransferRequest);

                completedSendSteps++;
                UpdateProgress(SendProgressBar, completedSendSteps);

                foreach (PendingEncryptedFile encryptedFile in pendingEncryptedFiles)
                {
                    CreateTransferFileResponse serverFile = createTransferResponse.Files
                        .Single(file => file.FileIndex == encryptedFile.FileIndex);

                    SelectedFilesStatusTextBlock.Text = Texts.T("UploadingFile", encryptedFile.FileIndex + 1, pendingEncryptedFiles.Count);

                    await using var encryptedFileStream = new FileStream(
                        encryptedFile.TempEncryptedFilePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 1024 * 64,
                        useAsync: true);

                    await _apiClient.UploadTransferFileContentAsync(
                        createTransferResponse.TransferId,
                        serverFile.FileId,
                        encryptedFileStream,
                        encryptedFile.CiphertextLength);

                    completedSendSteps++;
                    UpdateProgress(SendProgressBar, completedSendSteps);
                }

                SelectedFilesStatusTextBlock.Text =
                    Texts.T("SentExpires", TransferExpiryFormatter.Format(createTransferResponse.ExpiresAtUtc));

                _selectedFilePaths.Clear();
                SelectedFilesListBox.Items.Clear();
                SelectedFilesItemsControl.Items.Clear();
                SetSendButtonEnabled(false);
                DropZoneBorder.IsEnabled = IsCurrentRecipientStillSelected();
                DropZonePromptTextBlock.Text = DropZoneBorder.IsEnabled
                    ? DropZoneReadyText
                    : DropZoneDisabledText;
                UpdateProgress(SendProgressBar, totalSendSteps);
            }
            catch (Exception ex)
            {
                SelectedFilesStatusTextBlock.Foreground = System.Windows.Media.Brushes.IndianRed;
                SelectedFilesStatusTextBlock.Text = GetUserFacingError(ex);
            }
            finally
            {
                foreach (PendingEncryptedFile pendingFile in pendingEncryptedFiles)
                {
                    if (File.Exists(pendingFile.TempEncryptedFilePath))
                        File.Delete(pendingFile.TempEncryptedFilePath);
                }

                if (_selectedFilePaths.Count > 0)
                    SetSendButtonEnabled(true);

                SendEncryptedButton.Content = Texts.T("Send");
            }
        }

        private async Task SaveVaultAsync(string password)
        {
            if (_session is null || _vault is null)
                throw new InvalidOperationException(Texts.T("TheCryptographicSessionOrVaultIs"));

            byte[] updatedEncryptedKeyPayload = _vaultService.EncryptVault(
                password,
                _session.PayloadSalt,
                _vault);

            await _apiClient.UpdateVaultAsync(
                new UpdateVaultRequest
                {
                    EncryptedKeyPayload = updatedEncryptedKeyPayload
                });

            _session.EncryptedKeyPayload = updatedEncryptedKeyPayload;
        }        

        private void InboxListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            SetDownloadButtonEnabled(InboxListBox.SelectedIndex >= 0);
        }

        private async void DownloadDecryptButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isDownloading) return;
            InboxStatusTextBlock.Foreground = System.Windows.Media.Brushes.IndianRed;

            if (_session is null)
            {
                InboxStatusTextBlock.Text = Texts.T("SignInFirst");
                return;
            }

            if (_vault is null)
            {
                InboxStatusTextBlock.Text = Texts.T("TheCryptographicVaultHasNotBeen");
                return;
            }

            if (_sessionPassword is null)
            {
                InboxStatusTextBlock.Text = Texts.T("TheSessionPasswordIsUnavailable");
                return;
            }

            if (InboxListBox.SelectedIndex < 0 || InboxListBox.SelectedIndex >= _inboxTransfers.Count)
            {
                InboxStatusTextBlock.Text = Texts.T("SelectATransferFirst");
                return;
            }

            TransferSummaryResponse selectedTransfer = _inboxTransfers[InboxListBox.SelectedIndex];
            ClientOneTimePreKeyState? transferOneTimePreKey = null;
            _isDownloading = true;
            ++_inboxRevision;
            RefreshInboxButton.IsEnabled = false;
            InboxListBox.IsEnabled = false;
            SetDownloadButtonEnabled(false);

            try
            {
                string outputDirectory;

                if (UseDefaultDownloadDirectoryCheckBox.IsChecked == true)
                {
                    outputDirectory = GetDefaultDownloadDirectoryFromTextBox();

                    if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
                    {
                        InboxStatusTextBlock.Text = Texts.T("InvalidDefaultFolderChooseAValid");
                        return;
                    }
                }
                else
                {
                    var folderDialog = new OpenFolderDialog
                    {
                        Title = Texts.T("ChooseWhereToSaveTheDecrypted")
                    };

                    if (folderDialog.ShowDialog() != true)
                        return;

                    outputDirectory = folderDialog.FolderName;
                }

                SetDownloadButtonEnabled(false);
                DownloadDecryptButton.Content = Texts.T("Decrypting");
                InboxStatusTextBlock.Foreground = System.Windows.Media.Brushes.LightGreen;
                InboxStatusTextBlock.Text = Texts.T("LoadingTransferDetails");
                ResetProgress(InboxProgressBar);

                TransferDetailResponse transferDetail = await _apiClient.GetTransferDetailAsync(
                    selectedTransfer.TransferId);

                int totalInboxSteps = (transferDetail.Files.Count * 2) + 2;
                int completedInboxSteps = 1;
                ShowProgress(InboxProgressBar, totalInboxSteps);
                UpdateProgress(InboxProgressBar, completedInboxSteps);

                X3dhMessageHeader x3dhHeader = _x3dhHeaderSerializer.Deserialize(
                    transferDetail.X3dhHeader);

                ClientSignedPreKeyState? recipientSignedPreKey = ResolveSignedPreKey(
                    _vault,
                    x3dhHeader.RecipientSignedPreKeyId);

                if (recipientSignedPreKey is null)
                    throw new InvalidOperationException(Texts.T("TheSignedPreKeyUsedFor"));

                ClientOneTimePreKeyState? oneTimePreKey = null;

                if (x3dhHeader.RecipientOneTimePreKeyId is not null)
                {
                    oneTimePreKey = _vault.OneTimePreKeys.SingleOrDefault(key =>
                        key.KeyId == x3dhHeader.RecipientOneTimePreKeyId.Value);

                    if (oneTimePreKey is null)
                        throw new InvalidOperationException(Texts.T("TheOneTimePreKeyUsed"));
                }

                transferOneTimePreKey = oneTimePreKey;

                X3dhSecretResult recipientSecret = _x3dhService.CreateRecipientSecret(
                    new X3dhRecipientSecretInput(
                        RecipientUsername: _session.Username,
                        RecipientX25519IdentityPrivateKey: _vault.X25519IdentityPrivateKey,
                        SignedPreKeyPrivateKey: recipientSignedPreKey.PrivateKey,
                        SignedPreKeyId: recipientSignedPreKey.KeyId,
                        OneTimePreKeyPrivateKey: oneTimePreKey?.PrivateKey,
                        OneTimePreKeyId: oneTimePreKey?.KeyId,
                        Header: x3dhHeader));

                string stagingDirectory = Path.Combine(
                    outputDirectory,
                    $".nox-download-{Guid.NewGuid():N}");

                Directory.CreateDirectory(stagingDirectory);

                var stagedFiles = new List<(string StagedPath, string OutputPath)>();
                var reservedOutputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    foreach (TransferFileResponse file in transferDetail.Files.OrderBy(file => file.FileIndex))
                    {
                        InboxStatusTextBlock.Text = Texts.T("DownloadingFile", file.FileIndex + 1, transferDetail.Files.Count);

                        FileDerivedKeys fileKeys = _fileKeyDerivationService.DeriveFileKeys(
                            recipientSecret.RootKey,
                            x3dhHeader.TransferContextId,
                            file.FileIndex);

                        FileMetadata metadata = _fileMetadataProtector.Unprotect(
                            file.FileHeader,
                            fileKeys.MetadataKey);

                        string safeFileName = Path.GetFileName(metadata.OriginalFileName);
                        string outputPath = GetAvailableFilePath(
                            Path.Combine(outputDirectory, safeFileName),
                            reservedOutputPaths);

                        reservedOutputPaths.Add(outputPath);

                        string tempPlaintextFilePath = Path.Combine(
                            stagingDirectory,
                            $"{file.FileId:N}.plaintext");

                        var fileDownload = new FileDownloadService(_fileContentEncryptionService);
                        await _apiClient.ProcessTransferFileContentAsync(
                            transferDetail.TransferId,
                            file.FileId,
                            (source, token) => fileDownload.DecryptToStagingFileAsync(
                                source, tempPlaintextFilePath, fileKeys.FileKey,
                                metadata.PlaintextLength, token));

                        stagedFiles.Add((tempPlaintextFilePath, outputPath));

                        completedInboxSteps += 2;
                        UpdateProgress(InboxProgressBar, completedInboxSteps);
                    }

                    foreach ((string stagedPath, string outputPath) in stagedFiles)
                    {
                        File.Move(stagedPath, outputPath);
                    }
                }
                finally
                {
                    if (Directory.Exists(stagingDirectory))
                        Directory.Delete(stagingDirectory, recursive: true);
                }

                // Keep the decryption key if the server cannot acknowledge a long download.
                await _apiClient.MarkTransferDownloadedAsync(transferDetail.TransferId);

                if (oneTimePreKey is not null)
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(
                        oneTimePreKey.PrivateKey);
                    _vault.OneTimePreKeys.Remove(oneTimePreKey);
                    await SaveVaultAsync(_sessionPassword);
                }

                completedInboxSteps++;
                UpdateProgress(InboxProgressBar, completedInboxSteps);

                InboxStatusTextBlock.Text = Texts.T("FileDownloadedDecryptedAndRemovedFrom");

                await RefreshInboxAsync(allowDuringDownload: true);
            }
            catch (System.Security.Cryptography.AuthenticationTagMismatchException)
            {
                bool removedFromServer = false;

                try
                {
                    await _apiClient.ReportTransferTamperedAsync(selectedTransfer.TransferId);
                    removedFromServer = true;

                    if (transferOneTimePreKey is not null)
                    {
                        System.Security.Cryptography.CryptographicOperations.ZeroMemory(
                            transferOneTimePreKey.PrivateKey);
                        _vault.OneTimePreKeys.Remove(transferOneTimePreKey);
                        await SaveVaultAsync(_sessionPassword);
                    }
                }
                catch
                {
                    // The local plaintext is already discarded. The dialog reports that
                    // server-side deletion could not be confirmed.
                }

                InboxStatusTextBlock.Foreground = System.Windows.Media.Brushes.IndianRed;
                InboxStatusTextBlock.Text = removedFromServer
                    ? Texts.T("TamperedFileDiscardedLocallyAndDeleted")
                    : Texts.T("TamperedFileDiscardedLocallyServerDeletion");
                ShowDataTamperingWarning(removedFromServer);

                if (removedFromServer)
                    await RefreshInboxAsync(allowDuringDownload: true);
            }
            catch (Exception ex)
            {
                InboxStatusTextBlock.Foreground = System.Windows.Media.Brushes.IndianRed;
                InboxStatusTextBlock.Text = GetUserFacingError(ex);
            }
            finally
            {
                _isDownloading = false;
                RefreshInboxButton.IsEnabled = !_isManualInboxRefresh;
                InboxListBox.IsEnabled = true;
                SetDownloadButtonEnabled(InboxListBox.SelectedIndex >= 0);
                DownloadDecryptButton.Content = Texts.T("Download");
            }
        }

        private static ClientSignedPreKeyState? ResolveSignedPreKey(
            UserKeyPayload vault,
            int keyId)
        {
            if (vault.SignedPreKey?.KeyId == keyId)
                return vault.SignedPreKey;

            return vault.PreviousSignedPreKeys.SingleOrDefault(preKey => preKey.KeyId == keyId);
        }

        private async Task RefreshInboxAsync(bool showUpdating = false, bool allowDuringDownload = false)
        {
            await _inboxRefreshGate.WaitAsync();
            try
            {
                if (_isDownloading && !allowDuringDownload) return;
                var session = _session;
                int revision = _inboxRevision;
                if (_session is null)
                {
                    InboxStatusTextBlock.Foreground = System.Windows.Media.Brushes.IndianRed;
                    InboxStatusTextBlock.Text = Texts.T("SignInFirstAlt");
                    UpdateInboxIndicator();
                    return;
                }

                if (showUpdating)
                {
                    SetInboxUpdatingStatus();
                    ResetProgress(InboxProgressBar);
                }

                List<TransferSummaryResponse> transfers = await _apiClient.GetInboxAsync();
                // A response started before a download or logout must not replace its UI.
                if (!ReferenceEquals(session, _session) || revision != _inboxRevision) return;
                Guid? selectedTransferId = null;

                if (InboxListBox.SelectedIndex >= 0 && InboxListBox.SelectedIndex < _inboxTransfers.Count)
                    selectedTransferId = _inboxTransfers[InboxListBox.SelectedIndex].TransferId;

                bool changed = !AreInboxTransfersEqual(_inboxTransfers, transfers);
                if (changed)
                {
                    InboxListBox.Items.Clear();
                    _inboxTransfers.Clear();

                    foreach (TransferSummaryResponse transfer in transfers)
                    {
                        _inboxTransfers.Add(transfer);
                        InboxListBox.Items.Add(FormatInboxTransfer(transfer));
                    }

                    if (selectedTransferId is Guid transferId)
                    {
                        int restoredIndex = _inboxTransfers.FindIndex(transfer => transfer.TransferId == transferId);
                        if (restoredIndex >= 0)
                            InboxListBox.SelectedIndex = restoredIndex;
                    }
                }

                SetDownloadButtonEnabled(InboxListBox.SelectedIndex >= 0);
                if (showUpdating || (changed && !_isDownloading))
                {
                    InboxStatusTextBlock.Foreground = System.Windows.Media.Brushes.LightGreen;
                    InboxStatusTextBlock.Text = transfers.Count == 0
                        ? string.Empty
                        : transfers.Count == 1
                            ? Texts.T("1PendingTransfer")
                            : Texts.T("PendingTransfers", transfers.Count);
                }
                UpdateInboxIndicator();
            }
            finally { _inboxRefreshGate.Release(); }
        }

        private void SetInboxUpdatingStatus()
        {
            InboxStatusTextBlock.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(245, 158, 11));
            InboxStatusTextBlock.Text = Texts.T("Refreshing");
        }

        private void UpdateInboxIndicator()
        {
            bool hasIncomingFiles = InboxListBox.Items.Count > 0;

            InboxPresenceIndicator.Fill = new System.Windows.Media.SolidColorBrush(
                hasIncomingFiles
                    ? System.Windows.Media.Color.FromRgb(64, 217, 119)
                    : System.Windows.Media.Color.FromRgb(170, 180, 195));
        }

        private static bool AreInboxTransfersEqual(
            IReadOnlyList<TransferSummaryResponse> currentTransfers,
            IReadOnlyList<TransferSummaryResponse> newTransfers)
        {
            if (currentTransfers.Count != newTransfers.Count)
                return false;

            for (int i = 0; i < currentTransfers.Count; i++)
            {
                TransferSummaryResponse current = currentTransfers[i];
                TransferSummaryResponse next = newTransfers[i];

                if (current.TransferId != next.TransferId ||
                    current.SenderUsername != next.SenderUsername ||
                    current.FileCount != next.FileCount ||
                    current.ExpiresAtUtc != next.ExpiresAtUtc)
                {
                    return false;
                }
            }

            return true;
        }

        private string FormatInboxTransfer(TransferSummaryResponse transfer)
        {
            string senderDisplayName = _session is not null &&
                string.Equals(transfer.SenderUsername, _session.Username, StringComparison.OrdinalIgnoreCase)
                    ? Texts.T("MeAlt")
                    : transfer.SenderUsername;

            return Texts.T("FileSExpires", senderDisplayName, transfer.FileCount,
                TransferExpiryFormatter.Format(transfer.ExpiresAtUtc));
        }

        private static string FormatFileSize(long bytes)
        {
            string[] units = [Texts.T("Bytes"), "KB", "MB", "GB", "TB"];
            double size = bytes;
            int unitIndex = 0;

            while (size >= 1024 && unitIndex < units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }

            if (unitIndex == 0)
                return $"{bytes} {units[unitIndex]}";

            return $"{size:0.#} {units[unitIndex]}";
        }

        private async void RefreshInboxButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isDownloading || _isManualInboxRefresh) return;
            var session = _session;
            int revision = _inboxRevision;
            _isManualInboxRefresh = true;
            try
            {
                RefreshInboxButton.IsEnabled = false;
                RefreshInboxButton.Content = Texts.T("RefreshingAlt");

                await RefreshInboxAsync(showUpdating: true);
            }
            catch (Exception ex)
            {
                if (_isDownloading || _session is null || !ReferenceEquals(session, _session) || revision != _inboxRevision) return;
                InboxStatusTextBlock.Foreground = System.Windows.Media.Brushes.IndianRed;
                InboxStatusTextBlock.Text = GetUserFacingError(ex);
            }
            finally
            {
                _isManualInboxRefresh = false;
                RefreshInboxButton.IsEnabled = !_isDownloading;
                RefreshInboxButton.Content = Texts.T("Refresh");
            }
        }

        private void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            bool confirmed = ConfirmAction(this,
                Texts.T("LogOutOfThisSession"),
                Texts.T("Logout"),
                Texts.T("Logout"));

            if (!confirmed)
                return;

            Logout();
        }

        private void Logout()
        {
            ++_inboxRevision;
            _inboxPollingTimer.Stop();
            _isInboxPollingRefreshRunning = false;

            _session = null;
            WorkspaceTabs.SelectedIndex = 0;
            IdentityUsername.Text = ContactsStatus.Text = string.Empty;
            IdentityFingerprint.ItemsSource = null;
            TrustedContactsListBox.Items.Clear();
            _sessionPassword = null;
            _vault = null;
            _currentRecipientBundle = null;
            _recipientBundleMustBeRefreshed = false;
            _selectedFilePaths.Clear();
            _inboxTransfers.Clear();
            _apiClient.ClearAuthentication();

            SignedInUserTextBlock.Text = string.Empty;
            SetRecipientLocked(false);
            RecipientUsernameTextBox.Text = string.Empty;
            ApplyRecipientPlaceholderIfEmpty();
            RecipientStatusTextBlock.Text = string.Empty;
            TrustedRecipientsComboBox.Items.Clear();

            SelectedFilesListBox.Items.Clear();
            SelectedFilesItemsControl.Items.Clear();
            SelectedFilesStatusTextBlock.Text = string.Empty;
            SetSendButtonEnabled(false);
            DropZoneBorder.IsEnabled = false;
            DropZonePromptTextBlock.Text = DropZoneDisabledText;
            ResetProgress(SendProgressBar);

            InboxListBox.Items.Clear();
            InboxStatusTextBlock.Text = string.Empty;
            SetDownloadButtonEnabled(false);
            UpdateInboxIndicator();
            ResetProgress(InboxProgressBar);

            AuthErrorTextBlock.Text = string.Empty;
            AuthErrorTextBlock.Foreground = System.Windows.Media.Brushes.IndianRed;
            PasswordBox.Clear();
            ResetServerConnectionState();

            MainShell.Visibility = Visibility.Collapsed;
            AuthScreen.Visibility = Visibility.Visible;
            SetResourceReference(TitleProperty, "NoxText.EndToEndEncryptedFileTransfer");
            ResizeAuthWindow(false);
        }

        private sealed record ContactRow(string Username, string[] Groups);

        private void WorkspaceTab_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (e.OriginalSource != WorkspaceTabs || _vault is null || _session is null) return;
            if (IdentityTab.IsSelected)
            {
                IdentityUsername.Text = _session.Username;
                IdentityFingerprint.ItemsSource = OwnFingerprint().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            }
            else if (ContactsTab.IsSelected) RefreshTrustedContactsList();
        }

        private string OwnFingerprint()
        {
            if (_vault is null || _session is null) return string.Empty;
            byte[] hash = _fingerprintService.ComputeFingerprint(_session.Username, _vault.X25519IdentityPublicKey, _vault.Ed25519IdentityPublicKey);
            return _fingerprintService.FormatFingerprint(hash, hash.Length);
        }

        private void CopyIdentity_Click(object sender, RoutedEventArgs e)
        {
            if (_vault != null && _session != null)
                Clipboard.SetText(Texts.T("UsernameNFullFingerprint", _session.Username, OwnFingerprint()));
        }

        private void RefreshTrustedContactsList()
        {
            string? selected = (TrustedContactsListBox.SelectedItem as ContactRow)?.Username;
            TrustedContactsListBox.Items.Clear();
            if (_vault == null) return;
            foreach (var contact in GetVisibleTrustedContacts())
            {
                byte[] hash = _fingerprintService.ComputeFingerprint(contact.Username, contact.X25519IdentityPublicKey, contact.Ed25519IdentityPublicKey);
                var row = new ContactRow(contact.Username, _fingerprintService.FormatFingerprint(hash, hash.Length).Split(' '));
                TrustedContactsListBox.Items.Add(row);
                if (row.Username == selected) TrustedContactsListBox.SelectedItem = row;
            }
            int count = TrustedContactsListBox.Items.Count;
            ContactsStatus.Text = count switch { 0 => Texts.T("NoTrustedContacts"), 1 => Texts.T("1TrustedContact"), _ => Texts.T("TrustedContacts", count) };
            RemoveTrustedContactButton.IsEnabled = TrustedContactsListBox.SelectedItem != null;
        }

        private void TrustedContactsListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            RemoveTrustedContactButton.IsEnabled = TrustedContactsListBox.SelectedIndex >= 0;
        }

        private async void RemoveTrustedContactButton_Click(object sender, RoutedEventArgs e)
        {
            if (_vault is null || _sessionPassword is null)
                return;

            if (TrustedContactsListBox.SelectedItem is not ContactRow selectedContact) return;

            bool confirmed = ConfirmAction(this,
                Texts.T("RemoveTrustedContact", selectedContact.Username),
                Texts.T("RemoveTrustedContactAlt"),
                Texts.T("RemoveContact"));

            if (!confirmed)
                return;

            var vault = _vault;
            var removed = vault.TrustedContacts.Find(contact =>
                string.Equals(contact.Username, selectedContact.Username, StringComparison.OrdinalIgnoreCase));
            if (removed is null) return;
            vault.TrustedContacts.Remove(removed);
            if (string.Equals(GetRecipientUsername(), removed.Username, StringComparison.OrdinalIgnoreCase))
            {
                ++_recipientCheckVersion;
                _currentRecipientBundle = null;
                _recipientBundleMustBeRefreshed = false;
                SetRecipientLocked(false);
                RecipientUsernameTextBox.Clear();
                ApplyRecipientPlaceholderIfEmpty();
                RecipientStatusTextBlock.Text = string.Empty;
                DropZoneBorder.IsEnabled = false;
                DropZonePromptTextBlock.Text = DropZoneDisabledText;
                SetSendButtonEnabled(false);
                ResetProgress(SendProgressBar);
            }

            RefreshTrustedRecipientsComboBox();
            RefreshTrustedContactsList();
            try
            {
                await SaveVaultAsync(_sessionPassword);
            }
            catch (Exception ex)
            {
                if (_vault != vault) return;
                if (!vault.TrustedContacts.Any(contact =>
                    string.Equals(contact.Username, removed.Username, StringComparison.OrdinalIgnoreCase)))
                    vault.TrustedContacts.Add(removed);
                RefreshTrustedRecipientsComboBox();
                RefreshTrustedContactsList();
                ContactsStatus.Text = GetUserFacingError(ex);
            }
        }

        private List<TrustedContact> GetVisibleTrustedContacts()
        {
            if (_vault is null)
                return [];

            return _vault.TrustedContacts
                .Where(contact => !IsCurrentUserTrustedContact(contact))
                .OrderBy(contact => contact.Username)
                .ToList();
        }

        private bool ConfirmAction(Window owner, string message, string title, string action)
        {
            var dialog = new Window { Owner = owner, Title = title, Width = 400, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                Background = (System.Windows.Media.Brush)FindResource("AppBackgroundBrush"),
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush") };
            dialog.SourceInitialized += (_, _) => ApplyNativeTitleBarColors(dialog);
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 15 });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0) };
            var cancel = new Button { Content = Texts.T("Cancel"), IsCancel = true };
            var confirm = new Button { Content = action, Margin = new Thickness(8,0,0,0) };
            confirm.Click += (_, _) => dialog.DialogResult = true;
            buttons.Children.Add(cancel);
            buttons.Children.Add(confirm);
            panel.Children.Add(buttons);
            Views.WindowLayout.FitDialog(dialog, panel);
            dialog.Loaded += (_, _) => cancel.Focus();
            return dialog.ShowDialog() == true;
        }

        private bool IsCurrentUserTrustedContact(TrustedContact contact)
        {
            if (_session is not null &&
                string.Equals(contact.Username, _session.Username, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (_vault is null)
                return false;

            return contact.X25519IdentityPublicKey.SequenceEqual(_vault.X25519IdentityPublicKey) &&
                contact.Ed25519IdentityPublicKey.SequenceEqual(_vault.Ed25519IdentityPublicKey);
        }

        private static string GetUserFacingError(Exception exception)
        {
            return exception switch
            {
                System.Net.Http.HttpRequestException =>
                    Texts.T("CannotReachTheServerCheckYour"),
                TaskCanceledException =>
                    Texts.T("TheRequestTimedOutTryAgain"),
                UriFormatException =>
                    Texts.T("InvalidServerAddress"),
                System.Text.Json.JsonException =>
                    Texts.T("TheServerReturnedInvalidData"),
                IOException =>
                    Texts.T("CouldNotReadOrWriteThe"),
                System.Security.Cryptography.AuthenticationTagMismatchException =>
                    Texts.T("CryptographicVerificationFailed"),
                InvalidOperationException => Texts.ErrorOr(exception.Message, "OperationUnavailable"),
                ArgumentException => Texts.ErrorOr(exception.Message, "InvalidOperationData"),
                _ => Texts.T("AnUnexpectedErrorOccurredTryAgain")
            };
        }


        private static string GetAvailableFilePath(
            string path,
            ISet<string>? reservedPaths = null)
        {
            if (!File.Exists(path) && reservedPaths?.Contains(path) != true)
                return path;

            string directory = Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException(Texts.T("InvalidOutputFolder"));

            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(path);
            string extension = Path.GetExtension(path);

            int counter = 1;

            while (true)
            {
                string candidate = Path.Combine(
                    directory,
                    $"{fileNameWithoutExtension} ({counter}){extension}");

                if (!File.Exists(candidate) && reservedPaths?.Contains(candidate) != true)
                    return candidate;

                counter++;
            }
        }

        private static void ShowProgress(System.Windows.Controls.ProgressBar progressBar, int maximum)
        {
            progressBar.Minimum = 0;
            progressBar.Maximum = Math.Max(1, maximum);
            progressBar.Value = 0;
            progressBar.Visibility = Visibility.Visible;
        }

        private static void UpdateProgress(System.Windows.Controls.ProgressBar progressBar, int value)
        {
            progressBar.Value = Math.Min(progressBar.Maximum, Math.Max(progressBar.Minimum, value));
        }

        private static void ResetProgress(System.Windows.Controls.ProgressBar progressBar)
        {
            progressBar.Value = 0;
            progressBar.Visibility = Visibility.Collapsed;
        }

        private sealed class PendingEncryptedFile
        {
            public int FileIndex { get; set; }
            public byte[] FileHeader { get; set; } = [];
            public string TempEncryptedFilePath { get; set; } = string.Empty;
            public long CiphertextLength { get; set; }
        }
    }
}
