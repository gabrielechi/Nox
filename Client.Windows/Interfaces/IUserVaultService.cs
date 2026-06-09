using Client.Windows.Models;

namespace Client.Windows.Interfaces
{
    public interface IUserVaultService
    {
        UserKeyPayload DecryptVault(
            string password,
            byte[] payloadSalt,
            byte[] encryptedKeyPayload);

        byte[] EncryptVault(
            string password,
            byte[] payloadSalt,
            UserKeyPayload payload);
    }
}