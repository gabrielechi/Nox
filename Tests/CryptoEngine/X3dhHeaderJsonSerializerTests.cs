using CryptoEngine.Interfaces;
using CryptoEngine.Models;
using CryptoEngine.Services;

namespace Tests.CryptoEngine
{
    public class X3dhHeaderJsonSerializerTests
    {
        private readonly IX3dhHeaderSerializer _serializer;

        public X3dhHeaderJsonSerializerTests()
        {
            _serializer = new X3dhHeaderJsonSerializer();
        }

        [Fact]
        public void SerializeAndDeserialize_WithOneTimePreKey_RoundTrips()
        {
            var header = new X3dhMessageHeader(
                Version: 1,
                TransferContextId: Guid.NewGuid(),
                SenderUsername: "alice",
                SenderX25519IdentityPublicKey: Enumerable.Repeat((byte)1, 32).ToArray(),
                SenderEd25519IdentityPublicKey: Enumerable.Repeat((byte)2, 32).ToArray(),
                SenderEphemeralPublicKey: Enumerable.Repeat((byte)3, 32).ToArray(),
                RecipientSignedPreKeyId: 7,
                RecipientOneTimePreKeyId: 9
            );

            byte[] serialized = _serializer.Serialize(header);

            X3dhMessageHeader deserialized = _serializer.Deserialize(serialized);

            AssertHeadersEqual(header, deserialized);
        }

        [Fact]
        public void SerializeAndDeserialize_WithoutOneTimePreKey_RoundTrips()
        {
            var header = new X3dhMessageHeader(
                Version: 1,
                TransferContextId: Guid.NewGuid(),
                SenderUsername: "alice",
                SenderX25519IdentityPublicKey: Enumerable.Repeat((byte)1, 32).ToArray(),
                SenderEd25519IdentityPublicKey: Enumerable.Repeat((byte)2, 32).ToArray(),
                SenderEphemeralPublicKey: Enumerable.Repeat((byte)3, 32).ToArray(),
                RecipientSignedPreKeyId: 7,
                RecipientOneTimePreKeyId: null
            );

            byte[] serialized = _serializer.Serialize(header);

            X3dhMessageHeader deserialized = _serializer.Deserialize(serialized);

            AssertHeadersEqual(header, deserialized);
        }

        [Fact]
        public void Deserialize_InvalidJson_Throws()
        {
            byte[] invalidJson = [1, 2, 3, 4];

            Assert.ThrowsAny<Exception>(() =>
                _serializer.Deserialize(invalidJson));
        }

        private static void AssertHeadersEqual(X3dhMessageHeader expected, X3dhMessageHeader actual)
        {
            Assert.Equal(expected.Version, actual.Version);
            Assert.Equal(expected.TransferContextId, actual.TransferContextId);
            Assert.Equal(expected.SenderUsername, actual.SenderUsername);
            Assert.Equal(expected.SenderX25519IdentityPublicKey, actual.SenderX25519IdentityPublicKey);
            Assert.Equal(expected.SenderEd25519IdentityPublicKey, actual.SenderEd25519IdentityPublicKey);
            Assert.Equal(expected.SenderEphemeralPublicKey, actual.SenderEphemeralPublicKey);
            Assert.Equal(expected.RecipientSignedPreKeyId, actual.RecipientSignedPreKeyId);
            Assert.Equal(expected.RecipientOneTimePreKeyId, actual.RecipientOneTimePreKeyId);
        }
    }
}