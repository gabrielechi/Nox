namespace CryptoEngine.Models
{
    public record EncryptedFileChunkHeader(
        int Version,
        int ChunkIndex,
        int EncryptedChunkLength
    );
}