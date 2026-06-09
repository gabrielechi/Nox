using CryptoEngine.Interfaces;
using Sodium;
using System;
using System.Collections.Generic;
using System.Text;

namespace CryptoEngine.Services
{
    public class ArgonKeyDerivationService : IArgonKeyDerivationService
    {
        private const int PayloadKeyLength = 32;

        public string HashPasswordForServer(string password)
        {
            return PasswordHash.ArgonHashString(password, PasswordHash.StrengthArgon.Interactive);
        }

        public bool VerifyPasswordForServer(string password, string passwordHash)
        {
            return PasswordHash.ArgonHashStringVerify(passwordHash, password);
        }

        public bool PasswordNeedsRehash(string passwordHash)
        {
            return PasswordHash.ArgonPasswordNeedsRehash(passwordHash, PasswordHash.StrengthArgon.Interactive);
        }

        public byte[] GeneratePayloadSalt()
        {
            return PasswordHash.ArgonGenerateSalt();
        }

        public byte[] DerivePayloadKey(string password, byte[] payloadSalt)
        {
            return PasswordHash.ArgonHashBinary(
                Encoding.UTF8.GetBytes(password), 
                payloadSalt, 
                PasswordHash.StrengthArgon.Interactive, 
                PayloadKeyLength, 
                PasswordHash.ArgonAlgorithm.Argon_2ID13);
        }
    }
}

