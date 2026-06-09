using CryptoEngine.Interfaces;
using CryptoEngine.Services;
using System.Security.Cryptography;
using System.Text;

namespace Tests.CryptoEngine
{
    public class FileContentEncryptionServiceTests
    {
        private readonly IFileContentEncryptionService _service;

        public FileContentEncryptionServiceTests()
        {
            ISymmetricService symmetricService = new AesGcmSymmetricService();
            _service = new FileContentEncryptionService(symmetricService);
        }

        [Fact]
        public async Task EncryptAndDecrypt_SmallStream_RoundTrips()
        {
            byte[] key = RandomNumberGenerator.GetBytes(32);
            byte[] plaintext = Encoding.UTF8.GetBytes("hello encrypted file");

            using var plaintextInput = new MemoryStream(plaintext);
            using var encrypted = new MemoryStream();
            using var decrypted = new MemoryStream();

            await _service.EncryptAsync(plaintextInput, encrypted, key);

            encrypted.Position = 0;

            await _service.DecryptAsync(encrypted, decrypted, key);

            Assert.Equal(plaintext, decrypted.ToArray());
        }

        [Fact]
        public async Task EncryptAndDecrypt_LargeStream_RoundTrips()
        {
            byte[] key = RandomNumberGenerator.GetBytes(32);
            byte[] plaintext = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 123);

            using var plaintextInput = new MemoryStream(plaintext);
            using var encrypted = new MemoryStream();
            using var decrypted = new MemoryStream();

            await _service.EncryptAsync(plaintextInput, encrypted, key);

            encrypted.Position = 0;

            await _service.DecryptAsync(encrypted, decrypted, key);

            Assert.Equal(plaintext, decrypted.ToArray());
        }

        [Fact]
        public async Task Encrypt_SamePlaintextTwice_ProducesDifferentCiphertext()
        {
            byte[] key = RandomNumberGenerator.GetBytes(32);
            byte[] plaintext = Encoding.UTF8.GetBytes("same plaintext");

            using var firstInput = new MemoryStream(plaintext);
            using var secondInput = new MemoryStream(plaintext);
            using var firstEncrypted = new MemoryStream();
            using var secondEncrypted = new MemoryStream();

            await _service.EncryptAsync(firstInput, firstEncrypted, key);
            await _service.EncryptAsync(secondInput, secondEncrypted, key);

            Assert.NotEqual(firstEncrypted.ToArray(), secondEncrypted.ToArray());
        }

        [Fact]
        public async Task Decrypt_WithWrongKey_Throws()
        {
            byte[] key = RandomNumberGenerator.GetBytes(32);
            byte[] wrongKey = RandomNumberGenerator.GetBytes(32);
            byte[] plaintext = Encoding.UTF8.GetBytes("secret");

            using var plaintextInput = new MemoryStream(plaintext);
            using var encrypted = new MemoryStream();

            await _service.EncryptAsync(plaintextInput, encrypted, key);

            encrypted.Position = 0;

            using var decrypted = new MemoryStream();

            await Assert.ThrowsAnyAsync<Exception>(() =>
                _service.DecryptAsync(encrypted, decrypted, wrongKey));
        }
    }
}