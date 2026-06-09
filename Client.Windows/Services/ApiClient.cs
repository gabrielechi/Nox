using Client.Windows.Models;
using Shared.DTO;
using Shared.DTO.Auth;
using Shared.DTO.Transfers;
using Shared.DTO.X3DH;
using System.IO;
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
                throw new ArgumentException("Server URL is required.", nameof(serverUrl));

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

            if (!response.IsSuccessStatusCode)
            {
                string error = await response.Content.ReadAsStringAsync(cancellationToken);

                if (string.IsNullOrWhiteSpace(error))
                    error = $"Login failed with status {(int)response.StatusCode}.";

                throw new InvalidOperationException(error);
            }

            LoginResponse loginResponse = await response.Content.ReadFromJsonAsync<LoginResponse>(
                cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException("Invalid login response.");

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

            if (!response.IsSuccessStatusCode)
            {
                string error = await response.Content.ReadAsStringAsync(cancellationToken);

                if (string.IsNullOrWhiteSpace(error))
                    error = $"Server health check failed with status {(int)response.StatusCode}.";

                throw new InvalidOperationException(error);
            }
        }

        public async Task<CurrentUserResponse> GetCurrentUserAsync(
            CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            CurrentUserResponse? response = await _httpClient.GetFromJsonAsync<CurrentUserResponse>(
                "api/auth/me",
                cancellationToken);

            return response ?? throw new InvalidOperationException("Invalid current user response.");
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

            if (!response.IsSuccessStatusCode)
            {
                string error = await response.Content.ReadAsStringAsync(cancellationToken);

                if (string.IsNullOrWhiteSpace(error))
                    error = $"Registration failed with status {(int)response.StatusCode}.";

                throw new InvalidOperationException(error);
            }
        }

        public async Task<PreKeyStatusResponse> GetPreKeyStatusAsync(
            CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            PreKeyStatusResponse? response = await _httpClient.GetFromJsonAsync<PreKeyStatusResponse>(
                "api/prekeys/status",
                cancellationToken);

            return response ?? throw new InvalidOperationException("Invalid prekey status response.");
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

            if (!response.IsSuccessStatusCode)
            {
                string error = await response.Content.ReadAsStringAsync(cancellationToken);

                if (string.IsNullOrWhiteSpace(error))
                    error = $"Signed prekey upload failed with status {(int)response.StatusCode}.";

                throw new InvalidOperationException(error);
            }
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

            if (!response.IsSuccessStatusCode)
            {
                string error = await response.Content.ReadAsStringAsync(cancellationToken);

                if (string.IsNullOrWhiteSpace(error))
                    error = $"One-time prekeys upload failed with status {(int)response.StatusCode}.";

                throw new InvalidOperationException(error);
            }
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

            if (!response.IsSuccessStatusCode)
            {
                string error = await response.Content.ReadAsStringAsync(cancellationToken);

                if (string.IsNullOrWhiteSpace(error))
                    error = $"Vault update failed with status {(int)response.StatusCode}.";

                throw new InvalidOperationException(error);
            }
        }

        public async Task<PreKeyBundleResponse> GetPreKeyBundleAsync(
                string username,
                CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException("Username is required.", nameof(username));

            string escapedUsername = Uri.EscapeDataString(username.Trim());

            HttpResponseMessage response = await _httpClient.GetAsync(
                $"api/users/{escapedUsername}/prekey-bundle",
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                string error = await response.Content.ReadAsStringAsync(cancellationToken);

                if (string.IsNullOrWhiteSpace(error))
                    error = $"PreKey bundle request failed with status {(int)response.StatusCode}.";

                throw new InvalidOperationException(error);
            }

            PreKeyBundleResponse? bundle = await response.Content.ReadFromJsonAsync<PreKeyBundleResponse>(
                cancellationToken: cancellationToken);

            return bundle ?? throw new InvalidOperationException("Invalid prekey bundle response.");
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

            if (!response.IsSuccessStatusCode)
            {
                string error = await response.Content.ReadAsStringAsync(cancellationToken);

                if (string.IsNullOrWhiteSpace(error))
                    error = $"Create transfer failed with status {(int)response.StatusCode}.";

                throw new InvalidOperationException(error);
            }

            CreateTransferResponse? transferResponse = await response.Content.ReadFromJsonAsync<CreateTransferResponse>(
                cancellationToken: cancellationToken);

            return transferResponse ?? throw new InvalidOperationException("Invalid create transfer response.");
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

            if (!response.IsSuccessStatusCode)
            {
                string error = await response.Content.ReadAsStringAsync(cancellationToken);

                if (string.IsNullOrWhiteSpace(error))
                    error = $"File content upload failed with status {(int)response.StatusCode}.";

                throw new InvalidOperationException(error);
            }
        }

        public async Task<List<TransferSummaryResponse>> GetInboxAsync(
            CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            List<TransferSummaryResponse>? response = await _httpClient.GetFromJsonAsync<List<TransferSummaryResponse>>(
                "api/transfers/inbox",
                cancellationToken);

            return response ?? [];
        }

        public async Task<TransferDetailResponse> GetTransferDetailAsync(
            Guid transferId,
            CancellationToken cancellationToken = default)
        {
            EnsureAuthenticated();

            TransferDetailResponse? response = await _httpClient.GetFromJsonAsync<TransferDetailResponse>(
                $"api/transfers/{transferId}",
                cancellationToken);

            return response ?? throw new InvalidOperationException("Invalid transfer detail response.");
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

            if (!response.IsSuccessStatusCode)
            {
                string error = await response.Content.ReadAsStringAsync(cancellationToken);

                if (string.IsNullOrWhiteSpace(error))
                    error = $"File download failed with status {(int)response.StatusCode}.";

                throw new InvalidOperationException(error);
            }

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

            if (!response.IsSuccessStatusCode)
            {
                string error = await response.Content.ReadAsStringAsync(cancellationToken);

                if (string.IsNullOrWhiteSpace(error))
                    error = $"Mark transfer downloaded failed with status {(int)response.StatusCode}.";

                throw new InvalidOperationException(error);
            }
        }


        private void EnsureAuthenticated()
        {
            if (string.IsNullOrWhiteSpace(Jwt))
                throw new InvalidOperationException("The client is not authenticated.");
        }
    }
}
