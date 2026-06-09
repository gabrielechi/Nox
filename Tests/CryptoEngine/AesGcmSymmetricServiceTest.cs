using CryptoEngine.Interfaces;
using CryptoEngine.Services;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Tests.CryptoEngine
{
    public class AesGcmSymmetricServiceTest
    {
        private readonly ISymmetricService _symmetricService;

        public AesGcmSymmetricServiceTest()
        {
            _symmetricService = new AesGcmSymmetricService();
        }

        [Fact]
        public void EncryptAndDecrypt_ShouldReturnOriginalPlaintext()
        {
            byte[] key = _symmetricService.GenerateKey();
            byte[] plaintext = Encoding.UTF8.GetBytes("Secret Message 123!");

            byte[] encryptedData = _symmetricService.Encrypt(plaintext, key);
            byte[] decryptedData = _symmetricService.Decrypt(encryptedData, key);

            Assert.Equal(plaintext, decryptedData);
        }

        [Fact]
        public void Encrypt_SameInput_ShouldProduceDifferentCiphertext()
        {
            byte[] key = _symmetricService.GenerateKey();
            byte[] plaintext = Encoding.UTF8.GetBytes("Secret Message 123!");

            byte[] encryptedData1 = _symmetricService.Encrypt(plaintext, key);
            byte[] encryptedData2 = _symmetricService.Encrypt(plaintext, key);

            Assert.NotEqual(encryptedData1, encryptedData2);
        }

        [Fact]
        public void Decrypt_wrongKey_ShouldThrowException()
        {
            byte[] key1 = _symmetricService.GenerateKey();
            byte[] key2 = _symmetricService.GenerateKey();

            byte[] plaintext = Encoding.UTF8.GetBytes("Secret Message 123!");

            byte[] encryptedData = _symmetricService.Encrypt(plaintext, key1);
            Assert.ThrowsAny<Exception>(() => _symmetricService.Decrypt(encryptedData, key2));
        }

        [Fact]
        public void Decrypt_tamperedData_ShouldThrowException()
        {
            byte[] key = _symmetricService.GenerateKey();
            byte[] plaintext = Encoding.UTF8.GetBytes("Secret Message 123!");

            byte[] encryptedData = _symmetricService.Encrypt(plaintext, key);

            encryptedData[encryptedData.Length - 1] ^= 0xFF; // Tamper with the last byte

            Assert.ThrowsAny<Exception>(() => _symmetricService.Decrypt(encryptedData, key));
        }
    }
}
