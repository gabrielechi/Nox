namespace Client.Windows.Models
{
    public sealed class PreKeySynchronizationException : Exception
    {
        public PreKeySynchronizationException()
            : base("Il pool di one-time pre-key sul server non corrisponde al vault locale.")
        {
        }
    }
}
