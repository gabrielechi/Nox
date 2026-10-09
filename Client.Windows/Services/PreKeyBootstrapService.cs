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
        private static readonly TimeSpan SignedPreKeyLifetime = TimeSpan.FromHours(48);
        private static readonly TimeSpan PreviousSignedPreKeyRetention = TimeSpan.FromHours(25);

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

            PruneExpiredKeyMaterial(vault);

            bool signedPreKeyMustRotate =
                !status.HasSignedPreKey ||
                vault.SignedPreKey is null ||
                status.SignedPreKeyId != vault.SignedPreKey.KeyId ||
                DateTime.UtcNow - vault.SignedPreKey.CreatedAtUtc >= SignedPreKeyLifetime ||
                status.SignedPreKeyExpiresAtUtc is DateTime expiresAtUtc && expiresAtUtc <= DateTime.UtcNow;

            if (signedPreKeyMustRotate)
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

        public void PrepareFreshRecoveryBundle(
            UserKeyPayload vault,
            bool preservePreviousDecryptionKeys)
        {
            ArgumentNullException.ThrowIfNull(vault);

            DateTime now = DateTime.UtcNow;

            if (preservePreviousDecryptionKeys)
            {
                ArchiveCurrentSignedPreKey(vault, now);

                foreach (ClientOneTimePreKeyState oneTimePreKey in vault.OneTimePreKeys)
                {
                    oneTimePreKey.IsUsed = true;
                    oneTimePreKey.IsUploaded = false;
                    oneTimePreKey.RetiredAtUtc = now;
                }
            }
            else
            {
                vault.SignedPreKey = null;
                vault.PreviousSignedPreKeys.Clear();
                vault.OneTimePreKeys.Clear();
            }

            vault.SignedPreKey = GenerateSignedPreKey(vault, now);

            List<ClientOneTimePreKeyState> freshOneTimePreKeys = GenerateOneTimePreKeys(
                vault,
                OneTimePreKeyBatchSize,
                now,
                isUploaded: true);

            vault.OneTimePreKeys.AddRange(freshOneTimePreKeys);
            PruneExpiredKeyMaterial(vault);
        }

        private async Task CreateAndUploadSignedPreKeyAsync(
            ApiClient apiClient,
            UserKeyPayload vault,
            CancellationToken cancellationToken)
        {
            DateTime now = DateTime.UtcNow;
            ClientSignedPreKeyState newSignedPreKey = GenerateSignedPreKey(vault, now);

            var request = new UploadSignedPreKeyRequest
            {
                KeyId = newSignedPreKey.KeyId,
                PublicKey = newSignedPreKey.PublicKey,
                Signature = newSignedPreKey.Signature
            };

            await apiClient.UploadSignedPreKeyAsync(
                request,
                cancellationToken);

            ArchiveCurrentSignedPreKey(vault, now);
            vault.SignedPreKey = newSignedPreKey;
        }

        private async Task CreateAndUploadOneTimePreKeysAsync(
            ApiClient apiClient,
            UserKeyPayload vault,
            int count,
            CancellationToken cancellationToken)
        {
            List<ClientOneTimePreKeyState> newPreKeys = GenerateOneTimePreKeys(
                vault,
                count,
                DateTime.UtcNow,
                isUploaded: false);

            List<OneTimePreKeyDto> requestKeys = newPreKeys
                .Select(preKey => new OneTimePreKeyDto
                {
                    KeyId = preKey.KeyId,
                    PublicKey = preKey.PublicKey
                })
                .ToList();

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

        private ClientSignedPreKeyState GenerateSignedPreKey(
            UserKeyPayload vault,
            DateTime createdAtUtc)
        {
            var signedPreKey = _keyPairService.GenerateX25519KeyPair();

            int currentKeyId = vault.SignedPreKey?.KeyId ?? 0;
            int keyId = Math.Max(vault.NextSignedPreKeyId, currentKeyId + 1);

            if (keyId < 1)
                keyId = 1;

            vault.NextSignedPreKeyId = checked(keyId + 1);

            byte[] message = X3dhService.BuildSignedPreKeySignatureMessage(
                keyId,
                signedPreKey.PublicKey);

            byte[] signature = _keyPairService.Sign(
                message,
                vault.Ed25519IdentityPrivateKey);

            return new ClientSignedPreKeyState
            {
                KeyId = keyId,
                PublicKey = signedPreKey.PublicKey,
                PrivateKey = signedPreKey.PrivateKey,
                Signature = signature,
                CreatedAtUtc = createdAtUtc
            };
        }

        private List<ClientOneTimePreKeyState> GenerateOneTimePreKeys(
            UserKeyPayload vault,
            int count,
            DateTime createdAtUtc,
            bool isUploaded)
        {
            var generatedPreKeys = new List<ClientOneTimePreKeyState>(count);

            for (int i = 0; i < count; i++)
            {
                var oneTimePreKey = _keyPairService.GenerateX25519KeyPair();

                int keyId = Math.Max(vault.NextOneTimePreKeyId, 1);
                vault.NextOneTimePreKeyId = checked(keyId + 1);

                generatedPreKeys.Add(new ClientOneTimePreKeyState
                {
                    KeyId = keyId,
                    PublicKey = oneTimePreKey.PublicKey,
                    PrivateKey = oneTimePreKey.PrivateKey,
                    IsUploaded = isUploaded,
                    IsUsed = false,
                    CreatedAtUtc = createdAtUtc
                });
            }

            return generatedPreKeys;
        }

        private static void ArchiveCurrentSignedPreKey(
            UserKeyPayload vault,
            DateTime retiredAtUtc)
        {
            if (vault.SignedPreKey is null)
                return;

            vault.SignedPreKey.RetiredAtUtc = retiredAtUtc;
            vault.PreviousSignedPreKeys.RemoveAll(preKey =>
                preKey.KeyId == vault.SignedPreKey.KeyId);
            vault.PreviousSignedPreKeys.Add(vault.SignedPreKey);
        }

        private static void PruneExpiredKeyMaterial(UserKeyPayload vault)
        {
            DateTime cutoff = DateTime.UtcNow - PreviousSignedPreKeyRetention;

            foreach (ClientSignedPreKeyState preKey in vault.PreviousSignedPreKeys
                         .Where(preKey =>
                             preKey.RetiredAtUtc is DateTime retiredAtUtc && retiredAtUtc < cutoff)
                         .ToList())
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(preKey.PrivateKey);
                vault.PreviousSignedPreKeys.Remove(preKey);
            }

            foreach (ClientOneTimePreKeyState preKey in vault.OneTimePreKeys
                         .Where(preKey =>
                             preKey.IsUsed &&
                             (preKey.RetiredAtUtc is null || preKey.RetiredAtUtc < cutoff))
                         .ToList())
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(preKey.PrivateKey);
                vault.OneTimePreKeys.Remove(preKey);
            }
        }
    }
}
