using CryptoEngine.Interfaces;
using CryptoEngine.Models;
using CryptoEngine.Services;

namespace Tests.CryptoEngine
{
    public class X3dhServiceTests
    {
        private readonly IKeyPairService _keyPairService;
        private readonly IX3dhService _x3dhService;

        public X3dhServiceTests()
        {
            _keyPairService = new KeyPairService();
            IHkdfService hkdfService = new HkdfService();
            _x3dhService = new X3dhService(_keyPairService, hkdfService);
        }

        [Fact]
        public void CreateSecret_WithOneTimePreKey_AliceAndBobDeriveSameRootKey()
        {
            var aliceIdentity = _keyPairService.GenerateX25519KeyPair();
            var aliceSigning = _keyPairService.GenerateEd25519KeyPair();

            var bobIdentity = _keyPairService.GenerateX25519KeyPair();
            var bobSigning = _keyPairService.GenerateEd25519KeyPair();
            var bobSignedPreKey = _keyPairService.GenerateX25519KeyPair();
            var bobOneTimePreKey = _keyPairService.GenerateX25519KeyPair();

            int signedPreKeyId = 1;
            int oneTimePreKeyId = 10;

            byte[] signedPreKeyMessage = X3dhService.BuildSignedPreKeySignatureMessage(
                signedPreKeyId,
                bobSignedPreKey.PublicKey);

            byte[] signedPreKeySignature = _keyPairService.Sign(
                signedPreKeyMessage,
                bobSigning.PrivateKey);

            var bundle = new X3dhPreKeyBundle(
                RecipientUsername: "bob",
                RecipientX25519IdentityPublicKey: bobIdentity.PublicKey,
                RecipientEd25519IdentityPublicKey: bobSigning.PublicKey,
                SignedPreKeyId: signedPreKeyId,
                SignedPreKeyPublicKey: bobSignedPreKey.PublicKey,
                SignedPreKeySignature: signedPreKeySignature,
                OneTimePreKeyId: oneTimePreKeyId,
                OneTimePreKeyPublicKey: bobOneTimePreKey.PublicKey
            );

            var senderInput = new X3dhSenderSecretInput(
                SenderUsername: "alice",
                SenderX25519IdentityPrivateKey: aliceIdentity.PrivateKey,
                SenderX25519IdentityPublicKey: aliceIdentity.PublicKey,
                SenderEd25519IdentityPublicKey: aliceSigning.PublicKey,
                RecipientBundle: bundle
            );

            X3dhSecretResult aliceResult = _x3dhService.CreateSenderSecret(senderInput);

            var recipientInput = new X3dhRecipientSecretInput(
                RecipientUsername: "bob",
                RecipientX25519IdentityPrivateKey: bobIdentity.PrivateKey,
                SignedPreKeyPrivateKey: bobSignedPreKey.PrivateKey,
                SignedPreKeyId: signedPreKeyId,
                OneTimePreKeyPrivateKey: bobOneTimePreKey.PrivateKey,
                OneTimePreKeyId: oneTimePreKeyId,
                Header: aliceResult.Header
            );

            X3dhSecretResult bobResult = _x3dhService.CreateRecipientSecret(recipientInput);

            Assert.Equal(aliceResult.RootKey, bobResult.RootKey);
        }

        [Fact]
        public void CreateSecret_WithoutOneTimePreKey_AliceAndBobDeriveSameRootKey()
        {
            var aliceIdentity = _keyPairService.GenerateX25519KeyPair();
            var aliceSigning = _keyPairService.GenerateEd25519KeyPair();

            var bobIdentity = _keyPairService.GenerateX25519KeyPair();
            var bobSigning = _keyPairService.GenerateEd25519KeyPair();
            var bobSignedPreKey = _keyPairService.GenerateX25519KeyPair();

            int signedPreKeyId = 1;

            byte[] signedPreKeyMessage = X3dhService.BuildSignedPreKeySignatureMessage(
                signedPreKeyId,
                bobSignedPreKey.PublicKey);

            byte[] signedPreKeySignature = _keyPairService.Sign(
                signedPreKeyMessage,
                bobSigning.PrivateKey);

            var bundle = new X3dhPreKeyBundle(
                RecipientUsername: "bob",
                RecipientX25519IdentityPublicKey: bobIdentity.PublicKey,
                RecipientEd25519IdentityPublicKey: bobSigning.PublicKey,
                SignedPreKeyId: signedPreKeyId,
                SignedPreKeyPublicKey: bobSignedPreKey.PublicKey,
                SignedPreKeySignature: signedPreKeySignature,
                OneTimePreKeyId: null,
                OneTimePreKeyPublicKey: null
            );

            var senderInput = new X3dhSenderSecretInput(
                SenderUsername: "alice",
                SenderX25519IdentityPrivateKey: aliceIdentity.PrivateKey,
                SenderX25519IdentityPublicKey: aliceIdentity.PublicKey,
                SenderEd25519IdentityPublicKey: aliceSigning.PublicKey,
                RecipientBundle: bundle
            );

            X3dhSecretResult aliceResult = _x3dhService.CreateSenderSecret(senderInput);

            var recipientInput = new X3dhRecipientSecretInput(
                RecipientUsername: "bob",
                RecipientX25519IdentityPrivateKey: bobIdentity.PrivateKey,
                SignedPreKeyPrivateKey: bobSignedPreKey.PrivateKey,
                SignedPreKeyId: signedPreKeyId,
                OneTimePreKeyPrivateKey: null,
                OneTimePreKeyId: null,
                Header: aliceResult.Header
            );

            X3dhSecretResult bobResult = _x3dhService.CreateRecipientSecret(recipientInput);

            Assert.Equal(aliceResult.RootKey, bobResult.RootKey);
        }

        [Fact]
        public void CreateSenderSecret_InvalidSignedPreKeySignature_Throws()
        {
            var aliceIdentity = _keyPairService.GenerateX25519KeyPair();
            var aliceSigning = _keyPairService.GenerateEd25519KeyPair();

            var bobIdentity = _keyPairService.GenerateX25519KeyPair();
            var bobSigning = _keyPairService.GenerateEd25519KeyPair();
            var bobSignedPreKey = _keyPairService.GenerateX25519KeyPair();

            byte[] invalidSignature = new byte[64];

            var bundle = new X3dhPreKeyBundle(
                RecipientUsername: "bob",
                RecipientX25519IdentityPublicKey: bobIdentity.PublicKey,
                RecipientEd25519IdentityPublicKey: bobSigning.PublicKey,
                SignedPreKeyId: 1,
                SignedPreKeyPublicKey: bobSignedPreKey.PublicKey,
                SignedPreKeySignature: invalidSignature,
                OneTimePreKeyId: null,
                OneTimePreKeyPublicKey: null
            );

            var senderInput = new X3dhSenderSecretInput(
                SenderUsername: "alice",
                SenderX25519IdentityPrivateKey: aliceIdentity.PrivateKey,
                SenderX25519IdentityPublicKey: aliceIdentity.PublicKey,
                SenderEd25519IdentityPublicKey: aliceSigning.PublicKey,
                RecipientBundle: bundle
            );

            Assert.Throws<InvalidOperationException>(() =>
                _x3dhService.CreateSenderSecret(senderInput));
        }

        [Fact]
        public void CreateRecipientSecret_MismatchedSignedPreKeyId_Throws()
        {
            var aliceIdentity = _keyPairService.GenerateX25519KeyPair();
            var aliceSigning = _keyPairService.GenerateEd25519KeyPair();

            var bobIdentity = _keyPairService.GenerateX25519KeyPair();
            var bobSigning = _keyPairService.GenerateEd25519KeyPair();
            var bobSignedPreKey = _keyPairService.GenerateX25519KeyPair();

            int signedPreKeyId = 1;

            byte[] signedPreKeyMessage = X3dhService.BuildSignedPreKeySignatureMessage(
                signedPreKeyId,
                bobSignedPreKey.PublicKey);

            byte[] signedPreKeySignature = _keyPairService.Sign(
                signedPreKeyMessage,
                bobSigning.PrivateKey);

            var bundle = new X3dhPreKeyBundle(
                RecipientUsername: "bob",
                RecipientX25519IdentityPublicKey: bobIdentity.PublicKey,
                RecipientEd25519IdentityPublicKey: bobSigning.PublicKey,
                SignedPreKeyId: signedPreKeyId,
                SignedPreKeyPublicKey: bobSignedPreKey.PublicKey,
                SignedPreKeySignature: signedPreKeySignature,
                OneTimePreKeyId: null,
                OneTimePreKeyPublicKey: null
            );

            var senderInput = new X3dhSenderSecretInput(
                SenderUsername: "alice",
                SenderX25519IdentityPrivateKey: aliceIdentity.PrivateKey,
                SenderX25519IdentityPublicKey: aliceIdentity.PublicKey,
                SenderEd25519IdentityPublicKey: aliceSigning.PublicKey,
                RecipientBundle: bundle
            );

            X3dhSecretResult aliceResult = _x3dhService.CreateSenderSecret(senderInput);

            var recipientInput = new X3dhRecipientSecretInput(
                RecipientUsername: "bob",
                RecipientX25519IdentityPrivateKey: bobIdentity.PrivateKey,
                SignedPreKeyPrivateKey: bobSignedPreKey.PrivateKey,
                SignedPreKeyId: 999,
                OneTimePreKeyPrivateKey: null,
                OneTimePreKeyId: null,
                Header: aliceResult.Header
            );

            Assert.Throws<ArgumentException>(() =>
                _x3dhService.CreateRecipientSecret(recipientInput));
        }
    }
}