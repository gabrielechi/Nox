using Client.Windows.Interfaces;
using Client.Windows.Models;
using CryptoEngine.Interfaces;
using System.Text.Json;

namespace Client.Windows.Services
{
    public class UserVaultService : IUserVaultService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        private readonly IArgonKeyDerivationService _argonService;
        private readonly ISymmetricService _symmetricService;

        public UserVaultService(
            IArgonKeyDerivationService argonService,
            ISymmetricService symmetricService)
        {
            _argonService = argonService;
            _symmetricService = symmetricService;
        }

        public UserKeyPayload DecryptVault(
            string password,
            byte[] payloadSalt,
            byte[] encryptedKeyPayload)
        {
            byte[] payloadKey = _argonService.DerivePayloadKey(password, payloadSalt);

            byte[] plaintext = _symmetricService.Decrypt(
                encryptedKeyPayload,
                payloadKey);

            UserKeyPayload? payload = JsonSerializer.Deserialize<UserKeyPayload>(
                plaintext,
                JsonOptions);

            if (payload is null)
                throw new InvalidOperationException("Invalid encrypted key payload.");

            return payload;
        }

        public byte[] EncryptVault(
            string password,
            byte[] payloadSalt,
            UserKeyPayload payload)
        {
            byte[] payloadKey = _argonService.DerivePayloadKey(password, payloadSalt);

            byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(
                payload,
                JsonOptions);

            return _symmetricService.Encrypt(
                plaintext,
                payloadKey);
        }
    }
}