namespace Server.Options
{
    public class TransferOptions
    {
        public const string SectionName = "Transfers";
        public long MaximumCiphertextLength { get; set; } = 10L * 1024 * 1024 * 1024;
    }
}
