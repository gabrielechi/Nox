using CryptoEngine.Interfaces;
using Sodium;
using System;
using System.Collections.Generic;
using System.Text;

namespace CryptoEngine.Services
{
    public class KeyPairService : IKeyPairService
    {
        public KeyPair GenerateX25519KeyPair()
        {
            var kp = PublicKeyBox.GenerateKeyPair();
            return new KeyPair(kp.PublicKey, kp.PrivateKey);
        }

        public KeyPair GenerateEd25519KeyPair()
        {
            var kp = PublicKeyAuth.GenerateKeyPair();
            return new KeyPair(kp.PublicKey, kp.PrivateKey);
        }
        
        public byte[] ComputeSharedSecret(byte[] myPrivateKey, byte[] theirPublicKey)
        {
            return ScalarMult.Mult(myPrivateKey, theirPublicKey);
        }

        public byte[] Sign(byte[] message, byte[] privateKey)
        {
            return PublicKeyAuth.SignDetached(message, privateKey); // returns only the signature, not the signed message (detached)
        }

        public bool Verify(byte[] message, byte[] signature, byte[] publicKey)
        {
            return PublicKeyAuth.VerifyDetached(signature, message, publicKey); // verifies the signature against the original message and public key
        }
    }
}
