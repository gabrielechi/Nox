namespace Server.Entities.PreKeys
{
    public class OneTimePreKey
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public ApplicationUser User { get; set; } = default!;
        public int KeyId { get; set; }
        public byte[] PublicKey { get; set; } = Array.Empty<byte>();
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? ClaimedAtUtc { get; set; }
        public bool IsClaimed { get; set; }
    }
}
