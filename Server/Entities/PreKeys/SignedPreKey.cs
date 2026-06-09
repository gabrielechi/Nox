namespace Server.Entities.PreKeys
{
    // Signed X25519 Pre-key
    public class SignedPreKey
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public ApplicationUser User { get; set; } = default!;
        public int KeyId { get; set; }
        public byte[] PublicKey { get; set; } = Array.Empty<byte>();
        public byte[] Signature { get; set; } = Array.Empty<byte>(); // Signature = SignEd25519(SignedPreKey.PublicKey)
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? ExpiresAtUtc { get; set; }

    }
}
