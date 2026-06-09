using Microsoft.AspNetCore.Identity;
using Server.Entities.PreKeys;
using Server.Entities.Transfers;

namespace Server.Entities.PreKeys
{
    public class ApplicationUser : IdentityUser<Guid>
    {
        public byte[] PayloadSalt { get; set; } = Array.Empty<byte>();
        public byte[] EncryptedKeyPayload { get; set; } = Array.Empty<byte>();
        public byte[] X25519PublicKey { get; set; } = Array.Empty<byte>();
        public byte[] Ed25519PublicKey { get; set; } = Array.Empty<byte>();
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? LastLoginAtUtc { get; set; }

        public List<SignedPreKey> SignedPreKeys { get; set; } = new List<SignedPreKey>();
        public List<OneTimePreKey> OneTimePreKeys { get; set; } = new List<OneTimePreKey>();

        public List<FileTransfer> SentTransfers { get; set; } = [];
        public List<FileTransfer> ReceivedTransfers { get; set; } = [];
    }
}
