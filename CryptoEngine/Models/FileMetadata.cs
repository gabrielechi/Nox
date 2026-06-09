namespace CryptoEngine.Models
{
    public class FileMetadata
    {
        public string OriginalFileName { get; set; } = string.Empty;
        public long PlaintextLength { get; set; }
        public string? ContentType { get; set; }
        public DateTime? LastModifiedUtc { get; set; }
    }
}