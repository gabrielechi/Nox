using CryptoEngine.Interfaces;
using System.Security.Cryptography;
using System.Text;

namespace CryptoEngine.Services
{
    public class IdentityFingerprintService : IIdentityFingerprintService
    {
        private const string Context = "NOX-FINGERPRINT-V1";
        private const int X25519PublicKeyLength = 32;
        private const int Ed25519PublicKeyLength = 32;

        public byte[] ComputeFingerprint(
            string username,
            byte[] x25519IdentityPublicKey,
            byte[] ed25519IdentityPublicKey)
        {
            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException("Username is required.", nameof(username));

            if (x25519IdentityPublicKey.Length != X25519PublicKeyLength)
                throw new ArgumentException($"X25519 identity public key must be {X25519PublicKeyLength} bytes.", nameof(x25519IdentityPublicKey));

            if (ed25519IdentityPublicKey.Length != Ed25519PublicKeyLength)
                throw new ArgumentException($"Ed25519 identity public key must be {Ed25519PublicKeyLength} bytes.", nameof(ed25519IdentityPublicKey));

            string normalizedUsername = username.Trim().ToLowerInvariant();

            byte[] contextBytes = Encoding.UTF8.GetBytes(Context);
            byte[] usernameBytes = Encoding.UTF8.GetBytes(normalizedUsername);

            byte[] input = contextBytes
                .Concat(BitConverter.GetBytes(usernameBytes.Length))
                .Concat(usernameBytes)
                .Concat(x25519IdentityPublicKey)
                .Concat(ed25519IdentityPublicKey)
                .ToArray();

            return SHA256.HashData(input);
        }

        public string FormatFingerprint(byte[] fingerprint, int bytesToShow = 16)
        {
            if (bytesToShow <= 0)
                throw new ArgumentOutOfRangeException(nameof(bytesToShow));

            if (bytesToShow > fingerprint.Length)
                throw new ArgumentException("Requested length exceeds fingerprint length.", nameof(bytesToShow));

            string hex = Convert.ToHexString(fingerprint[..bytesToShow]);

            return string.Join(
                " ",
                Enumerable.Range(0, hex.Length / 4)
                    .Select(index => hex.Substring(index * 4, 4)));
        }
    }
}