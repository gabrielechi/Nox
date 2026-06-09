using CryptoEngine.Models;

namespace CryptoEngine.Interfaces
{
    public interface IFileMetadataProtector
    {
        byte[] Protect(FileMetadata metadata, byte[] metadataKey);
        FileMetadata Unprotect(byte[] protectedMetadata, byte[] metadataKey);
    }
}