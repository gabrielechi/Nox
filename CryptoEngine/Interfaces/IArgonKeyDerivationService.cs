using System;
using System.Collections.Generic;
using System.Text;

namespace CryptoEngine.Interfaces
{
    public interface IArgonKeyDerivationService
    {
        string HashPasswordForServer(string password);
        bool VerifyPasswordForServer(string password, string passwordHash);
        bool PasswordNeedsRehash(string passwordHash);
        byte[] GeneratePayloadSalt();
        byte[] DerivePayloadKey(string password, byte[] payloadSalt);
    }
}
