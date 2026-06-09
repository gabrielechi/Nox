using CryptoEngine.Interfaces;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace CryptoEngine.Services
{
    // HKDF for key derivation, used for deriving encryption keys from MasterKey in E2EE sessions
    public class HkdfService : IHkdfService
    {
        public byte[] DeriveKey(byte[] masterKey, string info, int outputLength = 32)
        {
            byte[] infoBytes = System.Text.Encoding.UTF8.GetBytes(info);

            return HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, outputLength, salt: null, infoBytes);
        }
    }
}

