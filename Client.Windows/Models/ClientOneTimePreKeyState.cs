namespace Client.Windows.Models
{
    public class ClientOneTimePreKeyState
    {
        public int KeyId { get; set; }
        public byte[] PublicKey { get; set; } = [];
        public byte[] PrivateKey { get; set; } = [];
        public bool IsUploaded { get; set; }
        public bool IsUsed { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }
}