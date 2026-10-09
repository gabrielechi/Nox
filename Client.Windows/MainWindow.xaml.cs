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

        private const string DropZoneDisabledText = "Selezionare un destinatario";
        private const string DropZoneReadyText = "Drag&Drop o Click per selezionare i file";
        private const string DropZoneSelectedFilesText = "File selezionati";
        private const string RecipientPlaceholderText = "Immetti destinatario";
        private const string SendToSelfDisplayText = "me";
        private const double AuthDesignWidth = 1000;
        private const double AuthDesignHeight = 650;
        private const double DashboardDesignWidth = 800;
        private const double DashboardDesignHeight = 665;
        private const double WindowScreenPadding = 32;

        private readonly ApiClient _apiClient = new();
        private readonly ClientAppSettingsService _appSettingsService = new();
        private ClientSession? _session;

        private readonly IUserVaultService _vaultService = new UserVaultService(new ArgonKeyDerivationService(), new AesGcmSymmetricService());
        private readonly IArgonKeyDerivationService _argonService = new ArgonKeyDerivationService();
        private readonly IKeyPairService _keyPairService = new KeyPairService();
        private readonly IPreKeyBootstrapService _preKeyBootstrapService = new PreKeyBootstrapService(new KeyPairService());
        private readonly IIdentityFingerprintService _fingerprintService = new IdentityFingerprintService();

        private PreKeyBundleResponse? _currentRecipientBundle;
        private string? _sessionPassword;
        private readonly List<string> _selectedFilePaths = [];
        private UserKeyPayload? _vault;
        private bool _isRefreshingTrustedRecipients;
        private bool _isServerConnectionVerified;
        private bool _suppressRememberServerChange = true;
        private bool _isApplyingSendToSelfMode;
        private bool _suppressSendToSelfAutoCheck;

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

        public MainWindow()
        {
            InitializeComponent();
            SourceInitialized += MainWindow_SourceInitialized;

            _inboxPollingTimer.Interval = TimeSpan.FromSeconds(30);
            _inboxPollingTimer.Tick += InboxPollingTimer_Tick;

            SetSendButtonEnabled(false);
            SetDownloadButtonEnabled(false);
            LoadClientAppSettings();
            _suppressRememberServerChange = false;
            ApplyScaledWindowSize(AuthDesignWidth, AuthDesignHeight);
        }

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            ApplyNativeTitleBarColors(this);
        }

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
            if (_isInboxPollingRefreshRunning)
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

        private void LoadClientAppSettings()
        {
            ClientAppSettings settings = _appSettingsService.Load();

            if (settings.RememberServer && !string.IsNullOrWhiteSpace(settings.RememberedServerUrl))
            {
                RememberServerCheckBox.IsChecked = true;
                ServerUrlTextBox.Text = settings.RememberedServerUrl;
            }

            UseDefaultDownloadDirectoryCheckBox.IsChecked = settings.UseDefaultDownloadDirectory;
            SetDefaultDownloadDirectoryText(settings.DefaultDownloadDirectory);
        }

        private void ApplyScaledWindowSize(double designWidth, double designHeight)
        {
            Rect workArea = SystemParameters.WorkArea;

            double availableWidth = Math.Max(1, workArea.Width - WindowScreenPadding);
            double availableHeight = Math.Max(1, workArea.Height - WindowScreenPadding);
            double scale = Math.Min(1.0, Math.Min(availableWidth / designWidth, availableHeight / designHeight));

            AppRoot.Width = designWidth;
            AppRoot.Height = designHeight;
            AppRoot.LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale);

            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                Left = workArea.Left + (workArea.Width - ActualWidth) / 2;
                Top = workArea.Top + (workArea.Height - ActualHeight) / 2;
            }));
        }

        private async void SignInButton_Click(object sender, RoutedEventArgs e)
        {
            AuthErrorTextBlock.Text = string.Empty;
            AuthErrorTextBlock.Foreground = System.Windows.Media.Brushes.IndianRed;

            string serverUrl = ServerUrlTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(serverUrl))
            {
                AuthErrorTextBlock.Text = "Server URL mancante.";
                return;
            }

            if (!_isServerConnectionVerified)
            {
                await CheckServerConnectionAsync(serverUrl);
                return;
            }

            string username = UsernameTextBox.Text.Trim();
            string password = PasswordBox.Password;

            if (string.IsNullOrWhiteSpace(username))
            {
                AuthErrorTextBlock.Text = "Username mancante.";
                return;
            }

            if (string.IsNullOrWhiteSpace(password))
            {
                AuthErrorTextBlock.Text = "Password mancante.";
                return;
            }

            try
            {
                SignInButton.IsEnabled = false;
                SignInButton.Content = "Accedo...";

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
                    AuthErrorTextBlock.Text = $"Accesso riuscito, ma non è stato possibile decifrare il vault: {GetUserFacingError(ex)}";
                    return;
                }

                if (_vault is null)
                    throw new InvalidOperationException("Non è stato possibile caricare il vault crittografico.");

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
                        AuthErrorTextBlock.Text = "Accesso annullato: identità crittografica non verificata.";
                        return;
                    }

                    await RecoverOwnIdentityAsync(recoveryAction, password);
                }

                _sessionPassword = password;
                await _preKeyBootstrapService.EnsurePreKeysAsync(_apiClient, _vault);
                byte[] updatedEncryptedKeyPayload = _vaultService.EncryptVault(password, _session.PayloadSalt, _vault);

                await _apiClient.UpdateVaultAsync(
                    new UpdateVaultRequest
                    {
                        EncryptedKeyPayload = updatedEncryptedKeyPayload
                    });

                _session.EncryptedKeyPayload = updatedEncryptedKeyPayload;

                CurrentUserResponse currentUser = await _apiClient.GetCurrentUserAsync();

                SignedInUserTextBlock.Text = currentUser.Username;
                RefreshTrustedRecipientsComboBox();

                AuthScreen.Visibility = Visibility.Collapsed;
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
                SignInButton.IsEnabled = true;
                SignInButton.Content = "Accedi";
            }
        }

        private async Task CheckServerConnectionAsync(string serverUrl)
        {
            try
            {
                SignInButton.IsEnabled = false;
                SignInButton.Content = "Checking...";

                await _apiClient.CheckHealthAsync(serverUrl);

                _isServerConnectionVerified = true;
                UsernameTextBox.IsEnabled = true;
                PasswordBox.IsEnabled = true;
                UsernameLabelTextBlock.Visibility = Visibility.Visible;
                UsernameTextBox.Visibility = Visibility.Visible;
                PasswordLabelTextBlock.Visibility = Visibility.Visible;
                PasswordBox.Visibility = Visibility.Visible;
                CreateAccountButton.Visibility = Visibility.Visible;
                ServerOnlineIndicator.Visibility = Visibility.Visible;
                ServerOnlineIndicator.Fill = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(64, 217, 119));
                SaveRememberedServerIfRequested(serverUrl);

                AuthErrorTextBlock.Foreground = System.Windows.Media.Brushes.LightGreen;
                AuthErrorTextBlock.Text = "Connessione al server riuscita.";
                SignInButton.Content = "Accedi";
            }
            catch (Exception ex)
            {
                _isServerConnectionVerified = false;
                UsernameTextBox.IsEnabled = false;
                PasswordBox.IsEnabled = false;
                UsernameLabelTextBlock.Visibility = Visibility.Collapsed;
                UsernameTextBox.Visibility = Visibility.Collapsed;
                PasswordLabelTextBlock.Visibility = Visibility.Collapsed;
                PasswordBox.Visibility = Visibility.Collapsed;
                CreateAccountButton.Visibility = Visibility.Collapsed;
                ServerOnlineIndicator.Visibility = Visibility.Visible;
                ServerOnlineIndicator.Fill = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(255, 92, 92));

                AuthErrorTextBlock.Foreground = System.Windows.Media.Brushes.IndianRed;
                AuthErrorTextBlock.Text = GetUserFacingError(ex);
                SignInButton.Content = "Connetti";
            }
            finally
            {
                SignInButton.IsEnabled = true;
            }
        }

        private void ServerUrlTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ResetServerConnectionState();
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
                Title = "Scegli la default directory"
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
                DefaultDownloadDirectoryTextBox.Text = "default directory";
                DefaultDownloadDirectoryTextBox.Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush");
                return;
            }

            DefaultDownloadDirectoryTextBox.Text = directory;
            DefaultDownloadDirectoryTextBox.Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
        }

        private string GetDefaultDownloadDirectoryFromTextBox()
        {
            string directory = DefaultDownloadDirectoryTextBox.Text.Trim();

            return string.Equals(directory, "default directory", StringComparison.Ordinal)
                ? string.Empty
                : directory;
        }

        private void ResetServerConnectionState()
        {
            _isServerConnectionVerified = false;

            if (UsernameTextBox is null ||
                PasswordBox is null ||
                UsernameLabelTextBlock is null ||
                PasswordLabelTextBlock is null ||
                CreateAccountButton is null ||
                SignInButton is null ||
                ServerOnlineIndicator is null)
                return;

            UsernameTextBox.IsEnabled = false;
            PasswordBox.IsEnabled = false;
            UsernameLabelTextBlock.Visibility = Visibility.Collapsed;
            UsernameTextBox.Visibility = Visibility.Collapsed;
            PasswordLabelTextBlock.Visibility = Visibility.Collapsed;
            PasswordBox.Visibility = Visibility.Collapsed;
            CreateAccountButton.Visibility = Visibility.Collapsed;
            ServerOnlineIndicator.Visibility = Visibility.Visible;
            ServerOnlineIndicator.Fill = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(255, 92, 92));
            SignInButton.Content = "Connetti";
        }

        private async void CreateAccountButton_Click(object sender, RoutedEventArgs e)
        {
            AuthErrorTextBlock.Text = string.Empty;

            string serverUrl = ServerUrlTextBox.Text.Trim();
            string username = UsernameTextBox.Text.Trim();
            string password = PasswordBox.Password;

            if (!_isServerConnectionVerified)
            {
                AuthErrorTextBlock.Text = "Verifica prima la connessione al server.";
                return;
            }

            if (string.IsNullOrWhiteSpace(serverUrl))
            {
                AuthErrorTextBlock.Text = "Server URL mancante";
                return;
            }

            if (string.IsNullOrWhiteSpace(username))
            {
                AuthErrorTextBlock.Text = "Inserire Username";
                return;
            }

            if (string.IsNullOrWhiteSpace(password))
            {
                AuthErrorTextBlock.Text = "Inserire Password";
                return;
            }

            if (password.Length < 12)
            {
                AuthErrorTextBlock.Text = "La password deve avere minimo 12 caratteri";
                return;
            }

            if (!password.Any(char.IsDigit))
            {
                AuthErrorTextBlock.Text = "La password deve contenere almeno un numero";
                return;
            }

            if (!password.Any(ch => !char.IsLetterOrDigit(ch)))
            {
                AuthErrorTextBlock.Text = "La password deve contenere almeno un carattere speciale";
                return;
            }

            try
            {
                CreateAccountButton.IsEnabled = false;
                SignInButton.IsEnabled = false;
                CreateAccountButton.Content = "Registrazione..";

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
                AuthErrorTextBlock.Text = "Account creato. Puoi eseguire il login";
            }
            catch (Exception ex)
            {
                AuthErrorTextBlock.Foreground = System.Windows.Media.Brushes.IndianRed;
                AuthErrorTextBlock.Text = GetUserFacingError(ex);
            }
            finally
            {
                CreateAccountButton.IsEnabled = true;
                SignInButton.IsEnabled = true;
                CreateAccountButton.Content = "Crea Account";
            }
        }

        private async void CheckRecipientButton_Click(object sender, RoutedEventArgs e)
        {
            RecipientStatusTextBlock.Text = string.Empty;
            RecipientFingerprintTextBlock.Text = string.Empty;

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
                RecipientStatusTextBlock.Text = "Devi effettuare il login prima di contattare un destinatario.";
                return;
            }

            string recipientUsername = GetRecipientUsername();

            if (string.IsNullOrWhiteSpace(recipientUsername))
            {
                RecipientStatusTextBlock.Text = "L'username del destinatario è necessario.";
                return;
            }

            try
            {
                ApplySendToSelfModeIfRecipientIsCurrentUser(recipientUsername);
                recipientUsername = GetRecipientUsername();

                CheckRecipientButton.IsEnabled = false;
                CheckRecipientButton.Content = "Checking...";

                PreKeyBundleResponse bundle = await _apiClient.GetPreKeyBundleAsync(recipientUsername);

                _currentRecipientBundle = bundle;

                byte[] fingerprint = _fingerprintService.ComputeFingerprint(
                    bundle.Username,
                    bundle.X25519IdentityPublicKey,
                    bundle.Ed25519IdentityPublicKey);

                string formattedFingerprint = _fingerprintService.FormatFingerprint(fingerprint);

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
                    RecipientStatusTextBlock.Text = "Rifiuto del destinatario, trasferimento bloccato.";
                    _currentRecipientBundle = null;
                    return;
                }

                RecipientStatusTextBlock.Foreground = System.Windows.Media.Brushes.LightGreen;

                if (IsCurrentUserBundle(bundle))
                {
                    RecipientStatusTextBlock.Text = "Fingerprint";
                }
                else if (wasAlreadyTrusted)
                {
                    RecipientStatusTextBlock.Text = hasOneTimePreKey
                        ? $"Utente '{bundle.Username}' disponibile"
                        : $"Utente '{bundle.Username}' pronto, ma non ci sono OPKs disponibili | X3DH fallback attivato";
                }
                else
                {
                    RecipientStatusTextBlock.Text = hasOneTimePreKey
                        ? $"Utente '{bundle.Username}' verificato e aggiunto ai contatti"
                        : $"Utente '{bundle.Username}' verificato, ma non ci sono OPKs disponibili | X3DH fallback attivato";
                }

                if (_sessionPassword is null)
                    throw new InvalidOperationException("La password della sessione non è disponibile.");

                await SaveVaultAsync(_sessionPassword);

                RecipientFingerprintTextBlock.Text = formattedFingerprint;
                DropZoneBorder.IsEnabled = true;
                DropZonePromptTextBlock.Text = DropZoneReadyText;
            }
            catch (Exception ex)
            {
                RecipientStatusTextBlock.Foreground = System.Windows.Media.Brushes.IndianRed;
                RecipientStatusTextBlock.Text = GetUserFacingError(ex);
            }
            finally
            {
                CheckRecipientButton.IsEnabled = true;
                CheckRecipientButton.Content = "Check";
            }
        }


        private bool EnsureRecipientTrusted(
            PreKeyBundleResponse bundle,
            string formattedFingerprint)
        {
            if (_vault is null)
                throw new InvalidOperationException("Il vault crittografico non è stato caricato.");

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
                    windowTitle: "Verifica Trust On First Use",
                    heading: "Prima verifica del destinatario",
                    message: $"È la prima volta che contatti '{bundle.Username}'. Verifica che la fingerprint appartenga realmente al destinatario prima di procedere.",
                    fingerprintLabel: "Fingerprint del destinatario",
                    formattedFingerprint: formattedFingerprint,
                    verificationText: "Confronta la fingerprint tramite un canale di comunicazione separato. Ti fidi del destinatario?",
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
                    windowTitle: "Trasferimento bloccato",
                    heading: "Il trasferimento è stato bloccato",
                    message: $"Le chiavi di identità di '{bundle.Username}' sono cambiate. " +
                             "Questo potrebbe indicare il ripristino di un dispositivo o un attacco man-in-the-middle.",
                    fingerprintLabel: "Nuova fingerprint rilevata",
                    formattedFingerprint: formattedFingerprint,
                    verificationText: "Esegui nuovamente la verifica della fingerprint tramite un canale di comunicazione separato. " +
                                      "Ti fidi del destinatario?",
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

        private bool ShowFingerprintTrustDialog(
            string windowTitle,
            string heading,
            string message,
            string fingerprintLabel,
            string formattedFingerprint,
            string verificationText,
            bool isWarning)
        {
            var dialog = new Window
            {
                Title = windowTitle,
                Owner = this,
                Width = 720,
                Height = 520,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = (System.Windows.Media.Brush)FindResource("AppBackgroundBrush"),
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush")
            };
            dialog.SourceInitialized += (_, _) => ApplyNativeTitleBarColors(dialog);

            var root = new Grid
            {
                Margin = new Thickness(32)
            };

            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = new TextBlock
            {
                Text = heading,
                FontSize = 26,
                FontWeight = FontWeights.SemiBold,
                Foreground = isWarning
                    ? System.Windows.Media.Brushes.IndianRed
                    : (System.Windows.Media.Brush)FindResource("PrimaryBrush")
            };

            var warning = new TextBlock
            {
                Text = message,
                Margin = new Thickness(0, 16, 0, 22),
                FontSize = 17,
                LineHeight = 25,
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
                Text = formattedFingerprint,
                Height = 88,
                Margin = new Thickness(0, 8, 0, 20),
                Padding = new Thickness(14, 10, 14, 10),
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
                FontSize = 17,
                LineHeight = 25,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush")
            };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 24, 0, 0)
            };

            var rejectButton = new Button
            {
                Content = "Non mi fido",
                Width = 140,
                Height = 44,
                FontSize = 15,
                IsCancel = true,
                Background = System.Windows.Media.Brushes.Transparent,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush")
            };

            var trustButton = new Button
            {
                Content = "Mi fido",
                Width = 140,
                Height = 44,
                Margin = new Thickness(12, 0, 0, 0),
                FontSize = 15,
                Background = (System.Windows.Media.Brush)FindResource("PrimaryBrush"),
                Foreground = System.Windows.Media.Brushes.White,
                BorderBrush = (System.Windows.Media.Brush)FindResource("PrimaryBrush")
            };

            rejectButton.Click += (_, _) => dialog.DialogResult = false;
            trustButton.Click += (_, _) => dialog.DialogResult = true;

            buttons.Children.Add(rejectButton);
            buttons.Children.Add(trustButton);

            Grid.SetRow(title, 0);
            Grid.SetRow(warning, 1);
            Grid.SetRow(fingerprintLabelTextBlock, 2);
            Grid.SetRow(fingerprintBox, 3);
            Grid.SetRow(verification, 4);
            Grid.SetRow(buttons, 5);

            root.Children.Add(title);
            root.Children.Add(warning);
            root.Children.Add(fingerprintLabelTextBlock);
            root.Children.Add(fingerprintBox);
            root.Children.Add(verification);
            root.Children.Add(buttons);

            dialog.Content = root;

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
                    session.Ed25519PublicKey));

            string vaultFingerprint = _fingerprintService.FormatFingerprint(
                _fingerprintService.ComputeFingerprint(
                    session.Username,
                    vault.X25519IdentityPublicKey,
                    vault.Ed25519IdentityPublicKey));

            OwnIdentityRecoveryAction selectedAction = OwnIdentityRecoveryAction.Cancel;

            var dialog = new Window
            {
                Title = "Possibile attacco MITM",
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
                Text = "Le tue chiavi pubbliche sono cambiate",
                FontSize = 27,
                FontWeight = FontWeights.SemiBold,
                Foreground = System.Windows.Media.Brushes.IndianRed
            };

            var message = new TextBlock
            {
                Text = "Le chiavi pubbliche associate al tuo account sul server non corrispondono " +
                       "alle chiavi contenute nel vault cifrato locale. Potrebbe trattarsi di un " +
                       "attacco man-in-the-middle.",
                Margin = new Thickness(0, 20, 0, 0),
                FontSize = 18,
                LineHeight = 27,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush")
            };

            var action = new TextBlock
            {
                Text = "Puoi ripristinare sul server le chiavi originali conservate nel vault oppure " +
                       "eliminare la vecchia identità e crearne una nuova. Se scegli una nuova identità, " +
                       "i tuoi contatti dovranno verificarne nuovamente la fingerprint.",
                Margin = new Thickness(0, 18, 0, 0),
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.IndianRed
            };

            var fingerprints = new TextBlock
            {
                Text = $"Fingerprint nel vault:\n{vaultFingerprint}\n\n" +
                       $"Fingerprint pubblicata dal server:\n{serverFingerprint}",
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
                Content = "Annulla accesso",
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
                Content = "Nuova identità",
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
                Content = "Ripristina dal vault",
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
                MessageBoxResult confirmation = MessageBox.Show(
                    dialog,
                    "La precedente identità privata verrà eliminata dal vault. " +
                    "I trasferimenti destinati alle vecchie chiavi potrebbero non essere più decifrabili " +
                    "e i contatti dovranno verificare la nuova fingerprint. Continuare?",
                    "Crea nuova identità crittografica",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (confirmation != MessageBoxResult.Yes)
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

            dialog.Content = root;
            dialog.ShowDialog();

            return selectedAction;
        }

        private async Task RecoverOwnIdentityAsync(
            OwnIdentityRecoveryAction recoveryAction,
            string password)
        {
            if (_session is null || _vault is null)
                throw new InvalidOperationException("La sessione o il vault non sono disponibili per il ripristino dell'identità.");

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
                ?? throw new InvalidOperationException("Non è stato possibile generare una signed pre-key.");

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
                ? "È stata creata e pubblicata una nuova identità crittografica."
                : "L’identità del vault è stata ripubblicata sul server.";

            MessageBox.Show(
                this,
                operation + " Il server contiene ora una nuova SPK e 25 OPK non utilizzate.",
                "Identità ripristinata",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
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
                Title = "Integrità del file compromessa",
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
                Text = "Possibile manomissione rilevata",
                FontSize = 27,
                FontWeight = FontWeights.SemiBold,
                Foreground = System.Windows.Media.Brushes.IndianRed
            };

            var message = new TextBlock
            {
                Text = "La verifica di autenticità AES-GCM non è riuscita. Il contenuto cifrato " +
                       "potrebbe essere stato modificato durante la permanenza sul server o durante il trasferimento.",
                Margin = new Thickness(0, 20, 0, 0),
                FontSize = 18,
                LineHeight = 27,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush")
            };

            var action = new TextBlock
            {
                Text = removedFromServer
                    ? "Per la tua sicurezza il file è stato scartato dal dispositivo. " +
                      "Il server è stato avvisato e ha eliminato definitivamente il trasferimento cifrato."
                    : "Per la tua sicurezza il file è stato scartato dal dispositivo. " +
                      "Non è stato possibile confermare l’eliminazione dal server: riprova quando la connessione è disponibile.",
                Margin = new Thickness(0, 18, 0, 0),
                FontSize = 19,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.IndianRed
            };

            var closeButton = new Button
            {
                Content = "Chiudi",
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

            dialog.Content = root;
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

            SelectFilesFromDialog();
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
                Title = "Select files to send",
                Multiselect = true,
                CheckFileExists = true
            };

            bool? result = dialog.ShowDialog();

            if (result != true)
                return;

            SetSelectedFiles(dialog.FileNames);
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
                string displayText = $"{fileInfo.Name} - {FormatFileSize(fileInfo.Length)}";
                SelectedFilesListBox.Items.Add(displayText);
                SelectedFilesItemsControl.Items.Add(displayText);
            }

            SelectedFilesStatusTextBlock.Text = _selectedFilePaths.Count == 1 ? $" 1 file selezionato." : $" {_selectedFilePaths.Count} file selezionati.";
            DropZonePromptTextBlock.Text = _selectedFilePaths.Count == 0
                ? DropZoneReadyText
                : DropZoneSelectedFilesText;
            SetSendButtonEnabled(_selectedFilePaths.Count > 0);
            ResetProgress(SendProgressBar);
        }

        private void SetSendButtonEnabled(bool isEnabled)
        {
            SendEncryptedButton.IsEnabled = isEnabled;
            ApplyActionButtonVisual(SendEncryptedButton, isEnabled);
        }

        private void SetDownloadButtonEnabled(bool isEnabled)
        {
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
                button.Foreground = System.Windows.Media.Brushes.White;
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

            TrustedRecipientsComboBox.Items.Add("Contatti fidati");

            foreach (TrustedContact contact in GetVisibleTrustedContacts())
            {
                TrustedRecipientsComboBox.Items.Add(contact.Username);
            }

            TrustedRecipientsComboBox.SelectedIndex = 0;
            _isRefreshingTrustedRecipients = false;
        }

        private void TrustedRecipientsComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_isRefreshingTrustedRecipients)
                return;

            if (TrustedRecipientsComboBox.SelectedItem is not string selectedUsername)
                return;

            if (selectedUsername == "Contatti fidati")
                return;

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
            DropZoneBorder.IsEnabled = false;
            DropZonePromptTextBlock.Text = DropZoneDisabledText;
            SetSendButtonEnabled(false);
            RecipientFingerprintTextBlock.Text = string.Empty;
            RecipientStatusTextBlock.Foreground = System.Windows.Media.Brushes.IndianRed;
            RecipientStatusTextBlock.Text = "Destinatario cambiato, ricontrollare";
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

            try
            {
                _isApplyingSendToSelfMode = true;

                RecipientUsernameTextBox.IsEnabled = !sendToSelf;
                TrustedRecipientsComboBox.IsEnabled = !sendToSelf;
                _currentRecipientBundle = null;
                DropZoneBorder.IsEnabled = false;
                DropZonePromptTextBlock.Text = DropZoneDisabledText;
                SetSendButtonEnabled(false);
                RecipientStatusTextBlock.Text = string.Empty;
                RecipientFingerprintTextBlock.Text = string.Empty;

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
                RecipientStatusTextBlock.Text = "Devi effettuare il login prima di inviare file a te stesso.";
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
                SelectedFilesStatusTextBlock.Text = "Devi prima effettuare l'accesso.";
                return;
            }

            if (_vault is null)
            {
                SelectedFilesStatusTextBlock.Text = "Il vault crittografico non è stato caricato.";
                return;
            }

            if (_currentRecipientBundle is null)
            {
                SelectedFilesStatusTextBlock.Text = "Controlla e verifica prima il destinatario.";
                return;
            }

            if (_selectedFilePaths.Count == 0)
            {
                SelectedFilesStatusTextBlock.Text = "Seleziona almeno un file.";
                return;
            }

            try
            {
                SetSendButtonEnabled(false);
                SendEncryptedButton.Content = "Cifro..";
                SelectedFilesStatusTextBlock.Foreground = System.Windows.Media.Brushes.LightGreen;
                SelectedFilesStatusTextBlock.Text = "Creo il secret X3DH..";
                int totalSendSteps = (_selectedFilePaths.Count * 2) + 1;
                int completedSendSteps = 0;
                ShowProgress(SendProgressBar, totalSendSteps);

                X3dhPreKeyBundle recipientBundle = ToX3dhPreKeyBundle(_currentRecipientBundle);

                X3dhSecretResult x3dhResult = _x3dhService.CreateSenderSecret(
                    new X3dhSenderSecretInput(
                        SenderUsername: _session.Username,
                        SenderX25519IdentityPrivateKey: _vault.X25519IdentityPrivateKey,
                        SenderX25519IdentityPublicKey: _vault.X25519IdentityPublicKey,
                        SenderEd25519IdentityPublicKey: _vault.Ed25519IdentityPublicKey,
                        RecipientBundle: recipientBundle));

                byte[] serializedX3dhHeader = _x3dhHeaderSerializer.Serialize(x3dhResult.Header);

                for (int fileIndex = 0; fileIndex < _selectedFilePaths.Count; fileIndex++)
                {
                    string filePath = _selectedFilePaths[fileIndex];
                    var fileInfo = new FileInfo(filePath);

                    SelectedFilesStatusTextBlock.Text = $"Cifratura di {fileInfo.Name}...";

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

                SelectedFilesStatusTextBlock.Text = "Avvio trasferimento...";

                var createTransferRequest = new CreateTransferRequest
                {
                    RecipientUsername = _currentRecipientBundle.Username,
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

                    SelectedFilesStatusTextBlock.Text = $"Caricamento file {encryptedFile.FileIndex + 1}/{pendingEncryptedFiles.Count}...";

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
                    $"Inviato | Scade tra 24 ore > {createTransferResponse.ExpiresAtUtc:yyyy-MM-dd HH:mm:ss} UTC.";

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

                SendEncryptedButton.Content = "Invia";
            }
        }

        private async Task SaveVaultAsync(string password)
        {
            if (_session is null || _vault is null)
                throw new InvalidOperationException("La sessione o il vault crittografico non sono disponibili.");

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
            InboxStatusTextBlock.Foreground = System.Windows.Media.Brushes.IndianRed;

            if (_session is null)
            {
                InboxStatusTextBlock.Text = "Devi prima effettuare l'accesso.";
                return;
            }

            if (_vault is null)
            {
                InboxStatusTextBlock.Text = "Il vault crittografico non è stato caricato.";
                return;
            }

            if (_sessionPassword is null)
            {
                InboxStatusTextBlock.Text = "La password della sessione non è disponibile.";
                return;
            }

            if (InboxListBox.SelectedIndex < 0 || InboxListBox.SelectedIndex >= _inboxTransfers.Count)
            {
                InboxStatusTextBlock.Text = "Seleziona prima un trasferimento.";
                return;
            }

            string outputDirectory;

            if (UseDefaultDownloadDirectoryCheckBox.IsChecked == true)
            {
                outputDirectory = GetDefaultDownloadDirectoryFromTextBox();

                if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
                {
                    InboxStatusTextBlock.Text = "Default directory non valida. Scegli una directory valida o disattiva l'opzione.";
                    return;
                }
            }
            else
            {
                var folderDialog = new OpenFolderDialog
                {
                    Title = "Scegli dove salvare i file decifrati"
                };

                if (folderDialog.ShowDialog() != true)
                    return;

                outputDirectory = folderDialog.FolderName;
            }

            TransferSummaryResponse selectedTransfer = _inboxTransfers[InboxListBox.SelectedIndex];
            ClientOneTimePreKeyState? transferOneTimePreKey = null;

            try
            {
                SetDownloadButtonEnabled(false);
                DownloadDecryptButton.Content = "Decifro..";
                InboxStatusTextBlock.Foreground = System.Windows.Media.Brushes.LightGreen;
                InboxStatusTextBlock.Text = "Carico dettagli di trasferimento...";
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
                    throw new InvalidOperationException("Il trasferimento usa una signed pre-key che non è più disponibile nel vault locale.");

                ClientOneTimePreKeyState? oneTimePreKey = null;

                if (x3dhHeader.RecipientOneTimePreKeyId is not null)
                {
                    oneTimePreKey = _vault.OneTimePreKeys.SingleOrDefault(key =>
                        key.KeyId == x3dhHeader.RecipientOneTimePreKeyId.Value);

                    if (oneTimePreKey is null)
                        throw new InvalidOperationException("Il trasferimento usa una one-time pre-key che non è più disponibile nel vault locale.");
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
                    Path.GetTempPath(),
                    "Nox",
                    $"download-{Guid.NewGuid():N}");

                Directory.CreateDirectory(stagingDirectory);

                var stagedFiles = new List<(string StagedPath, string OutputPath)>();
                var reservedOutputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    foreach (TransferFileResponse file in transferDetail.Files.OrderBy(file => file.FileIndex))
                    {
                        InboxStatusTextBlock.Text = $"Scarico {file.FileIndex + 1}/{transferDetail.Files.Count}...";

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

                        string tempEncryptedFilePath = Path.Combine(
                            stagingDirectory,
                            $"{file.FileId:N}.encrypted");
                        string tempPlaintextFilePath = Path.Combine(
                            stagingDirectory,
                            $"{file.FileId:N}.plaintext");

                        await using (var tempEncryptedFileStream = new FileStream(
                            tempEncryptedFilePath,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None,
                            bufferSize: 1024 * 64,
                            useAsync: true))
                        {
                            await _apiClient.DownloadTransferFileContentToAsync(
                                transferDetail.TransferId,
                                file.FileId,
                                tempEncryptedFileStream);
                        }

                        completedInboxSteps++;
                        UpdateProgress(InboxProgressBar, completedInboxSteps);

                        await using (var encryptedStream = new FileStream(
                            tempEncryptedFilePath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            bufferSize: 1024 * 64,
                            useAsync: true))
                        await using (var plaintextStream = new FileStream(
                            tempPlaintextFilePath,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None,
                            bufferSize: 1024 * 64,
                            useAsync: true))
                        {
                            await _fileContentEncryptionService.DecryptAsync(
                                encryptedStream,
                                plaintextStream,
                                fileKeys.FileKey);
                        }

                        stagedFiles.Add((tempPlaintextFilePath, outputPath));

                        completedInboxSteps++;
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

                if (oneTimePreKey is not null)
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(
                        oneTimePreKey.PrivateKey);
                    _vault.OneTimePreKeys.Remove(oneTimePreKey);
                    await SaveVaultAsync(_sessionPassword);
                }

                await _apiClient.MarkTransferDownloadedAsync(transferDetail.TransferId);

                completedInboxSteps++;
                UpdateProgress(InboxProgressBar, completedInboxSteps);

                InboxStatusTextBlock.Text = "File scaricato, decifrato e rimosso dal server.";

                await RefreshInboxAsync();
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
                    ? "File manomesso: scartato localmente ed eliminato dal server."
                    : "File manomesso: scartato localmente; eliminazione server non confermata.";
                ShowDataTamperingWarning(removedFromServer);

                if (removedFromServer)
                    await RefreshInboxAsync();
            }
            catch (Exception ex)
            {
                InboxStatusTextBlock.Foreground = System.Windows.Media.Brushes.IndianRed;
                InboxStatusTextBlock.Text = GetUserFacingError(ex);
            }
            finally
            {
                SetDownloadButtonEnabled(InboxListBox.SelectedIndex >= 0);
                DownloadDecryptButton.Content = "Download";
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

        private async Task RefreshInboxAsync()
        {
            if (_session is null)
            {
                InboxStatusTextBlock.Foreground = System.Windows.Media.Brushes.IndianRed;
                InboxStatusTextBlock.Text = "Devi fare login";
                UpdateInboxIndicator();
                return;
            }

            SetInboxUpdatingStatus();
            ResetProgress(InboxProgressBar);

            List<TransferSummaryResponse> transfers = await _apiClient.GetInboxAsync();
            Guid? selectedTransferId = null;

            if (InboxListBox.SelectedIndex >= 0 && InboxListBox.SelectedIndex < _inboxTransfers.Count)
                selectedTransferId = _inboxTransfers[InboxListBox.SelectedIndex].TransferId;

            if (!AreInboxTransfersEqual(_inboxTransfers, transfers))
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
            InboxStatusTextBlock.Foreground = System.Windows.Media.Brushes.LightGreen;
            InboxStatusTextBlock.Text = transfers.Count == 0
                ? string.Empty
                : transfers.Count == 1
                    ? "1 trasferimento in attesa"
                    : $"{transfers.Count} trasferimenti in attesa";
            UpdateInboxIndicator();
        }

        private void SetInboxUpdatingStatus()
        {
            InboxStatusTextBlock.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(245, 158, 11));
            InboxStatusTextBlock.Text = "Aggiornamento..";
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
                    ? "Me"
                    : transfer.SenderUsername;

            return $"{senderDisplayName} - {transfer.FileCount} file(s) - SCADE: {transfer.ExpiresAtUtc:yyyy-MM-dd HH:mm} UTC";
        }

        private static string FormatFileSize(long bytes)
        {
            string[] units = ["bytes", "KB", "MB", "GB", "TB"];
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
            try
            {
                RefreshInboxButton.IsEnabled = false;
                RefreshInboxButton.Content = "Aggiornamento...";

                await RefreshInboxAsync();
            }
            catch (Exception ex)
            {
                InboxStatusTextBlock.Foreground = System.Windows.Media.Brushes.IndianRed;
                InboxStatusTextBlock.Text = GetUserFacingError(ex);
            }
            finally
            {
                RefreshInboxButton.IsEnabled = true;
                RefreshInboxButton.Content = "Aggiorna";
            }
        }

        private void TopTrustedContactsButton_Click(object sender, RoutedEventArgs e)
        {
            ShowTrustedContactsDialog();
        }

        private void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBoxResult result = MessageBox.Show(
                "Vuoi effettuare il logout?",
                "Logout",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            Logout();
        }

        private void Logout()
        {
            _inboxPollingTimer.Stop();
            _isInboxPollingRefreshRunning = false;

            _session = null;
            _sessionPassword = null;
            _vault = null;
            _currentRecipientBundle = null;
            _selectedFilePaths.Clear();
            _inboxTransfers.Clear();
            _apiClient.ClearAuthentication();

            SignedInUserTextBlock.Text = "Signed in";
            RecipientUsernameTextBox.Text = string.Empty;
            ApplyRecipientPlaceholderIfEmpty();
            RecipientStatusTextBlock.Text = string.Empty;
            RecipientFingerprintTextBlock.Text = string.Empty;
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
            ApplyScaledWindowSize(AuthDesignWidth, AuthDesignHeight);
        }

        private void TopMyIdentityButton_Click(object sender, RoutedEventArgs e)
        {
            ShowMyIdentityDialog();
        }

        private void ShowMyIdentityDialog()
        {
            if (_session is null || _vault is null)
            {
                MessageBox.Show(
                    "L'identità crittografica non è ancora stata caricata.",
                    "Identità",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            byte[] fingerprint = _fingerprintService.ComputeFingerprint(
                _session.Username,
                _vault.X25519IdentityPublicKey,
                _vault.Ed25519IdentityPublicKey);

            string shortFingerprint = _fingerprintService.FormatFingerprint(fingerprint, bytesToShow: 8);
            string fullFingerprint = _fingerprintService.FormatFingerprint(fingerprint);

            var dialog = new Window
            {
                Title = "Identity",
                Owner = this,
                Width = 560,
                Height = 390,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = (System.Windows.Media.Brush)FindResource("AppBackgroundBrush"),
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush")
            };
            dialog.SourceInitialized += (_, _) => ApplyNativeTitleBarColors(dialog);

            var root = new StackPanel
            {
                Margin = new Thickness(24)
            };

            root.Children.Add(new TextBlock
            {
                Text = "Identity",
                FontSize = 24,
                FontWeight = FontWeights.SemiBold,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush")
            });

            root.Children.Add(new TextBlock
            {
                Text = _session.Username,
                Margin = new Thickness(0, 6, 0, 20),
                FontSize = 15,
                Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush")
            });

            root.Children.Add(new TextBlock
            {
                Text = "Short check code",
                FontSize = 13,
                Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush")
            });

            root.Children.Add(new TextBox
            {
                Text = shortFingerprint,
                Margin = new Thickness(0, 6, 0, 18),
                FontSize = 24,
                FontWeight = FontWeights.SemiBold,
                IsReadOnly = true,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(17, 24, 39)),
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush")
            });

            root.Children.Add(new TextBlock
            {
                Text = "Full fingerprint",
                FontSize = 13,
                Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush")
            });

            root.Children.Add(new TextBox
            {
                Text = fullFingerprint,
                Margin = new Thickness(0, 6, 0, 18),
                FontSize = 15,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(17, 24, 39)),
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush")
            });

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var copyButton = new Button
            {
                Content = "Copia",
                Height = 38,
                Padding = new Thickness(18, 0, 18, 0),
                Background = (System.Windows.Media.Brush)FindResource("PrimaryBrush"),
                Foreground = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(0)
            };

            copyButton.Click += (_, _) =>
            {
                Clipboard.SetText(
                    $"Username: {_session.Username}{Environment.NewLine}" +
                    $"Short check code: {shortFingerprint}{Environment.NewLine}" +
                    $"Full fingerprint: {fullFingerprint}");
            };

            var closeButton = new Button
            {
                Content = "Chiudi",
                Height = 38,
                Margin = new Thickness(10, 0, 0, 0),
                Padding = new Thickness(18, 0, 18, 0),
                Background = System.Windows.Media.Brushes.Transparent,
                Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush")
            };

            closeButton.Click += (_, _) => dialog.Close();

            buttons.Children.Add(copyButton);
            buttons.Children.Add(closeButton);
            root.Children.Add(buttons);

            dialog.Content = root;
            dialog.ShowDialog();
        }

        private void ShowTrustedContactsDialog()
        {
            var dialog = new Window
            {
                Title = "Contatti fidati",
                Owner = this,
                Width = 560,
                Height = 420,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = (System.Windows.Media.Brush)FindResource("AppBackgroundBrush"),
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush")
            };
            dialog.SourceInitialized += (_, _) => ApplyNativeTitleBarColors(dialog);

            var root = new Grid
            {
                Margin = new Thickness(22)
            };

            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = new TextBlock
            {
                Text = "Contatti fidati",
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush")
            };

            var listBox = new System.Windows.Controls.ListBox
            {
                Margin = new Thickness(0, 16, 0, 14),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(17, 24, 39)),
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush")
            };

            var footer = new DockPanel();

            var status = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush")
            };

            var removeButton = new Button
            {
                Content = "Rimuovi contatto",
                Width = 150,
                Height = 38,
                IsEnabled = false,
                Background = System.Windows.Media.Brushes.Transparent,
                Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush")
            };

            DockPanel.SetDock(removeButton, Dock.Right);
            footer.Children.Add(removeButton);
            footer.Children.Add(status);

            Grid.SetRow(title, 0);
            Grid.SetRow(listBox, 1);
            Grid.SetRow(footer, 2);

            root.Children.Add(title);
            root.Children.Add(listBox);
            root.Children.Add(footer);

            dialog.Content = root;

            List<TrustedContact> orderedContacts = [];

            void RefreshDialogList()
            {
                listBox.Items.Clear();
                orderedContacts = GetVisibleTrustedContacts();

                foreach (TrustedContact contact in orderedContacts)
                {
                    listBox.Items.Add($"{contact.Username} - {contact.Fingerprint}");
                }

                status.Text = orderedContacts.Count == 0
                    ? "Nessun contatto verificato"
                    : orderedContacts.Count == 1 ? "1 Contatto fidato" : $"{orderedContacts.Count} Contatti fidati.";

                removeButton.IsEnabled = false;
            }

            listBox.SelectionChanged += (_, _) =>
            {
                removeButton.IsEnabled = listBox.SelectedIndex >= 0;
            };

            removeButton.Click += async (_, _) =>
            {
                if (_vault is null || _sessionPassword is null)
                    return;

                if (listBox.SelectedIndex < 0 || listBox.SelectedIndex >= orderedContacts.Count)
                    return;

                TrustedContact selectedContact = orderedContacts[listBox.SelectedIndex];

                MessageBoxResult result = MessageBox.Show(
                    $"Rimuovi contatto verificato '{selectedContact.Username}'?",
                    "Rimuovi contatto verificato",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                    return;

                _vault.TrustedContacts.RemoveAll(contact =>
                    string.Equals(contact.Username, selectedContact.Username, StringComparison.OrdinalIgnoreCase));

                await SaveVaultAsync(_sessionPassword);

                RefreshTrustedRecipientsComboBox();
                RefreshDialogList();
            };

            RefreshDialogList();
            dialog.ShowDialog();
        }

        private async void SidebarIdentityButton_Click(object sender, RoutedEventArgs e)
        {
            SidebarDetailPanel.Visibility = Visibility.Visible;
            TrustedContactsListBox.Visibility = Visibility.Collapsed;
            RemoveTrustedContactButton.Visibility = Visibility.Collapsed;

            SidebarDetailTitleTextBlock.Text = "Identity & Prekeys";
            SidebarDetailStatusTextBlock.Foreground = System.Windows.Media.Brushes.LightGreen;
            SidebarDetailStatusTextBlock.Text = "Loading prekey status...";

            if (_vault is null)
            {
                SidebarDetailStatusTextBlock.Foreground = System.Windows.Media.Brushes.IndianRed;
                SidebarDetailStatusTextBlock.Text = "Il vault crittografico non è stato caricato.";
                return;
            }

            try
            {
                PreKeyStatusResponse status = await _apiClient.GetPreKeyStatusAsync();

                int localAvailableOpks = _vault.OneTimePreKeys.Count(key => key.IsUploaded && !key.IsUsed);

                SidebarDetailStatusTextBlock.Text =
                    $"Signed prekey: {(status.HasSignedPreKey ? $"available, id {status.SignedPreKeyId}" : "missing")}\n" +
                    $"Server available OPKs: {status.AvailableOneTimePreKeys}\n" +
                    $"Local available OPKs: {localAvailableOpks}\n" +
                    $"Next local OPK id: {_vault.NextOneTimePreKeyId}";
            }
            catch (Exception ex)
            {
                SidebarDetailStatusTextBlock.Foreground = System.Windows.Media.Brushes.IndianRed;
                SidebarDetailStatusTextBlock.Text = GetUserFacingError(ex);
            }
        }

        private void SidebarTrustedButton_Click(object sender, RoutedEventArgs e)
        {
            SidebarDetailPanel.Visibility = Visibility.Visible;
            TrustedContactsListBox.Visibility = Visibility.Visible;
            RemoveTrustedContactButton.Visibility = Visibility.Visible;

            SidebarDetailTitleTextBlock.Text = "Contatti fidati";
            SidebarDetailStatusTextBlock.Foreground = System.Windows.Media.Brushes.LightGreen;

            RefreshTrustedContactsList();
        }

        private void RefreshTrustedContactsList()
        {
            TrustedContactsListBox.Items.Clear();
            RemoveTrustedContactButton.IsEnabled = false;

            if (_vault is null)
            {
                SidebarDetailStatusTextBlock.Foreground = System.Windows.Media.Brushes.IndianRed;
                SidebarDetailStatusTextBlock.Text = "Vault non caricato.";
                return;
            }

            List<TrustedContact> visibleContacts = GetVisibleTrustedContacts();

            foreach (TrustedContact contact in visibleContacts)
            {
                TrustedContactsListBox.Items.Add($"{contact.Username} - {contact.Fingerprint}");
            }

            SidebarDetailStatusTextBlock.Foreground = System.Windows.Media.Brushes.LightGreen;
            SidebarDetailStatusTextBlock.Text = visibleContacts.Count == 0
                ? "Nessun contatto verificato ancora."
                : $"{visibleContacts.Count} contatti verificati.";
        }

        private void TrustedContactsListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            RemoveTrustedContactButton.IsEnabled = TrustedContactsListBox.SelectedIndex >= 0;
        }

        private async void RemoveTrustedContactButton_Click(object sender, RoutedEventArgs e)
        {
            if (_vault is null || _sessionPassword is null)
                return;

            if (TrustedContactsListBox.SelectedIndex < 0)
                return;

            List<TrustedContact> orderedContacts = GetVisibleTrustedContacts();

            if (TrustedContactsListBox.SelectedIndex >= orderedContacts.Count)
                return;

            TrustedContact selectedContact = orderedContacts[TrustedContactsListBox.SelectedIndex];

            MessageBoxResult result = MessageBox.Show(
                $"Rimuovi contatto verificato '{selectedContact.Username}'?",
                "Rimuovi contatto verificato",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            _vault.TrustedContacts.RemoveAll(contact =>
                string.Equals(contact.Username, selectedContact.Username, StringComparison.OrdinalIgnoreCase));

            await SaveVaultAsync(_sessionPassword);

            RefreshTrustedRecipientsComboBox();
            RefreshTrustedContactsList();
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
                    "Impossibile comunicare con il server. Controlla la connessione e l'indirizzo configurato.",
                TaskCanceledException =>
                    "La richiesta ha impiegato troppo tempo ed è stata annullata. Riprova.",
                UriFormatException =>
                    "L'indirizzo del server non è valido.",
                System.Text.Json.JsonException =>
                    "Il server ha restituito dati in un formato non valido.",
                IOException =>
                    "Non è stato possibile leggere o scrivere il file.",
                System.Security.Cryptography.AuthenticationTagMismatchException =>
                    "La verifica crittografica dei dati non è riuscita.",
                InvalidOperationException when !string.IsNullOrWhiteSpace(exception.Message) =>
                    exception.Message,
                ArgumentException when !string.IsNullOrWhiteSpace(exception.Message) =>
                    exception.Message,
                _ => "Si è verificato un errore imprevisto. Riprova."
            };
        }


        private static string GetAvailableFilePath(
            string path,
            ISet<string>? reservedPaths = null)
        {
            if (!File.Exists(path) && reservedPaths?.Contains(path) != true)
                return path;

            string directory = Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException("Directory di output non valida.");

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
