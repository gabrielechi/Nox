using CryptoEngine.Interfaces;
using CryptoEngine.Models;
using CryptoEngine.Services;
using System.Text;

namespace Tests.CryptoEngine
{
    public class EndToEndFileTransferCryptoTests
    {
        private readonly IKeyPairService _keyPairService;
        private readonly IX3dhService _x3dhService;
        private readonly IFileKeyDerivationService _fileKeyDerivationService;
        private readonly IFileMetadataProtector _fileMetadataProtector;
        private readonly IFileContentEncryptionService _fileContentEncryptionService;

        public EndToEndFileTransferCryptoTests()
        {
            _keyPairService = new KeyPairService();

            IHkdfService hkdfService = new HkdfService();
            ISymmetricService symmetricService = new AesGcmSymmetricService();

            _x3dhService = new X3dhService(_keyPairService, hkdfService);
            _fileKeyDerivationService = new FileKeyDerivationService(hkdfService);
            _fileMetadataProtector = new FileMetadataProtector(symmetricService);
            _fileContentEncryptionService = new FileContentEncryptionService(symmetricService);
        }

        [Fact]
        public async Task AliceEncryptsFile_BobDecryptsSameFile_WithX3dhAndOneTimePreKey()
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

            var bobBundle = new X3dhPreKeyBundle(
                RecipientUsername: "bob",
                RecipientX25519IdentityPublicKey: bobIdentity.PublicKey,
                RecipientEd25519IdentityPublicKey: bobSigning.PublicKey,
                SignedPreKeyId: signedPreKeyId,
                SignedPreKeyPublicKey: bobSignedPreKey.PublicKey,
                SignedPreKeySignature: signedPreKeySignature,
                OneTimePreKeyId: oneTimePreKeyId,
                OneTimePreKeyPublicKey: bobOneTimePreKey.PublicKey);

            X3dhSecretResult aliceSecret = _x3dhService.CreateSenderSecret(
                new X3dhSenderSecretInput(
                    SenderUsername: "alice",
                    SenderX25519IdentityPrivateKey: aliceIdentity.PrivateKey,
                    SenderX25519IdentityPublicKey: aliceIdentity.PublicKey,
                    SenderEd25519IdentityPublicKey: aliceSigning.PublicKey,
                    RecipientBundle: bobBundle));

            int fileIndex = 0;

            byte[] originalPlaintext = Encoding.UTF8.GetBytes(
                "Questo e' il contenuto del file cifrato end-to-end.");

            var originalMetadata = new FileMetadata
            {
                OriginalFileName = "tesi.txt",
                PlaintextLength = originalPlaintext.Length,
                ContentType = "text/plain",
                LastModifiedUtc = new DateTime(2026, 6, 4, 12, 0, 0, DateTimeKind.Utc)
            };

            FileDerivedKeys aliceFileKeys = _fileKeyDerivationService.DeriveFileKeys(
                aliceSecret.RootKey,
                aliceSecret.Header.TransferContextId,
                fileIndex);

            byte[] encryptedMetadata = _fileMetadataProtector.Protect(
                originalMetadata,
                aliceFileKeys.MetadataKey);

            using var plaintextStream = new MemoryStream(originalPlaintext);
            using var encryptedContentStream = new MemoryStream();

            await _fileContentEncryptionService.EncryptAsync(
                plaintextStream,
                encryptedContentStream,
                aliceFileKeys.FileKey);

            X3dhSecretResult bobSecret = _x3dhService.CreateRecipientSecret(
                new X3dhRecipientSecretInput(
                    RecipientUsername: "bob",
                    RecipientX25519IdentityPrivateKey: bobIdentity.PrivateKey,
                    SignedPreKeyPrivateKey: bobSignedPreKey.PrivateKey,
                    SignedPreKeyId: signedPreKeyId,
                    OneTimePreKeyPrivateKey: bobOneTimePreKey.PrivateKey,
                    OneTimePreKeyId: oneTimePreKeyId,
                    Header: aliceSecret.Header));

            FileDerivedKeys bobFileKeys = _fileKeyDerivationService.DeriveFileKeys(
                bobSecret.RootKey,
                bobSecret.Header.TransferContextId,
                fileIndex);

            FileMetadata decryptedMetadata = _fileMetadataProtector.Unprotect(
                encryptedMetadata,
                bobFileKeys.MetadataKey);

            encryptedContentStream.Position = 0;

            using var decryptedContentStream = new MemoryStream();

            await _fileContentEncryptionService.DecryptAsync(
                encryptedContentStream,
                decryptedContentStream,
                bobFileKeys.FileKey);

            Assert.Equal(aliceSecret.RootKey, bobSecret.RootKey);
            Assert.Equal(originalMetadata.OriginalFileName, decryptedMetadata.OriginalFileName);
            Assert.Equal(originalMetadata.PlaintextLength, decryptedMetadata.PlaintextLength);
            Assert.Equal(originalMetadata.ContentType, decryptedMetadata.ContentType);
            Assert.Equal(
                originalMetadata.LastModifiedUtc?.ToUniversalTime().ToString("O"),
                decryptedMetadata.LastModifiedUtc?.ToUniversalTime().ToString("O"));
            Assert.Equal(originalPlaintext, decryptedContentStream.ToArray());
        }
    }
}