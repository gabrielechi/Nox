namespace Shared.DTO.X3DH
{
    public class RecoverIdentityBundleRequest
    {
        public string Password { get; set; } = string.Empty;
        public byte[] EncryptedKeyPayload { get; set; } = [];
        public byte[] X25519IdentityPublicKey { get; set; } = [];
        public byte[] Ed25519IdentityPublicKey { get; set; } = [];
        public int SignedPreKeyId { get; set; }
        public byte[] SignedPreKeyPublicKey { get; set; } = [];
        public byte[] SignedPreKeySignature { get; set; } = [];
        public List<OneTimePreKeyDto> OneTimePreKeys { get; set; } = [];
    }
}
