using CryptoEngine.Interfaces;
using System.IO;
using System.Security.Cryptography;

namespace Client.Windows.Services
{
    public sealed class FileDownloadService
    {
        private readonly IFileContentEncryptionService _encryptionService;

        public FileDownloadService(IFileContentEncryptionService encryptionService)
        {
            _encryptionService = encryptionService;
        }

        public async Task DecryptToStagingFileAsync(
            Stream encryptedContent,
            string stagingPath,
            byte[] fileKey,
            long expectedPlaintextLength,
            CancellationToken cancellationToken = default)
        {
            bool created = false;
            try
            {
                await using var plaintext = new FileStream(stagingPath, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
                created = true;

                await _encryptionService.DecryptAsync(encryptedContent, plaintext, fileKey, cancellationToken);

                // EOF at a chunk boundary must not turn a truncated file into a valid download.
                if (plaintext.Length != expectedPlaintextLength)
                    throw new AuthenticationTagMismatchException(
                        "La lunghezza del file non corrisponde ai metadati autenticati.");
            }
            catch
            {
                if (created)
                    File.Delete(stagingPath);
                throw;
            }
        }
    }
}
