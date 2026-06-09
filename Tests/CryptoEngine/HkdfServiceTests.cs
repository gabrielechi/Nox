using CryptoEngine.Interfaces;
using CryptoEngine.Services;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Tests.CryptoEngine
{
    public class HkdfServiceTests
    {
        private readonly IHkdfService _hkdfService;

        public HkdfServiceTests()
        {
            _hkdfService = new HkdfService();
        }

        // Test that the same master key and info produce the same derived key
        [Fact]
        public void DeriveKey_sameInputAndInfo_shouldProduceSameKey()
        {
            byte[] masterKey = new byte[32];
            Random.Shared.NextBytes(masterKey);

            byte[] key1 = _hkdfService.DeriveKey(masterKey, "file-encryption");
            byte[] key2 = _hkdfService.DeriveKey(masterKey, "file-encryption");

            Assert.Equal(key1, key2);
        }

        // Test that different info values produce different derived keys
        [Fact]
        public void DeriveKey_differentInfo_shouldProduceDifferentKeys()
        {
            byte[] masterKey = new byte[32];
            Random.Shared.NextBytes(masterKey);

            byte[] key1 = _hkdfService.DeriveKey(masterKey, "file-encryption");
            byte[] key2 = _hkdfService.DeriveKey(masterKey, "metadata-encryption");

            Assert.NotEqual(key1, key2);
        }

        // Test that different master keys produce different derived keys
        [Fact]
        public void DeriveKey_differentMasterKey_shouldProduceDifferentKeys()
        {
            byte[] masterKey1 = new byte[32];
            byte[] masterKey2 = new byte[32];
            Random.Shared.NextBytes(masterKey1);
            Random.Shared.NextBytes(masterKey2);

            byte[] key1 = _hkdfService.DeriveKey(masterKey1, "file-encryption");
            byte[] key2 = _hkdfService.DeriveKey(masterKey2, "file-encryption");

            Assert.NotEqual(key1, key2);
        }

        // Test that the derived key has the correct length
        [Fact]
        public void DeriveKey_shouldProduceCorrectSpecifiedLength()
        {
            byte[] masterKey = new byte[32];
            RandomNumberGenerator.Fill(masterKey);

            byte[] key16 = _hkdfService.DeriveKey(masterKey, "test", outputLength: 16);
            byte[] key32 = _hkdfService.DeriveKey(masterKey, "test", outputLength: 32);

            Assert.Equal(16, key16.Length);
            Assert.Equal(32, key32.Length);
        }
    }
}
