// Ported from Client.Windows/MainWindow.xaml.cs: same flows and checks, Avalonia controls.
// Platform-specific members (dialogs, file pickers, drag and drop, layout) are in MainWindow.Platform.cs.
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Client.Core;
using Client.Core.Interfaces;
using Client.Core.Models;
using Client.Core.Services;
using CryptoEngine.Interfaces;
using CryptoEngine.Models;
using CryptoEngine.Services;
using Shared.DTO;
using Shared.DTO.Auth;
using Shared.DTO.Transfers;
using Shared.DTO.X3DH;

namespace Client.MacOS
{
    public partial class MainWindow
    {
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

        private async void SignInButton_Click(object sender, RoutedEventArgs e)
        {
            AuthErrorTextBlock.Text = string.Empty;
            AuthErrorTextBlock.Foreground = Brushes.IndianRed;

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

            string username = (UsernameTextBox.Text ?? string.Empty).Trim();
            string password = (PasswordBox.Text ?? string.Empty);
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
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
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
                    OwnIdentityRecoveryAction recoveryAction = await ShowOwnIdentityMismatchWarningAsync(
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

                SignedInUserTextBlock.Text = currentUser.Username;
                RefreshTrustedRecipientsComboBox();

                AuthScreen.IsVisible = false;
                MainShell.IsVisible = true;
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
            string host = (ServerUrlTextBox.Text ?? string.Empty).Trim();
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
            PasswordBox.Text = string.Empty;
            PasswordLabelTextBlock.IsVisible = PasswordBox.IsVisible = exists.HasValue;
            PasswordBox.IsEnabled = exists.HasValue;
            SignInButton.IsVisible = exists != false;
            CreateAccountButton.IsVisible = exists == false;
            CreateAccountButton.IsDefault = exists == false;
            SwitchAuthModeButton.IsVisible = _isServerConnectionVerified;
            SetTextResource(SwitchAuthModeLabel, exists == false ? "NoxText.AlreadyHaveAccount" : "NoxText.NoAccount");
            SetTextResource(AuthHeading, !_isServerConnectionVerified
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
                RegistrationPasswordHint.IsVisible = false;
                RegistrationPasswordHint.Text = string.Empty;
                CreateAccountButton.IsEnabled = false;
                return;
            }

            string missing = MissingPasswordRequirements((PasswordBox.Text ?? string.Empty));
            bool valid = missing.Length == 0;
            RegistrationPasswordHint.Text = valid ? Texts.T("PasswordMeetsTheRequirements") : Texts.T("StillNeeded", missing);
            RegistrationPasswordHint.Foreground = valid ? Brushes.LightGreen
                : Res("TextMutedBrush");
            RegistrationPasswordHint.IsVisible = true;
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
                UsernameLabelTextBlock.IsVisible = UsernameTextBox.IsVisible = true;
                SetUsernameChoice(null);
                SetTextResource(AuthHeading, "NoxText.SignInAlt");
                ConnectedServerUrl.Text = serverUrl;
                ServerConnectionFields.IsVisible = false;
                ConnectedServerSummary.IsVisible = true;
                AuthErrorTextBlock.Text = ServerSchemeBox.SelectedIndex == 1
                    ? Texts.T("HTTPDoesNotEncryptCredentialsIn") : string.Empty;
                UsernameTextBox.Focus();
            }
            catch (Exception ex)
            {
                ResetServerConnectionState();
                AuthErrorTextBlock.Foreground = Brushes.IndianRed;
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
            string text = (field.Text ?? string.Empty).Trim();
            int prefix = text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? 8 :
                text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? 7 : 0;
            if (prefix != 0 && ServerSchemeBox != null)
            {
                ServerSchemeBox.SelectedIndex = prefix == 7 ? 1 : 0;
                field.Text = text[prefix..];
                field.CaretIndex = field.Text?.Length ?? 0;
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

        private void ResetServerConnectionState()
        {
            if (!_isServerConnectionVerified && UsernameTextBox?.IsVisible != true)
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
            SetTextResource(AuthHeading, "NoxText.Server");
            ServerConnectionFields.IsVisible = true;
            ConnectedServerSummary.IsVisible = false;
            ConnectedServerUrl.Text = string.Empty;
            UsernameLabelTextBlock.IsVisible = false;
            UsernameTextBox.IsVisible = false;
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
            string username = (UsernameTextBox.Text ?? string.Empty).Trim();
            string password = (PasswordBox.Text ?? string.Empty);

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
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

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

                AuthErrorTextBlock.Foreground = Brushes.LightGreen;
                SetUsernameChoice(null);
                AuthErrorTextBlock.Text = Texts.T("AccountCreatedSelectContinueToSign");
                UsernameTextBox.Focus();
            }
            catch (Exception ex)
            {
                AuthErrorTextBlock.Foreground = Brushes.IndianRed;
                AuthErrorTextBlock.Text = GetUserFacingError(ex);
            }
            finally
            {
                SetLoginInputsBusy(false);
                CreateAccountButton.Content = Texts.T("Register");
            }
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
            ClearSelectedFilePreviews();
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
                CheckRecipientLabel.Text = "...";

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

                bool trusted = await EnsureRecipientTrustedAsync(
                    bundle,
                    formattedFingerprint);


                if (!trusted)
                {
                    RecipientStatusTextBlock.Foreground = Brushes.IndianRed;
                    RecipientStatusTextBlock.Text = Texts.T("RecipientNotTrustedTransferBlocked");
                    _currentRecipientBundle = null;
                    _recipientBundleMustBeRefreshed = false;
                    return;
                }

                RecipientStatusTextBlock.Foreground = Brushes.LightGreen;

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
                RecipientStatusTextBlock.Foreground = Brushes.IndianRed;
                RecipientStatusTextBlock.Text = GetUserFacingError(ex);
            }
            finally
            {
                CheckRecipientButton.IsEnabled = true;
                CheckRecipientLabel.Text = Texts.T("Check");
            }
        }

        private async Task<bool> EnsureRecipientTrustedAsync(
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
                bool trusted = await ShowFingerprintTrustDialogAsync(
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
                bool trusted = await ShowFingerprintTrustDialogAsync(
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

        private static string FormatFingerprintRows(string fingerprint) =>
            string.Join(Environment.NewLine, fingerprint.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Chunk(FingerprintGroupsPerRow).Select(row => string.Join("  ", row)));

        private static bool ServerIdentityKeysDifferFromVault(
            ClientSession session,
            UserKeyPayload vault)
        {
            return !session.X25519PublicKey.SequenceEqual(vault.X25519IdentityPublicKey) ||
                   !session.Ed25519PublicKey.SequenceEqual(vault.Ed25519IdentityPublicKey);
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
                await ShowInfoAsync(operation + Texts.T("TheServerNowHasANew"), Texts.T("IdentityRestored"));
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

        private void RecipientUsernameTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isApplyingSendToSelfMode)
                return;

            if (IsSendToSelfEnabled())
                return;

            if (IsRecipientPlaceholderActive())
                return;

            string typedRecipientUsername = (RecipientUsernameTextBox.Text ?? string.Empty).Trim();

            if (_currentRecipientBundle is null)
                return;

            if (string.Equals(typedRecipientUsername, _currentRecipientBundle.Username, StringComparison.OrdinalIgnoreCase))
                return;

            _currentRecipientBundle = null;
            _recipientBundleMustBeRefreshed = false;
            DropZoneBorder.IsEnabled = false;
            DropZonePromptTextBlock.Text = DropZoneDisabledText;
            SetSendButtonEnabled(false);
            RecipientStatusTextBlock.Foreground = Brushes.IndianRed;
            RecipientStatusTextBlock.Text = Texts.T("RecipientChangedCheckTheRecipientAgain");
        }

        private async void SendToSelfCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            bool sendToSelf = IsSendToSelfEnabled();
            SetRecipientLocked(false);

            try
            {
                _isApplyingSendToSelfMode = true;

                RecipientUsernameTextBox.IsEnabled = !sendToSelf;
                TrustedRecipientsButton.IsEnabled = !sendToSelf;
                _currentRecipientBundle = null;
                _recipientBundleMustBeRefreshed = false;
                DropZoneBorder.IsEnabled = false;
                DropZonePromptTextBlock.Text = DropZoneDisabledText;
                SetSendButtonEnabled(false);
                RecipientStatusTextBlock.Text = string.Empty;

                if (!sendToSelf)
                {
                    RecipientUsernameTextBox.Text = string.Empty;
                    RecipientUsernameTextBox.Foreground = Res("TextPrimaryBrush");
                    ApplyRecipientPlaceholderIfEmpty();
                    return;
                }

                RecipientUsernameTextBox.Text = SendToSelfDisplayText;
                RecipientUsernameTextBox.Foreground = Res("TextPrimaryBrush");
                ClearSelectedFilePreviews();
                SelectedFilesStatusTextBlock.Text = string.Empty;
                _selectedFilePaths.Clear();
            }
            finally
            {
                _isApplyingSendToSelfMode = false;
            }

            if (_session is null)
            {
                RecipientStatusTextBlock.Foreground = Brushes.IndianRed;
                RecipientStatusTextBlock.Text = Texts.T("SignInBeforeSendingFilesTo");
                return;
            }

            if (!_suppressSendToSelfAutoCheck && sendToSelf)
                await Dispatcher.UIThread.InvokeAsync(() => CheckRecipientButton_Click(sender, new RoutedEventArgs()));
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
            else if (!await EnsureRecipientTrustedAsync(freshBundle, formattedFingerprint))
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
            SelectedFilesStatusTextBlock.Foreground = Brushes.IndianRed;
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
                SendButtonLabel.Text = Texts.T("Encrypting");
                SelectedFilesStatusTextBlock.Foreground = Brushes.LightGreen;
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
                ClearSelectedFilePreviews();
                SetSendButtonEnabled(false);
                DropZoneBorder.IsEnabled = IsCurrentRecipientStillSelected();
                DropZonePromptTextBlock.Text = DropZoneBorder.IsEnabled
                    ? DropZoneReadyText
                    : DropZoneDisabledText;
                UpdateProgress(SendProgressBar, totalSendSteps);
            }
            catch (Exception ex)
            {
                SelectedFilesStatusTextBlock.Foreground = Brushes.IndianRed;
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

                SendButtonLabel.Text = Texts.T("Send");
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

        private void InboxListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SetDownloadButtonEnabled(InboxListBox.SelectedIndex >= 0);
        }

        private async void DownloadDecryptButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isDownloading) return;
            InboxStatusTextBlock.Foreground = Brushes.IndianRed;

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
                    string? pickedDirectory = await PickFolderAsync(Texts.T("ChooseWhereToSaveTheDecrypted"), null);

                    if (pickedDirectory is null)
                        return;

                    outputDirectory = pickedDirectory;
                }

                SetDownloadButtonEnabled(false);
                DownloadDecryptButton.Content = Texts.T("Decrypting");
                InboxStatusTextBlock.Foreground = Brushes.LightGreen;
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

                InboxStatusTextBlock.Foreground = Brushes.IndianRed;
                InboxStatusTextBlock.Text = removedFromServer
                    ? Texts.T("TamperedFileDiscardedLocallyAndDeleted")
                    : Texts.T("TamperedFileDiscardedLocallyServerDeletion");
                await ShowDataTamperingWarningAsync(removedFromServer);

                if (removedFromServer)
                    await RefreshInboxAsync(allowDuringDownload: true);
            }
            catch (Exception ex)
            {
                InboxStatusTextBlock.Foreground = Brushes.IndianRed;
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
                    InboxStatusTextBlock.Foreground = Brushes.IndianRed;
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
                    InboxStatusTextBlock.Foreground = Brushes.LightGreen;
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
            InboxStatusTextBlock.Foreground = new SolidColorBrush(
                Color.FromRgb(245, 158, 11));
            InboxStatusTextBlock.Text = Texts.T("Refreshing");
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
                InboxStatusTextBlock.Foreground = Brushes.IndianRed;
                InboxStatusTextBlock.Text = GetUserFacingError(ex);
            }
            finally
            {
                _isManualInboxRefresh = false;
                RefreshInboxButton.IsEnabled = !_isDownloading;
                RefreshInboxButton.Content = Texts.T("Refresh");
            }
        }

        private async void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            bool confirmed = await ConfirmActionAsync(this,
                Texts.T("LogOutOfThisSession"),
                Texts.T("Logout"),
                Texts.T("Logout"));

            if (!confirmed)
                return;

            Logout();
        }

        private string OwnFingerprint()
        {
            if (_vault is null || _session is null) return string.Empty;
            byte[] hash = _fingerprintService.ComputeFingerprint(_session.Username, _vault.X25519IdentityPublicKey, _vault.Ed25519IdentityPublicKey);
            return _fingerprintService.FormatFingerprint(hash, hash.Length);
        }

        private void TrustedContactsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RemoveTrustedContactButton.IsEnabled = TrustedContactsListBox.SelectedIndex >= 0;
        }

        private async void RemoveTrustedContactButton_Click(object sender, RoutedEventArgs e)
        {
            if (_vault is null || _sessionPassword is null)
                return;

            if (TrustedContactsListBox.SelectedItem is not ContactRow selectedContact) return;

            bool confirmed = await ConfirmActionAsync(this,
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

        private static void ShowProgress(ProgressBar progressBar, int maximum)
        {
            progressBar.Minimum = 0;
            progressBar.Maximum = Math.Max(1, maximum);
            progressBar.Value = 0;
            progressBar.IsVisible = true;
        }

        private static void UpdateProgress(ProgressBar progressBar, int value)
        {
            progressBar.Value = Math.Min(progressBar.Maximum, Math.Max(progressBar.Minimum, value));
        }

        private static void ResetProgress(ProgressBar progressBar)
        {
            progressBar.Value = 0;
            progressBar.IsVisible = false;
        }
    }
}
