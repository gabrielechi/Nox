using Server.Entities.PreKeys;

namespace Server.Entities.Transfers
{
    public class FileTransfer
    {
        public Guid Id { get; set; }
        public Guid SenderId { get; set; }
        public ApplicationUser Sender { get; set; } = default!;
        public Guid RecipientId { get; set; }
        public ApplicationUser Recipient { get; set; } = default!;
        public byte[] X3dhHeader { get; set; } = Array.Empty<byte>();
        public DateTime CreatedAtUtc { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public DateTime? DownloadedAtUtc { get; set; }
        public TransferStatus Status { get; set; }
        public List<FileTransferItem> Files { get; set; } = [];
    }
}
