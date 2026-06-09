namespace Client.Windows.Models
{
    public class ClientAppSettings
    {
        public bool RememberServer { get; set; }
        public string RememberedServerUrl { get; set; } = string.Empty;
        public bool UseDefaultDownloadDirectory { get; set; }
        public string DefaultDownloadDirectory { get; set; } = string.Empty;
    }
}
