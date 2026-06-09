using System;
using System.Collections.Generic;
using System.Text;

namespace CryptoEngine.Interfaces
{
    public interface IIdentityFingerprintService
    {
        byte[] ComputeFingerprint(
            string username,
            byte[] x25519IdentityPublicKey,
            byte[] ed25519IdentityPublicKey);
        string FormatFingerprint(byte[] fingerprint, int bytesToShow = 16);
    }
}
