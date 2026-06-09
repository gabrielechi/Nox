using CryptoEngine.Interfaces;
using CryptoEngine.Models;
using CryptoEngine.Services;
using System.Security.Cryptography;

namespace Tests.CryptoEngine
{
    public class FileMetadataProtectorTests
    {
        private readonly IFileMetadataProtector _protector;

        public FileMetadataProtectorTests()
        {
            ISymmetricService symmetricService = new AesGcmSymmetricService();
            _protector = new FileMetadataProtector(symmetricService);
        }

        [Fact]
        public void ProtectAndUnprotect_ValidMetadata_RoundTrips()
        {
            byte[] key = RandomNumberGenerator.GetBytes(32);

            var metadata = new FileMetadata
            {
                OriginalFileName = "documento.pdf",
                PlaintextLength = 12345,
                ContentType = "application/pdf",
                LastModifiedUtc = DateTime.UtcNow
            };

            byte[] protectedMetadata = _protector.Protect(metadata, key);

            FileMetadata unprotected = _protector.Unprotect(protectedMetadata, key);

            Assert.Equal(metadata.OriginalFileName, unprotected.OriginalFileName);
            Assert.Equal(metadata.PlaintextLength, unprotected.PlaintextLength);
            Assert.Equal(metadata.ContentType, unprotected.ContentType);
            Assert.Equal(metadata.LastModifiedUtc?.ToUniversalTime().ToString("O"), unprotected.LastModifiedUtc?.ToUniversalTime().ToString("O"));
        }

        [Fact]
        public void Protect_SameMetadataTwice_ProducesDifferentCiphertext()
        {
            byte[] key = RandomNumberGenerator.GetBytes(32);

            var metadata = new FileMetadata
            {
                OriginalFileName = "documento.pdf",
                PlaintextLength = 12345
            };

            byte[] first = _protector.Protect(metadata, key);
            byte[] second = _protector.Protect(metadata, key);

            Assert.NotEqual(first, second);
        }

        [Fact]
        public void Unprotect_WithWrongKey_Throws()
        {
            byte[] key = RandomNumberGenerator.GetBytes(32);
            byte[] wrongKey = RandomNumberGenerator.GetBytes(32);

            var metadata = new FileMetadata
            {
                OriginalFileName = "documento.pdf",
                PlaintextLength = 12345
            };

            byte[] protectedMetadata = _protector.Protect(metadata, key);

            Assert.ThrowsAny<Exception>(() =>
                _protector.Unprotect(protectedMetadata, wrongKey));
        }

        [Fact]
        public void Protect_InvalidKeyLength_Throws()
        {
            var metadata = new FileMetadata
            {
                OriginalFileName = "documento.pdf",
                PlaintextLength = 12345
            };

            byte[] invalidKey = new byte[31];

            Assert.Throws<ArgumentException>(() =>
                _protector.Protect(metadata, invalidKey));
        }
    }
}