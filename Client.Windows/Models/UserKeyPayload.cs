namespace Client.Windows.Models
{
    public class UserKeyPayload
    {
        public int Version { get; set; } = 1;
        public byte[] X25519IdentityPublicKey { get; set; } = [];
        public byte[] X25519IdentityPrivateKey { get; set; } = [];
        public byte[] Ed25519IdentityPublicKey { get; set; } = [];
        public byte[] Ed25519IdentityPrivateKey { get; set; } = [];
        public ClientSignedPreKeyState? SignedPreKey { get; set; }
        public List<ClientOneTimePreKeyState> OneTimePreKeys { get; set; } = [];
        public int NextOneTimePreKeyId { get; set; } = 1;
        public List<TrustedContact> TrustedContacts { get; set; } = [];
    }
}