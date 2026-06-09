using System;
using System.Collections.Generic;
using System.Text;

namespace CryptoEngine.Interfaces
{
    public interface ISymmetricService
    {
        byte[] GenerateKey();
        byte[] Encrypt(byte[] plaintext, byte[] key);
        byte[] Decrypt(byte[] encryptedData, byte[] key);
    }
}
