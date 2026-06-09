using CryptoEngine.Interfaces;
using CryptoEngine.Models;

namespace CryptoEngine.Services
{
    public class FileKeyDerivationService : IFileKeyDerivationService
    {
        private const int RootKeyLength = 32;
        private const int DerivedKeyLength = 32;

        private readonly IHkdfService _hkdfService;

        public FileKeyDerivationService(IHkdfService hkdfService)
        {
            _hkdfService = hkdfService;
        }

        public FileDerivedKeys DeriveFileKeys(
            byte[] rootKey,
            Guid transferId,
            int fileIndex)
        {
            if (rootKey.Length != RootKeyLength)
                throw new ArgumentException($"RootKey must be {RootKeyLength} bytes.", nameof(rootKey));

            if (fileIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(fileIndex));

            string transferIdText = transferId.ToString("N");

            byte[] fileKey = _hkdfService.DeriveKey(
                rootKey,
                $"ZKT-FILE-CONTENT-KEY-V1:{transferIdText}:{fileIndex}",
                DerivedKeyLength);

            byte[] metadataKey = _hkdfService.DeriveKey(
                rootKey,
                $"ZKT-FILE-METADATA-KEY-V1:{transferIdText}:{fileIndex}",
                DerivedKeyLength);

            return new FileDerivedKeys(fileKey, metadataKey);
        }
    }
}