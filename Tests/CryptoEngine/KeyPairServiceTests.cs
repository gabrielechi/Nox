using CryptoEngine.Interfaces;
using CryptoEngine.Services;
using System;
using System.Collections.Generic;
using System.Text;

namespace Tests.CryptoEngine
{
    public class KeyPairServiceTests
    {
        private readonly IKeyPairService _keyPairService;

        public KeyPairServiceTests()
        {
            _keyPairService = new KeyPairService();
        }

        //Checks if the generated X25519 key pair has the correct lengths for public and private keys

        [Fact]
        public void GenerateX25519KeyPair_ShouldReturnValidKeys()
        {
            var keyPair = _keyPairService.GenerateX25519KeyPair();

            Assert.NotNull(keyPair);
            Assert.Equal(32, keyPair.PublicKey.Length);
            Assert.Equal(32, keyPair.PrivateKey.Length);
        }

        [Fact]
        public void GenerateEd25519KeyPair_ShouldReturnValidKeys()
        {
            var keyPair = _keyPairService.GenerateEd25519KeyPair();

            Assert.NotNull(keyPair);
            Assert.Equal(32, keyPair.PublicKey.Length);
            Assert.Equal(64, keyPair.PrivateKey.Length); // Ed25519 private keys are 64 bytes long 
        }

        // Checks if both parties compute the same shared secret using their private keys and the other party's public key
        [Fact]
        public void ComputeSharedSecret_BothShouldReturnSameSecret()
        {
            var alice = _keyPairService.GenerateX25519KeyPair();
            var bob = _keyPairService.GenerateX25519KeyPair();

            byte[] secretAlice = _keyPairService.ComputeSharedSecret(alice.PrivateKey, bob.PublicKey);
            byte[] secretBob = _keyPairService.ComputeSharedSecret(bob.PrivateKey, alice.PublicKey);

            Assert.Equal(secretAlice, secretBob);

        }

        // Checks if a message signed with the private key can be successfully verified with the corresponding public key
        [Fact]
        public void SignAndVerify_ShouldSucceed()
        {
            var keyPair = _keyPairService.GenerateEd25519KeyPair();
            byte[] msg = Encoding.UTF8.GetBytes("Message 123!");

            byte[] signature = _keyPairService.Sign(msg, keyPair.PrivateKey);
            bool isValid = _keyPairService.Verify(msg, signature, keyPair.PublicKey);

            Assert.True(isValid);
        }

        [Fact]
        public void Verify_tamperedSignature_ShouldFail()
        {
            var keyPair = _keyPairService.GenerateEd25519KeyPair();
            byte[] msg = Encoding.UTF8.GetBytes("Original message");

            byte[] signature = _keyPairService.Sign(msg, keyPair.PrivateKey);
            signature[0] ^= 0xFF; // Tamper with the signature by flipping the first byte

            bool isValid = _keyPairService.Verify(msg, signature, keyPair.PublicKey);

            Assert.False(isValid);
        }

        [Fact]
        public void Verify_tamperedMessage_ShouldFail()
        {
            var keyPair = _keyPairService.GenerateEd25519KeyPair();
            byte[] msg = Encoding.UTF8.GetBytes("Original message");
            byte[] fakeMsg = Encoding.UTF8.GetBytes("Tampered message");

            byte[] signature = _keyPairService.Sign(msg, keyPair.PrivateKey);
            bool isValid = _keyPairService.Verify(fakeMsg, signature, keyPair.PublicKey);

            Assert.False(isValid);
        }
    }
}
