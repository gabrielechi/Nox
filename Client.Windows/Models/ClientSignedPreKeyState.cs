namespace Client.Windows.Models
{
    public class ClientSignedPreKeyState
    {
        public int KeyId { get; set; }
        public byte[] PublicKey { get; set; } = [];
        public byte[] PrivateKey { get; set; } = [];
        public byte[] Signature { get; set; } = [];
        public DateTime CreatedAtUtc { get; set; }
    }
}