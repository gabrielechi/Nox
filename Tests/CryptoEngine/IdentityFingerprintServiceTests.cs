using CryptoEngine.Interfaces;
using CryptoEngine.Services;
using System;
using System.Collections.Generic;
using System.Text;

namespace Tests.CryptoEngine
{
    public class IdentityFingerprintServiceTests
    {
        private readonly IIdentityFingerprintService _fingerprintService;

        public IdentityFingerprintServiceTests()
        {
            _fingerprintService = new IdentityFingerprintService();
        }

        [Fact]
        public void ComputeFingerprint_SameInputs_ReturnsSameFingerprint()
        {
            byte[] x25519 = Enumerable.Repeat((byte)1, 32).ToArray();
            byte[] ed25519 = Enumerable.Repeat((byte)2, 32).ToArray();

            byte[] first = _fingerprintService.ComputeFingerprint("Alice", x25519, ed25519);
            byte[] second = _fingerprintService.ComputeFingerprint("alice", x25519, ed25519);

            Assert.Equal(first, second);
        }

        [Fact]
        public void ComputeFingerprint_DifferentIdentityKey_ReturnsDifferentFingerprint()
        {
            byte[] x25519 = Enumerable.Repeat((byte)1, 32).ToArray();
            byte[] ed25519 = Enumerable.Repeat((byte)2, 32).ToArray();
            byte[] changedEd25519 = Enumerable.Repeat((byte)3, 32).ToArray();

            byte[] first = _fingerprintService.ComputeFingerprint("alice", x25519, ed25519);
            byte[] second = _fingerprintService.ComputeFingerprint("alice", x25519, changedEd25519);

            Assert.NotEqual(first, second);
        }

        [Fact]
        public void FormatFingerprint_ReturnsGroupedHex()
        {
            byte[] fingerprint = Enumerable.Range(0, 32)
                .Select(value => (byte)value)
                .ToArray();

            string formatted = _fingerprintService.FormatFingerprint(fingerprint);

            Assert.Equal("0001 0203 0405 0607 0809 0A0B 0C0D 0E0F", formatted);
        }

        [Fact]
        public void ComputeFingerprint_InvalidKeyLength_Throws()
        {
            byte[] invalidX25519 = new byte[31];
            byte[] ed25519 = new byte[32];

            Assert.Throws<ArgumentException>(() =>
                _fingerprintService.ComputeFingerprint("alice", invalidX25519, ed25519));
        }
    }
}
