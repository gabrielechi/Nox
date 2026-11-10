// Platform part of the Avalonia client: state, window layout, language, pickers, drag and drop,
// navigation and the members whose WPF version depended on WPF-only APIs.
// The flows themselves are in MainWindow.Flows.cs (ported 1:1 from Client.Windows).
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Client.Core;
using Client.Core.Interfaces;
using Client.Core.Models;
using Client.Core.Services;
using Client.MacOS.Views;
using CryptoEngine.Interfaces;
using CryptoEngine.Services;
using Shared.DTO.Transfers;
using Shared.DTO.X3DH;

namespace Client.MacOS
{
    public partial class MainWindow : Window
    {
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
        private static string SendToSelfDisplayText => Texts.T("Me");
        private const double AuthDesignWidth = 432;
        private const double DashboardDesignWidth = 776;
        // The WPF window has a 34 px caption above the 582 px dashboard design; here the caption
        // area belongs to the window, so the same content gets the same room.
        private const double DashboardDesignHeight = 582 + 34;

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
        private bool _isServerConnectionVerified;
        private bool? _usernameExists;
        private bool _suppressRememberServerChange = true;
        private bool _isApplyingSendToSelfMode;
        private bool _suppressSendToSelfAutoCheck;
        private bool _recipientLocked;
        private bool _authRequestBusy;
        private int _currentPage;

        private readonly IX3dhService _x3dhService = new X3dhService(new KeyPairService(), new HkdfService());
        private readonly IX3dhHeaderSerializer _x3dhHeaderSerializer = new X3dhHeaderJsonSerializer();

        private readonly IFileKeyDerivationService _fileKeyDerivationService = new FileKeyDerivationService(new HkdfService());
        private readonly IFileMetadataProtector _fileMetadataProtector = new FileMetadataProtector(new AesGcmSymmetricService());
        private readonly IFileContentEncryptionService _fileContentEncryptionService = new FileContentEncryptionService(new AesGcmSymmetricService());

        private readonly List<TransferSummaryResponse> _inboxTransfers = new List<TransferSummaryResponse>();

        private CheckBox? SendToSelfCheckBoxControl => SendToSelfCheckBox;
        private MenuFlyout TrustedRecipientsFlyout => (MenuFlyout)TrustedRecipientsButton.Flyout!;
        private const int FingerprintGroupsPerRow = 4;

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
            InitializeComponent();
            var version = typeof(MainWindow).Assembly.GetName().Version!;
            ClientVersionLabel.Text = "v." + version.ToString(version.Build > 0 ? 3 : 2);

            _inboxPollingTimer.Interval = TimeSpan.FromSeconds(5);
            _inboxPollingTimer.Tick += InboxPollingTimer_Tick;

            SetSendButtonEnabled(false);
            SetDownloadButtonEnabled(false);
            LoadClientAppSettings(settings);
            _suppressRememberServerChange = false;
            InitializeAuthLayout();
            InitializePasswordStrength();
            InitializeAccountMenu();
            InitializeDropZone();
            InitializeUpdateCheck();
            TrustedRecipientsButton.IsEnabled = false;
            ShowAuthWindow();
        }

        // ---------------- resources and text ----------------

        /// <summary>Theme brush by key; also accepts the legacy WPF key names used by the ported flows.</summary>
        private IBrush Res(string key)
        {
            string mapped = key switch
            {
                "TextMutedBrush" => "NoxTextMutedBrush",
                "TextPrimaryBrush" => "NoxTextBrush",
                "AccentBrush" or "PrimaryBrush" => "NoxAccentBrush",
                "AppBackgroundBrush" => "NoxWindowBrush",
                "BorderBrush" => "NoxBorderBrush",
                _ => key
            };
            return this.TryFindResource(mapped, ActualThemeVariant, out object? value) && value is IBrush brush
                ? brush
                : Brushes.Transparent;
        }

        private static void SetTextResource(TextBlock textBlock, string key) =>
            textBlock.Bind(TextBlock.TextProperty, textBlock.GetResourceObservable(key));

        // ---------------- window layout ----------------

        private void ShowAuthWindow()
        {
            SizeToContent = SizeToContent.Height;
            Width = AuthDesignWidth;
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#14211C"), 0), new GradientStop(Color.Parse("#0E1116"), 0.7) }
            };
        }

        private void ApplyScaledWindowSize(double designWidth, double designHeight)
        {
            SizeToContent = SizeToContent.Manual;
            Width = designWidth;
            Height = designHeight;
            Background = Res("NoxWindowBrush");
        }

        // The WPF client animates its sign-in window height; Avalonia sizes it to content instead.
        private static void PrepareAuthTransition() { }
        private static void RequestAuthWindowResize(bool animate) { }
        private static void StopAuthWindowAnimation() { }

        private void ResizeAuthWindow(bool animate)
        {
            if (AuthScreen.IsVisible) ShowAuthWindow();
        }

        private void InitializeAuthLayout()
        {
            // Compact protocol box: the Fluent template keeps 32 px for the arrow; 24 px leave room for "HTTPS".
            ServerSchemeBox.TemplateApplied += (_, e) =>
            {
                if (e.NameScope.Find<PathIcon>("DropDownGlyph") is not { } glyph) return;
                glyph.Margin = new Thickness(0, 0, 8, 0);
                if (glyph.Parent is Grid { ColumnDefinitions.Count: 2 } grid)
                    grid.ColumnDefinitions[1].Width = new GridLength(24);
            };

            AuthErrorTextBlock.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBlock.TextProperty)
                    AuthNotice.IsVisible = !string.IsNullOrWhiteSpace(AuthErrorTextBlock.Text);
            };
        }

        // ---------------- settings and language ----------------

        private void LoadClientAppSettings(ClientAppSettings settings)
        {
            SyncLanguageSelectors();

            if (settings.RememberServer && !string.IsNullOrWhiteSpace(settings.RememberedServerUrl))
            {
                RememberServerCheckBox.IsChecked = true;
                ServerUrlTextBox.Text = settings.RememberedServerUrl;
            }

            UseDefaultDownloadDirectoryCheckBox.IsChecked = settings.UseDefaultDownloadDirectory;
            SetDefaultDownloadDirectoryText(settings.DefaultDownloadDirectory);
        }

        private bool _syncingLanguage;

        private void SyncLanguageSelectors()
        {
            _syncingLanguage = true;
            LoginLanguageBox.SelectedIndex = Texts.Language == "en" ? 1 : 0;
            _syncingLanguage = false;
            LanguageItCheck.Opacity = Texts.Language == "it" ? 1 : 0;
            LanguageEnCheck.Opacity = Texts.Language == "en" ? 1 : 0;
            LanguageItButton.Classes.Set("selected", Texts.Language == "it");
            LanguageEnButton.Classes.Set("selected", Texts.Language == "en");
        }

        private void LoginLanguageBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_syncingLanguage || LoginLanguageBox.SelectedItem is not ComboBoxItem { Tag: string code }) return;
            ChangeLanguage(code);
        }

        private void LanguageMenu_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string code }) ChangeLanguage(code);
        }

        private async void ChangeLanguage(string code)
        {
            if (code == Texts.Language) return;

            string directory = GetDefaultDownloadDirectoryFromTextBox();
            var settings = _appSettingsService.Load();
            settings.Language = code;
            try { _appSettingsService.Save(settings); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SyncLanguageSelectors();
                await ShowInfoAsync(Texts.T("CannotSaveLanguage"), "NOX");
                return;
            }
            Texts.Apply(code);
            SyncLanguageSelectors();
            SetDefaultDownloadDirectoryText(directory);
            foreach (var label in new[] { AuthErrorTextBlock, SignedInUserTextBlock, RecipientStatusTextBlock,
                         DropZonePromptTextBlock, SelectedFilesStatusTextBlock, InboxStatusTextBlock, ContactsStatus,
                         SendButtonLabel, CheckRecipientLabel })
            {
                string old = label.Text ?? string.Empty;
                string next = Texts.Refresh(old);
                if (next != old) label.Text = next;
            }
            foreach (var button in new[] { SignInButton, CreateAccountButton, RefreshInboxButton, DownloadDecryptButton })
                if (button.Content is string old)
                {
                    string next = Texts.Refresh(old);
                    if (next != old) button.Content = next;
                }
            UpdateRegistrationHint();
            UpdatePasswordStrengthVisuals();
            RefreshUpdateTooltip();
            int selection = InboxListBox.SelectedIndex;
            for (int i = 0; i < _inboxTransfers.Count; i++) InboxListBox.Items[i] = FormatInboxTransfer(_inboxTransfers[i]);
            InboxListBox.SelectedIndex = selection;
            if (_currentPage == 3) RefreshTrustedContactsList();
        }

        private void RememberServerCheckBox_Changed(object? sender, RoutedEventArgs e)
        {
            if (_suppressRememberServerChange || RememberServerCheckBox.IsChecked == true)
                return;

            _appSettingsService.ClearRememberedServer();
        }

        private async void ChooseDefaultDownloadDirectoryButton_Click(object? sender, RoutedEventArgs e)
        {
            string currentDirectory = GetDefaultDownloadDirectoryFromTextBox();
            string? folder = await PickFolderAsync(Texts.T("ChooseTheDefaultFolder"),
                Directory.Exists(currentDirectory) ? currentDirectory : null);
            if (folder is null)
                return;

            SetDefaultDownloadDirectoryText(folder);
            UseDefaultDownloadDirectoryCheckBox.IsChecked = true;

            ClientAppSettings settings = _appSettingsService.Load();
            settings.UseDefaultDownloadDirectory = true;
            settings.DefaultDownloadDirectory = folder;
            _appSettingsService.Save(settings);
        }

        // The folder is shown as text; the watermark covers the empty state.
        private void SetDefaultDownloadDirectoryText(string? directory) =>
            DefaultDownloadDirectoryTextBox.Text = string.IsNullOrWhiteSpace(directory) ? string.Empty : directory;

        private string GetDefaultDownloadDirectoryFromTextBox() =>
            (DefaultDownloadDirectoryTextBox.Text ?? string.Empty).Trim();

        // ---------------- pickers ----------------

        private async Task<string?> PickFolderAsync(string title, string? startDirectory)
        {
            var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
            if (startDirectory is not null)
                options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(startDirectory);

            IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(options);
            return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
        }

        private async void SelectFilesFromDialog()
        {
            IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Texts.T("SelectFilesToSendUpTo", MaximumFilesPerTransfer),
                AllowMultiple = true
            });

            if (files.Count == 0)
                return;

            SetSelectedFiles(files.Select(file => file.TryGetLocalPath()).OfType<string>());
        }

        // ---------------- drop zone ----------------

        // Drag and drop uses the Avalonia 11 IDataObject API (still supported, marked obsolete in 11.3).
#pragma warning disable CS0618

        private void InitializeDropZone()
        {
            DropZoneBorder.AddHandler(DragDrop.DragEnterEvent, DropZoneBorder_DragEnter);
            DropZoneBorder.AddHandler(DragDrop.DragOverEvent, DropZoneBorder_DragOver);
            DropZoneBorder.AddHandler(DragDrop.DragLeaveEvent, (_, _) => ResetDropZoneBackground());
            DropZoneBorder.AddHandler(DragDrop.DropEvent, DropZoneBorder_Drop);
        }

        private void DropZoneBorder_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!DropZoneBorder.IsEnabled || e.InitialPressMouseButton != MouseButton.Left)
                return;

            for (var node = e.Source as Visual; node != null && node != DropZoneBorder; node = node.GetVisualParent())
                if (node is Avalonia.Controls.Primitives.ScrollBar)
                    return;

            SelectFilesFromDialog();
        }

        private void DropZoneBorder_KeyDown(object? sender, KeyEventArgs e)
        {
            if (DropZoneBorder.IsEnabled && e.Key is Key.Enter or Key.Space)
            {
                e.Handled = true;
                SelectFilesFromDialog();
            }
        }

        private void DropZoneBorder_DragEnter(object? sender, DragEventArgs e)
        {
            if (!DropZoneBorder.IsEnabled || !e.Data.Contains(DataFormats.Files))
                return;

            DropZoneBorder.Classes.Set("dragover", true);
        }

        private void DropZoneBorder_DragOver(object? sender, DragEventArgs e)
        {
            e.DragEffects = DropZoneBorder.IsEnabled && e.Data.Contains(DataFormats.Files)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        }

        private void DropZoneBorder_Drop(object? sender, DragEventArgs e)
        {
            ResetDropZoneBackground();

            if (!DropZoneBorder.IsEnabled)
                return;

            IEnumerable<IStorageItem>? items = e.Data.GetFiles();
            if (items is null)
                return;

            SetSelectedFiles(items.Select(item => item.TryGetLocalPath()).OfType<string>());
        }

#pragma warning restore CS0618

        private void ResetDropZoneBackground() => DropZoneBorder.Classes.Set("dragover", false);

        private void SetSelectedFiles(IEnumerable<string> filePaths)
        {
            _selectedFilePaths.Clear();
            ClearSelectedFilePreviews();

            foreach (string filePath in filePaths.Where(File.Exists))
            {
                _selectedFilePaths.Add(filePath);

                var fileInfo = new FileInfo(filePath);
                SelectedFilesItemsControl.Items.Add(new SelectedFilePreview(fileInfo.Name, FormatFileSize(fileInfo.Length)));
            }
            UpdateSelectedFilesVisibility();

            bool exceedsLimit = _selectedFilePaths.Count > MaximumFilesPerTransfer;
            SelectedFilesStatusTextBlock.Foreground = exceedsLimit ? Brushes.IndianRed : Brushes.LightGreen;
            SelectedFilesStatusTextBlock.Text = exceedsLimit
                ? Texts.T("YouSelectedFilesTheLimitIs", _selectedFilePaths.Count, MaximumFilesPerTransfer)
                : Texts.T("FilesSelected", _selectedFilePaths.Count, MaximumFilesPerTransfer);
            DropZonePromptTextBlock.Text = _selectedFilePaths.Count == 0
                ? DropZoneReadyText
                : DropZoneSelectedFilesText;
            SetSendButtonEnabled(_selectedFilePaths.Count > 0 && !exceedsLimit);
            ResetProgress(SendProgressBar);
        }

        private void ClearSelectedFilePreviews()
        {
            SelectedFilesItemsControl.Items.Clear();
            UpdateSelectedFilesVisibility();
        }

        private void UpdateSelectedFilesVisibility()
        {
            bool hasFiles = SelectedFilesItemsControl.Items.Count > 0;
            SelectedFilesScroll.IsVisible = hasFiles;
            DropZonePromptPanel.IsVisible = !hasFiles;
        }

        // In WPF this forced colours set by code; here the button styles follow IsEnabled.
        private static void ApplyActionButtonVisual(Button button, bool isEnabled) { }

        // ---------------- recipient ----------------

        private void RecipientUsername_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (!_recipientLocked && CheckRecipientButton.IsEnabled && !TrustedRecipientsFlyout.IsOpen)
                CheckRecipientButton_Click(CheckRecipientButton, new RoutedEventArgs());
        }

        private void SetRecipientLocked(bool locked)
        {
            _recipientLocked = locked;
            RecipientUsernameTextBox.IsReadOnly = locked;
            RecipientUsernameTextBox.Classes.Set("locked", locked);
            TrustedRecipientsButton.IsEnabled = !locked && !IsSendToSelfEnabled() && _vault is not null;
            CheckRecipientButton.Classes.Set("primary", !locked);
            CheckRecipientButton.Classes.Set("edit", locked);
            CheckRecipientLabel.IsVisible = !locked;
            CheckRecipientPencil.IsVisible = locked;
        }

        private void RefreshTrustedRecipientsComboBox()
        {
            TrustedRecipientsFlyout.Items.Clear();

            if (_vault is null)
                return;

            foreach (TrustedContact contact in GetVisibleTrustedContacts())
            {
                var item = new MenuItem { Header = contact.Username };
                item.Click += TrustedRecipient_Click;
                TrustedRecipientsFlyout.Items.Add(item);
            }
            TrustedRecipientsButton.IsEnabled = TrustedRecipientsFlyout.Items.Count > 0 && !_recipientLocked && !IsSendToSelfEnabled();
        }

        private void TrustedRecipient_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Header: string selectedUsername })
                return;

            CheckBox? sendToSelfCheckBox = SendToSelfCheckBoxControl;
            if (sendToSelfCheckBox is not null)
                sendToSelfCheckBox.IsChecked = false;

            RecipientUsernameTextBox.Text = selectedUsername;
            CheckRecipientButton_Click(sender, new RoutedEventArgs());
        }

        // The WPF field drew its placeholder as text; Avalonia uses the TextBox watermark instead,
        // so the placeholder helpers have nothing to do and the field is empty when nothing was typed.
        private string GetRecipientUsername()
        {
            if (IsSendToSelfEnabled())
                return _session?.Username ?? string.Empty;

            return (RecipientUsernameTextBox.Text ?? string.Empty).Trim();
        }

        private static bool IsRecipientPlaceholderActive() => false;
        private static void ClearRecipientPlaceholder() { }
        private static void ApplyRecipientPlaceholderIfEmpty() { }

        // ---------------- navigation ----------------

        private void Nav_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string tag } && int.TryParse(tag, out int page))
                SelectPage(page);
        }

        private void SelectPage(int page)
        {
            _currentPage = page;
            Button[] nav = [NavSend, NavInbox, NavIdentity, NavContacts];
            Control[] pages = [SendPage, InboxPage, IdentityPage, ContactsPage];
            for (int i = 0; i < nav.Length; i++)
            {
                nav[i].Classes.Set("selected", i == page);
                pages[i].IsVisible = i == page;
            }

            if (_vault is null || _session is null) return;
            if (page == 2)
            {
                IdentityUsername.Text = _session.Username;
                IdentityFingerprint.ItemsSource = OwnFingerprint().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            }
            else if (page == 3) RefreshTrustedContactsList();
        }

        // ---------------- inbox ----------------

        private void UpdateInboxIndicator()
        {
            bool hasIncomingFiles = InboxListBox.Items.Count > 0;

            InboxPresenceIndicator.Fill = new SolidColorBrush(
                hasIncomingFiles ? Color.FromRgb(64, 217, 119) : Color.FromRgb(170, 180, 195));
            InboxListBox.IsVisible = hasIncomingFiles;
            InboxEmptyState.IsVisible = !hasIncomingFiles;
        }

        // ---------------- identity and contacts ----------------

        private sealed record ContactRow(string Username, string[] Groups);

        private async void CopyIdentity_Click(object? sender, RoutedEventArgs e)
        {
            if (_vault == null || _session == null || Clipboard is null)
                return;
            await Clipboard.SetTextAsync(Texts.T("UsernameNFullFingerprint", _session.Username, OwnFingerprint()));
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

        // ---------------- logout ----------------

        private void Logout()
        {
            ++_inboxRevision;
            _inboxPollingTimer.Stop();
            _isInboxPollingRefreshRunning = false;

            _session = null;
            SelectPage(0);
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
            RecipientStatusTextBlock.Text = string.Empty;
            TrustedRecipientsFlyout.Items.Clear();
            TrustedRecipientsButton.IsEnabled = false;

            ClearSelectedFilePreviews();
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
            AuthErrorTextBlock.Foreground = Brushes.IndianRed;
            PasswordBox.Text = string.Empty;
            ResetServerConnectionState();

            MainShell.IsVisible = false;
            AuthScreen.IsVisible = true;
            ResizeAuthWindow(false);
        }

        // ---------------- dialogs ----------------

        private Task ShowInfoAsync(string message, string title) =>
            NoxDialogs.ShowInfoAsync(this, message, title);

        private Task<bool> ConfirmActionAsync(Window owner, string message, string title, string action) =>
            NoxDialogs.ConfirmAsync(owner, message, title, action);

        private Task<bool> ShowFingerprintTrustDialogAsync(
            string windowTitle,
            string heading,
            string message,
            string fingerprintLabel,
            string formattedFingerprint,
            string verificationText,
            bool isWarning) =>
            NoxDialogs.FingerprintTrustAsync(this, windowTitle, heading, message, fingerprintLabel,
                FormatFingerprintRows, formattedFingerprint, verificationText, isWarning);

        private async Task<OwnIdentityRecoveryAction> ShowOwnIdentityMismatchWarningAsync(
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

            return await NoxDialogs.OwnIdentityMismatchAsync(this, vaultFingerprint, serverFingerprint) switch
            {
                NoxDialogs.IdentityChoice.RestoreVaultIdentity => OwnIdentityRecoveryAction.RestoreVaultIdentity,
                NoxDialogs.IdentityChoice.CreateNewIdentity => OwnIdentityRecoveryAction.CreateNewIdentity,
                _ => OwnIdentityRecoveryAction.Cancel
            };
        }

        private Task ShowDataTamperingWarningAsync(bool removedFromServer) =>
            NoxDialogs.DataTamperingAsync(this, removedFromServer);

        private sealed class PendingEncryptedFile
        {
            public int FileIndex { get; set; }
            public byte[] FileHeader { get; set; } = [];
            public string TempEncryptedFilePath { get; set; } = string.Empty;
            public long CiphertextLength { get; set; }
        }
    }
}
