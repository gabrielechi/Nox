using Sodium;
using System;
using System.Collections.Generic;
using System.Text;

namespace CryptoEngine.Interfaces
{
    public interface IKeyPairService
    {
        KeyPair GenerateX25519KeyPair();
        KeyPair GenerateEd25519KeyPair();
        byte[] ComputeSharedSecret(byte[] myPrivateKey, byte[] theirPublicKey);
        byte[] Sign(byte[] message, byte[] privateKey);
        bool Verify(byte[] signedMessage, byte[] signature, byte[] publicKey);
    }
}
