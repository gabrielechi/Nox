using Client.Windows.Models;
using Shared.DTO;
using Shared.DTO.Auth;
using Shared.DTO.Transfers;
using Shared.DTO.X3DH;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Client.Windows.Services
{
    public class ApiClient
    {
        private HttpClient _httpClient = new();

        public string? Jwt { get; private set; }

        public string? ServerUrl { get; private set; }

        public void ClearAuthentication()
        {
            Jwt = null;
            _httpClient.DefaultRequestHeaders.Authorization = null;
        }

        private void ConfigureServerUrl(string serverUrl)
        {
            if (string.IsNullOrWhiteSpace(serverUrl))
                throw new ArgumentException("L'indirizzo del server è obbligatorio.", nameof(serverUrl));

            string normalizedServerUrl = serverUrl.Trim().TrimEnd('/');

            if (ServerUrl == normalizedServerUrl)
                return;

            _httpClient.Dispose();
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(normalizedServerUrl + "/")
            };

            ServerUrl = normalizedServerUrl;

            if (!string.IsNullOrWhiteSpace(Jwt))
            {
                _httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", Jwt);
            }
        }

        public async Task<ClientSession> LoginAsync(
            string serverUrl,
            string username,
            string password,
            CancellationToken cancellationToken = default)
        {
            ConfigureServerUrl(serverUrl);

            var request = new LoginRequest
            {
                Username = username,
                Password = password
            };

            HttpResponseMessage response = await _httpClient.PostAsJsonAsync(
                "api/auth/login",
                request,
                cancellationToken);

            await EnsureSuccessAsync(response, ApiOperation.Login, cancellationToken);

            LoginResponse loginResponse = await response.Content.ReadFromJsonAsync<LoginResponse>(
                cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException("La risposta di accesso ricevuta dal server non è valida.");

            Jwt = loginResponse.Jwt;

            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", Jwt);

            return new ClientSession
            {
                ServerUrl = ServerUrl ?? serverUrl,
                Username = username,
                Jwt = loginResponse.Jwt,
                PayloadSalt = loginResponse.PayloadSalt,
                EncryptedKeyPayload = loginResponse.EncryptedKeyPayload,
                X25519PublicKey = loginResponse.X25519PublicKey,
                Ed25519PublicKey = loginResponse.Ed25519PublicKey
            };
        }

        public async Task CheckHealthAsync(
            string serverUrl,
            CancellationToken cancellationToken = default)
        {
            ConfigureServerUrl(serverUrl);

            HttpResponseMessage response = await _httpClient.GetAsync(
                "api/health",
                cancellationToken);

            await EnsureSuccessAsync(response, ApiOperation.ConnectionCheck, cancellationToken);
        }

        public async Task<CurrentUserResponse> GetCurrentUserAsync(
            CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            HttpResponseMessage httpResponse = await _httpClient.GetAsync("api/auth/me", cancellationToken);
            await EnsureSuccessAsync(httpResponse, ApiOperation.CurrentUser, cancellationToken);
            CurrentUserResponse? response = await httpResponse.Content.ReadFromJsonAsync<CurrentUserResponse>(
                cancellationToken: cancellationToken);

            return response ?? throw new InvalidOperationException("La risposta relativa all'utente non è valida.");
        }

        public async Task RegisterAsync(
                string serverUrl,
                RegisterRequest request,
                CancellationToken cancellationToken = default)
        {
            ConfigureServerUrl(serverUrl);

            HttpResponseMessage response = await _httpClient.PostAsJsonAsync(
                "api/auth/register",
                request,
                cancellationToken);

            await EnsureSuccessAsync(response, ApiOperation.Registration, cancellationToken);
        }

        public async Task<PreKeyStatusResponse> GetPreKeyStatusAsync(
            CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            HttpResponseMessage httpResponse = await _httpClient.GetAsync("api/prekeys/status", cancellationToken);
            await EnsureSuccessAsync(httpResponse, ApiOperation.PreKeyStatus, cancellationToken);
            PreKeyStatusResponse? response = await httpResponse.Content.ReadFromJsonAsync<PreKeyStatusResponse>(
                cancellationToken: cancellationToken);

            return response ?? throw new InvalidOperationException("La risposta sullo stato delle pre-key non è valida.");
        }

        public async Task UploadSignedPreKeyAsync(
            UploadSignedPreKeyRequest request,
            CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            HttpResponseMessage response = await _httpClient.PostAsJsonAsync(
                "api/prekeys/signed",
                request,
                cancellationToken);

            await EnsureSuccessAsync(response, ApiOperation.PreKeyUpload, cancellationToken);
        }

        public async Task UploadOneTimePreKeysAsync(
            UploadOneTimePreKeysRequest request,
            CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            HttpResponseMessage response = await _httpClient.PostAsJsonAsync(
                "api/prekeys/one-time",
                request,
                cancellationToken);

            await EnsureSuccessAsync(response, ApiOperation.PreKeyUpload, cancellationToken);
        }

        public async Task RecoverIdentityBundleAsync(
            RecoverIdentityBundleRequest request,
            CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            HttpResponseMessage response = await _httpClient.PutAsJsonAsync(
                "api/prekeys/recover-bundle",
                request,
                cancellationToken);

            await EnsureSuccessAsync(response, ApiOperation.IdentityRecovery, cancellationToken);
        }

        public async Task UpdateVaultAsync(
            UpdateVaultRequest request,
            CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            HttpResponseMessage response = await _httpClient.PutAsJsonAsync(
                "api/auth/vault",
                request,
                cancellationToken);

            await EnsureSuccessAsync(response, ApiOperation.VaultUpdate, cancellationToken);
        }

        public async Task<PreKeyBundleResponse> GetPreKeyBundleAsync(
                string username,
                CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException("Inserire il nome utente del destinatario.", nameof(username));

            string escapedUsername = Uri.EscapeDataString(username.Trim());

            HttpResponseMessage response = await _httpClient.GetAsync(
                $"api/users/{escapedUsername}/prekey-bundle",
                cancellationToken);

            await EnsureSuccessAsync(response, ApiOperation.BundleRequest, cancellationToken);

            PreKeyBundleResponse? bundle = await response.Content.ReadFromJsonAsync<PreKeyBundleResponse>(
                cancellationToken: cancellationToken);

            return bundle ?? throw new InvalidOperationException("Il bundle di pre-key ricevuto non è valido.");
        }

        public async Task<CreateTransferResponse> CreateTransferAsync(
                CreateTransferRequest request,
                CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            HttpResponseMessage response = await _httpClient.PostAsJsonAsync(
                "api/transfers",
                request,
                cancellationToken);

            await EnsureSuccessAsync(response, ApiOperation.TransferCreation, cancellationToken);

            CreateTransferResponse? transferResponse = await response.Content.ReadFromJsonAsync<CreateTransferResponse>(
                cancellationToken: cancellationToken);

            return transferResponse ?? throw new InvalidOperationException("La risposta di creazione del trasferimento non è valida.");
        }

        public async Task UploadTransferFileContentAsync(
            Guid transferId,
            Guid fileId,
            Stream encryptedContent,
            long ciphertextLength,
            CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            using var content = new StreamContent(encryptedContent);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = ciphertextLength;

            HttpResponseMessage response = await _httpClient.PostAsync(
                $"api/transfers/{transferId}/files/{fileId}/content",
                content,
                cancellationToken);

            await EnsureSuccessAsync(response, ApiOperation.FileUpload, cancellationToken);
        }

        public async Task<List<TransferSummaryResponse>> GetInboxAsync(
            CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            HttpResponseMessage httpResponse = await _httpClient.GetAsync("api/transfers/inbox", cancellationToken);
            await EnsureSuccessAsync(httpResponse, ApiOperation.Inbox, cancellationToken);
            List<TransferSummaryResponse>? response = await httpResponse.Content.ReadFromJsonAsync<List<TransferSummaryResponse>>(
                cancellationToken: cancellationToken);

            return response ?? [];
        }

        public async Task<TransferDetailResponse> GetTransferDetailAsync(
            Guid transferId,
            CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            HttpResponseMessage httpResponse = await _httpClient.GetAsync(
                $"api/transfers/{transferId}",
                cancellationToken);
            await EnsureSuccessAsync(httpResponse, ApiOperation.TransferDetails, cancellationToken);
            TransferDetailResponse? response = await httpResponse.Content.ReadFromJsonAsync<TransferDetailResponse>(
                cancellationToken: cancellationToken);

            return response ?? throw new InvalidOperationException("I dettagli del trasferimento ricevuti non sono validi.");
        }

        public async Task DownloadTransferFileContentToAsync(
            Guid transferId,
            Guid fileId,
            Stream destination,
            CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            HttpResponseMessage response = await _httpClient.GetAsync(
                $"api/transfers/{transferId}/files/{fileId}/content",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            await EnsureSuccessAsync(response, ApiOperation.FileDownload, cancellationToken);

            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);

            await source.CopyToAsync(destination, cancellationToken);
        }

        public async Task MarkTransferDownloadedAsync(
            Guid transferId,
            CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            HttpResponseMessage response = await _httpClient.PostAsync(
                $"api/transfers/{transferId}/downloaded",
                null,
                cancellationToken);

            await EnsureSuccessAsync(response, ApiOperation.TransferCompletion, cancellationToken);
        }

        public async Task ReportTransferTamperedAsync(
            Guid transferId,
            CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            HttpResponseMessage response = await _httpClient.PostAsync(
                $"api/transfers/{transferId}/tampered",
                content: null,
                cancellationToken);

            await EnsureSuccessAsync(response, ApiOperation.TamperingReport, cancellationToken);
        }


        private void EnsureAuthenticated()
        {
            if (string.IsNullOrWhiteSpace(Jwt))
                throw new InvalidOperationException("Il client non è autenticato. Eseguire nuovamente l'accesso.");
        }

        private static async Task EnsureSuccessAsync(
            HttpResponseMessage response,
            ApiOperation operation,
            CancellationToken cancellationToken)
        {
            if (response.IsSuccessStatusCode)
                return;

            string serverMessage = await response.Content.ReadAsStringAsync(cancellationToken);
            string message = response.StatusCode switch
            {
                HttpStatusCode.BadRequest => TranslateBadRequest(serverMessage),
                HttpStatusCode.Unauthorized when operation == ApiOperation.Login =>
                    "Username o password non corretti. Dopo cinque tentativi errati l'account viene bloccato per 10 minuti.",
                HttpStatusCode.Unauthorized =>
                    "Sessione scaduta o non valida. Eseguire nuovamente l'accesso.",
                HttpStatusCode.Forbidden =>
                    "Non disponi dei permessi necessari per eseguire questa operazione.",
                HttpStatusCode.NotFound when operation == ApiOperation.BundleRequest =>
                    "Destinatario non trovato oppure privo delle chiavi necessarie.",
                HttpStatusCode.NotFound =>
                    "La risorsa richiesta non è stata trovata.",
                HttpStatusCode.Conflict when operation == ApiOperation.Registration =>
                    "Questo nome utente è già utilizzato.",
                HttpStatusCode.Conflict =>
                    "L'operazione non può essere completata nello stato attuale.",
                HttpStatusCode.RequestEntityTooLarge =>
                    "Il file o la richiesta supera la dimensione consentita dal server.",
                HttpStatusCode.TooManyRequests =>
                    "Troppe richieste. Riprova tra 10 minuti.",
                >= HttpStatusCode.InternalServerError =>
                    "Il server ha riscontrato un errore. Riprova più tardi.",
                _ => $"Operazione non riuscita. Codice HTTP {(int)response.StatusCode}."
            };

            throw new InvalidOperationException(message);
        }

        private static string TranslateBadRequest(string serverMessage)
        {
            if (serverMessage.Contains("Username is required", StringComparison.OrdinalIgnoreCase))
                return "Il nome utente è obbligatorio.";
            if (serverMessage.Contains("Password is required", StringComparison.OrdinalIgnoreCase))
                return "La password è obbligatoria.";
            if (serverMessage.Contains("at least 12", StringComparison.OrdinalIgnoreCase))
                return "La password deve contenere almeno 12 caratteri.";
            if (serverMessage.Contains("uppercase", StringComparison.OrdinalIgnoreCase))
                return "La password deve contenere almeno una lettera maiuscola.";
            if (serverMessage.Contains("lowercase", StringComparison.OrdinalIgnoreCase))
                return "La password deve contenere almeno una lettera minuscola.";
            if (serverMessage.Contains("number", StringComparison.OrdinalIgnoreCase) ||
                serverMessage.Contains("digit", StringComparison.OrdinalIgnoreCase))
                return "La password deve contenere almeno un numero.";
            if (serverMessage.Contains("special", StringComparison.OrdinalIgnoreCase) ||
                serverMessage.Contains("non alphanumeric", StringComparison.OrdinalIgnoreCase))
                return "La password deve contenere almeno un carattere speciale.";

            return "La richiesta inviata al server non è valida.";
        }

        private enum ApiOperation
        {
            Login,
            Registration,
            ConnectionCheck,
            CurrentUser,
            PreKeyStatus,
            PreKeyUpload,
            IdentityRecovery,
            VaultUpdate,
            BundleRequest,
            TransferCreation,
            FileUpload,
            Inbox,
            TransferDetails,
            FileDownload,
            TransferCompletion,
            TamperingReport
        }
    }
}
