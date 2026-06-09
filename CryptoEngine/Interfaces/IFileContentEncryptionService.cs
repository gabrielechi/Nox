namespace CryptoEngine.Interfaces
{
    public interface IFileContentEncryptionService
    {
        Task EncryptAsync(
            Stream plaintext,
            Stream encryptedOutput,
            byte[] fileKey,
            CancellationToken cancellationToken = default);

        Task DecryptAsync(
            Stream encryptedInput,
            Stream plaintextOutput,
            byte[] fileKey,
            CancellationToken cancellationToken = default);
    }
}