namespace Client.Windows.Models
{
    public sealed class PreKeySynchronizationException : Exception
    {
        public PreKeySynchronizationException()
            : base(Texts.T("TheServerSOneTimePre"))
        {
        }
    }
}
