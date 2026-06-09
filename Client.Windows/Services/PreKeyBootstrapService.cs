using Client.Windows.Interfaces;
using Client.Windows.Models;
using CryptoEngine.Interfaces;
using CryptoEngine.Services;
using Shared.DTO;
using Shared.DTO.X3DH;

namespace Client.Windows.Services
{
    public class PreKeyBootstrapService : IPreKeyBootstrapService
    {
        private const int MinimumAvailableOneTimePreKeys = 25;
        private const int OneTimePreKeyBatchSize = 25;
        private const int SignedPreKeyId = 1;

        private readonly IKeyPairService _keyPairService;

        public PreKeyBootstrapService(IKeyPairService keyPairService)
        {
            _keyPairService = keyPairService;
        }

        public async Task EnsurePreKeysAsync(
            ApiClient apiClient,
            UserKeyPayload vault,
            CancellationToken cancellationToken = default)
        {
            PreKeyStatusResponse status = await apiClient.GetPreKeyStatusAsync(cancellationToken);

            if (!status.HasSignedPreKey || vault.SignedPreKey is null)
            {
                await CreateAndUploadSignedPreKeyAsync(
                    apiClient,
                    vault,
                    cancellationToken);
            }

            if (status.AvailableOneTimePreKeys < MinimumAvailableOneTimePreKeys)
            {
                int missing = MinimumAvailableOneTimePreKeys - status.AvailableOneTimePreKeys;
                int countToGenerate = Math.Max(missing, OneTimePreKeyBatchSize);

                await CreateAndUploadOneTimePreKeysAsync(
                    apiClient,
                    vault,
                    countToGenerate,
                    cancellationToken);
            }
        }

        private async Task CreateAndUploadSignedPreKeyAsync(
            ApiClient apiClient,
            UserKeyPayload vault,
            CancellationToken cancellationToken)
        {
            var signedPreKey = _keyPairService.GenerateX25519KeyPair();

            byte[] message = X3dhService.BuildSignedPreKeySignatureMessage(
                SignedPreKeyId,
                signedPreKey.PublicKey);

            byte[] signature = _keyPairService.Sign(
                message,
                vault.Ed25519IdentityPrivateKey);

            vault.SignedPreKey = new ClientSignedPreKeyState
            {
                KeyId = SignedPreKeyId,
                PublicKey = signedPreKey.PublicKey,
                PrivateKey = signedPreKey.PrivateKey,
                Signature = signature,
                CreatedAtUtc = DateTime.UtcNow
            };

            var request = new UploadSignedPreKeyRequest
            {
                KeyId = vault.SignedPreKey.KeyId,
                PublicKey = vault.SignedPreKey.PublicKey,
                Signature = vault.SignedPreKey.Signature
            };

            await apiClient.UploadSignedPreKeyAsync(
                request,
                cancellationToken);
        }

        private async Task CreateAndUploadOneTimePreKeysAsync(
            ApiClient apiClient,
            UserKeyPayload vault,
            int count,
            CancellationToken cancellationToken)
        {
            var newPreKeys = new List<ClientOneTimePreKeyState>();
            var requestKeys = new List<OneTimePreKeyDto>();

            for (int i = 0; i < count; i++)
            {
                var oneTimePreKey = _keyPairService.GenerateX25519KeyPair();

                int keyId = vault.NextOneTimePreKeyId;
                vault.NextOneTimePreKeyId++;

                var state = new ClientOneTimePreKeyState
                {
                    KeyId = keyId,
                    PublicKey = oneTimePreKey.PublicKey,
                    PrivateKey = oneTimePreKey.PrivateKey,
                    IsUploaded = false,
                    IsUsed = false,
                    CreatedAtUtc = DateTime.UtcNow
                };

                newPreKeys.Add(state);

                requestKeys.Add(new OneTimePreKeyDto
                {
                    KeyId = keyId,
                    PublicKey = oneTimePreKey.PublicKey
                });
            }

            var request = new UploadOneTimePreKeysRequest
            {
                Keys = requestKeys
            };

            await apiClient.UploadOneTimePreKeysAsync(
                request,
                cancellationToken);

            foreach (ClientOneTimePreKeyState preKey in newPreKeys)
            {
                preKey.IsUploaded = true;
                vault.OneTimePreKeys.Add(preKey);
            }
        }
    }
}