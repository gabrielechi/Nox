namespace Server.Entities.Transfers
{
    public class FileTransferItem
    {
        public Guid Id { get; set; }
        public Guid TransferId { get; set; }
        public FileTransfer Transfer { get; set; } = default!;
        public int FileIndex { get; set; }
        public byte[] FileHeader { get; set; } = Array.Empty<byte>();
        public string StorageObjectName { get; set; } = string.Empty;
        public long CiphertextLength { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? UploadedAtUtc { get; set; }
        public bool IsUploaded { get; set; }
    }
}
