using CryptoEngine.Interfaces;
using CryptoEngine.Models;
using System.Text;

namespace CryptoEngine.Services
{
    public class X3dhService : IX3dhService
    {
        private const int PublicKeyLength = 32;
        private const int PrivateKeyLength = 32;
        private const int SignatureLength = 64;
        private const int RootKeyLength = 32;
        private const int HeaderVersion = 1;

        private const string SignedPreKeySignatureContext = "NOX-X3DH-SPK-SIGNATURE-V1";
        private const string HkdfInfoWithOpk = "NOX-X3DH-ROOT-V1-WITH-OPK";
        private const string HkdfInfoWithoutOpk = "NOX-X3DH-ROOT-V1-WITHOUT-OPK";

        private readonly IKeyPairService _keyPairService;
        private readonly IHkdfService _hkdfService;

        public X3dhService(
            IKeyPairService keyPairService,
            IHkdfService hkdfService)
        {
            _keyPairService = keyPairService;
            _hkdfService = hkdfService;
        }

        public X3dhSecretResult CreateSenderSecret(X3dhSenderSecretInput input)
        {
            ValidateSenderInput(input);

            byte[] signedPreKeySignatureMessage = BuildSignedPreKeySignatureMessage(
                input.RecipientBundle.SignedPreKeyId,
                input.RecipientBundle.SignedPreKeyPublicKey);

            bool signedPreKeyIsValid = _keyPairService.Verify(
                signedPreKeySignatureMessage,
                input.RecipientBundle.SignedPreKeySignature,
                input.RecipientBundle.RecipientEd25519IdentityPublicKey);

            if (!signedPreKeyIsValid)
                throw new InvalidOperationException("Recipient signed prekey signature is invalid.");

            var ephemeralKeyPair = _keyPairService.GenerateX25519KeyPair();

            byte[] dh1 = _keyPairService.ComputeSharedSecret(
                input.SenderX25519IdentityPrivateKey,
                input.RecipientBundle.SignedPreKeyPublicKey);

            byte[] dh2 = _keyPairService.ComputeSharedSecret(
                ephemeralKeyPair.PrivateKey,
                input.RecipientBundle.RecipientX25519IdentityPublicKey);

            byte[] dh3 = _keyPairService.ComputeSharedSecret(
                ephemeralKeyPair.PrivateKey,
                input.RecipientBundle.SignedPreKeyPublicKey);

            byte[]? dh4 = null;

            if (input.RecipientBundle.OneTimePreKeyPublicKey is not null)
            {
                dh4 = _keyPairService.ComputeSharedSecret(
                    ephemeralKeyPair.PrivateKey,
                    input.RecipientBundle.OneTimePreKeyPublicKey);
            }

            byte[] rootKey = DeriveRootKey(dh1, dh2, dh3, dh4);

            var header = new X3dhMessageHeader(
                Version: HeaderVersion,
                TransferContextId: Guid.NewGuid(),
                SenderUsername: input.SenderUsername,
                SenderX25519IdentityPublicKey: input.SenderX25519IdentityPublicKey,
                SenderEd25519IdentityPublicKey: input.SenderEd25519IdentityPublicKey,
                SenderEphemeralPublicKey: ephemeralKeyPair.PublicKey,
                RecipientSignedPreKeyId: input.RecipientBundle.SignedPreKeyId,
                RecipientOneTimePreKeyId: input.RecipientBundle.OneTimePreKeyId
            );

            return new X3dhSecretResult(rootKey, header);
        }

        public X3dhSecretResult CreateRecipientSecret(X3dhRecipientSecretInput input)
        {
            ValidateRecipientInput(input);

            bool headerUsesOneTimePreKey = input.Header.RecipientOneTimePreKeyId is not null;

            if (headerUsesOneTimePreKey && input.OneTimePreKeyPrivateKey is null)
                throw new ArgumentException("Header references a one-time prekey but no one-time prekey private key was provided.");

            if (input.Header.RecipientSignedPreKeyId != input.SignedPreKeyId)
                throw new ArgumentException("Signed prekey id does not match the message header.");

            if (input.Header.RecipientOneTimePreKeyId != input.OneTimePreKeyId)
                throw new ArgumentException("One-time prekey id does not match the message header.");

            byte[] dh1 = _keyPairService.ComputeSharedSecret(
                input.SignedPreKeyPrivateKey,
                input.Header.SenderX25519IdentityPublicKey);

            byte[] dh2 = _keyPairService.ComputeSharedSecret(
                input.RecipientX25519IdentityPrivateKey,
                input.Header.SenderEphemeralPublicKey);

            byte[] dh3 = _keyPairService.ComputeSharedSecret(
                input.SignedPreKeyPrivateKey,
                input.Header.SenderEphemeralPublicKey);

            byte[]? dh4 = null;

            if (input.OneTimePreKeyPrivateKey is not null)
            {
                dh4 = _keyPairService.ComputeSharedSecret(
                    input.OneTimePreKeyPrivateKey,
                    input.Header.SenderEphemeralPublicKey);
            }

            byte[] rootKey = DeriveRootKey(dh1, dh2, dh3, dh4);

            return new X3dhSecretResult(rootKey, input.Header);
        }

        public static byte[] BuildSignedPreKeySignatureMessage(int signedPreKeyId, byte[] signedPreKeyPublicKey)
        {
            if (signedPreKeyId < 0)
                throw new ArgumentOutOfRangeException(nameof(signedPreKeyId));

            if (signedPreKeyPublicKey.Length != PublicKeyLength)
                throw new ArgumentException($"Signed prekey public key must be {PublicKeyLength} bytes.", nameof(signedPreKeyPublicKey));

            byte[] contextBytes = Encoding.UTF8.GetBytes(SignedPreKeySignatureContext);
            byte[] keyIdBytes = BitConverter.GetBytes(signedPreKeyId);

            return contextBytes
                .Concat(keyIdBytes)
                .Concat(signedPreKeyPublicKey)
                .ToArray();
        }

        private byte[] DeriveRootKey(byte[] dh1, byte[] dh2, byte[] dh3, byte[]? dh4)
        {
            byte[] inputKeyMaterial = dh4 is null
                ? dh1.Concat(dh2).Concat(dh3).ToArray()
                : dh1.Concat(dh2).Concat(dh3).Concat(dh4).ToArray();

            string info = dh4 is null
                ? HkdfInfoWithoutOpk
                : HkdfInfoWithOpk;

            return _hkdfService.DeriveKey(inputKeyMaterial, info, RootKeyLength);
        }

        private static void ValidateSenderInput(X3dhSenderSecretInput input)
        {
            if (string.IsNullOrWhiteSpace(input.SenderUsername))
                throw new ArgumentException("Sender username is required.", nameof(input));

            if (input.SenderX25519IdentityPrivateKey.Length != PrivateKeyLength)
                throw new ArgumentException($"Sender X25519 identity private key must be {PrivateKeyLength} bytes.", nameof(input));

            if (input.SenderX25519IdentityPublicKey.Length != PublicKeyLength)
                throw new ArgumentException($"Sender X25519 identity public key must be {PublicKeyLength} bytes.", nameof(input));

            if (input.SenderEd25519IdentityPublicKey.Length != PublicKeyLength)
                throw new ArgumentException($"Sender Ed25519 identity public key must be {PublicKeyLength} bytes.", nameof(input));

            ValidatePreKeyBundle(input.RecipientBundle);
        }

        private static void ValidateRecipientInput(X3dhRecipientSecretInput input)
        {
            if (string.IsNullOrWhiteSpace(input.RecipientUsername))
                throw new ArgumentException("Recipient username is required.", nameof(input));

            if (input.RecipientX25519IdentityPrivateKey.Length != PrivateKeyLength)
                throw new ArgumentException($"Recipient X25519 identity private key must be {PrivateKeyLength} bytes.", nameof(input));

            if (input.SignedPreKeyPrivateKey.Length != PrivateKeyLength)
                throw new ArgumentException($"Signed prekey private key must be {PrivateKeyLength} bytes.", nameof(input));

            if (input.OneTimePreKeyPrivateKey is not null &&
                input.OneTimePreKeyPrivateKey.Length != PrivateKeyLength)
                throw new ArgumentException($"One-time prekey private key must be {PrivateKeyLength} bytes.", nameof(input));

            ValidateHeader(input.Header);
        }

        private static void ValidatePreKeyBundle(X3dhPreKeyBundle bundle)
        {
            if (string.IsNullOrWhiteSpace(bundle.RecipientUsername))
                throw new ArgumentException("Recipient username is required.", nameof(bundle));

            if (bundle.RecipientX25519IdentityPublicKey.Length != PublicKeyLength)
                throw new ArgumentException($"Recipient X25519 identity public key must be {PublicKeyLength} bytes.", nameof(bundle));

            if (bundle.RecipientEd25519IdentityPublicKey.Length != PublicKeyLength)
                throw new ArgumentException($"Recipient Ed25519 identity public key must be {PublicKeyLength} bytes.", nameof(bundle));

            if (bundle.SignedPreKeyId < 0)
                throw new ArgumentException("Signed prekey id must be non-negative.", nameof(bundle));

            if (bundle.SignedPreKeyPublicKey.Length != PublicKeyLength)
                throw new ArgumentException($"Signed prekey public key must be {PublicKeyLength} bytes.", nameof(bundle));

            if (bundle.SignedPreKeySignature.Length != SignatureLength)
                throw new ArgumentException($"Signed prekey signature must be {SignatureLength} bytes.", nameof(bundle));

            if (bundle.OneTimePreKeyId is not null &&
                bundle.OneTimePreKeyId < 0)
                throw new ArgumentException("One-time prekey id must be non-negative.", nameof(bundle));

            if (bundle.OneTimePreKeyPublicKey is not null &&
                bundle.OneTimePreKeyPublicKey.Length != PublicKeyLength)
                throw new ArgumentException($"One-time prekey public key must be {PublicKeyLength} bytes.", nameof(bundle));
        }

        private static void ValidateHeader(X3dhMessageHeader header)
        {
            if (header.Version != HeaderVersion)
                throw new ArgumentException("Unsupported X3DH header version.", nameof(header));

            if (header.TransferContextId == Guid.Empty)
                throw new ArgumentException("Transfer context id is required.", nameof(header));

            if (string.IsNullOrWhiteSpace(header.SenderUsername))
                throw new ArgumentException("Sender username is required.", nameof(header));

            if (header.SenderX25519IdentityPublicKey.Length != PublicKeyLength)
                throw new ArgumentException($"Sender X25519 identity public key must be {PublicKeyLength} bytes.", nameof(header));

            if (header.SenderEd25519IdentityPublicKey.Length != PublicKeyLength)
                throw new ArgumentException($"Sender Ed25519 identity public key must be {PublicKeyLength} bytes.", nameof(header));

            if (header.SenderEphemeralPublicKey.Length != PublicKeyLength)
                throw new ArgumentException($"Sender ephemeral public key must be {PublicKeyLength} bytes.", nameof(header));

            if (header.RecipientSignedPreKeyId < 0)
                throw new ArgumentException("Recipient signed prekey id must be non-negative.", nameof(header));

            if (header.RecipientOneTimePreKeyId is not null &&
                header.RecipientOneTimePreKeyId < 0)
                throw new ArgumentException("Recipient one-time prekey id must be non-negative.", nameof(header));
        }
    }
}
