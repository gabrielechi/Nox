using CryptoEngine.Interfaces;
using CryptoEngine.Models;
using System.Text.Json;

namespace CryptoEngine.Services
{
    public class FileMetadataProtector : IFileMetadataProtector
    {
        private const int KeyLength = 32;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        private readonly ISymmetricService _symmetricService;

        public FileMetadataProtector(ISymmetricService symmetricService)
        {
            _symmetricService = symmetricService;
        }

        public byte[] Protect(FileMetadata metadata, byte[] metadataKey)
        {
            ValidateMetadata(metadata);
            ValidateKey(metadataKey);

            byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions);

            return _symmetricService.Encrypt(plaintext, metadataKey);
        }

        public FileMetadata Unprotect(byte[] protectedMetadata, byte[] metadataKey)
        {
            ValidateKey(metadataKey);

            byte[] plaintext = _symmetricService.Decrypt(protectedMetadata, metadataKey);

            FileMetadata? metadata = JsonSerializer.Deserialize<FileMetadata>(
                plaintext,
                JsonOptions);

            if (metadata is null)
                throw new InvalidOperationException("Invalid file metadata.");

            ValidateMetadata(metadata);

            return metadata;
        }

        private static void ValidateKey(byte[] key)
        {
            if (key.Length != KeyLength)
                throw new ArgumentException($"Metadata key must be {KeyLength} bytes.", nameof(key));
        }

        private static void ValidateMetadata(FileMetadata metadata)
        {
            if (string.IsNullOrWhiteSpace(metadata.OriginalFileName))
                throw new ArgumentException("Original file name is required.", nameof(metadata));

            if (metadata.PlaintextLength < 0)
                throw new ArgumentException("Plaintext length must be non-negative.", nameof(metadata));
        }
    }
}