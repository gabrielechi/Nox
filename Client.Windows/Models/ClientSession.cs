namespace Client.Windows.Models
{
    public class ClientSession
    {
        public string ServerUrl { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Jwt { get; set; } = string.Empty;
        public byte[] PayloadSalt { get; set; } = [];
        public byte[] EncryptedKeyPayload { get; set; } = [];
        public byte[] X25519PublicKey { get; set; } = [];
        public byte[] Ed25519PublicKey { get; set; } = [];
    }
}