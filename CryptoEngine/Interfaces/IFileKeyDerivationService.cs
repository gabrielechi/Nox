using CryptoEngine.Models;

namespace CryptoEngine.Interfaces
{
    public interface IFileKeyDerivationService
    {
        FileDerivedKeys DeriveFileKeys(
            byte[] rootKey,
            Guid transferId,
            int fileIndex);
    }
}