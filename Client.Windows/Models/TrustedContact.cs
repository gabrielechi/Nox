namespace Client.Windows.Models
{
    public class TrustedContact
    {
        public string Username { get; set; } = string.Empty;
        public byte[] X25519IdentityPublicKey { get; set; } = [];
        public byte[] Ed25519IdentityPublicKey { get; set; } = [];
        public string Fingerprint { get; set; } = string.Empty;
        public DateTime TrustedAtUtc { get; set; }
    }
}