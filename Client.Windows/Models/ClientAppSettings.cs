namespace Client.Windows.Models
{
    public class ClientAppSettings
    {
        public string Language { get; set; } = "it";
        public bool RememberServer { get; set; }
        public string RememberedServerUrl { get; set; } = string.Empty;
        public bool UseDefaultDownloadDirectory { get; set; }
        public string DefaultDownloadDirectory { get; set; } = string.Empty;
        /// <summary>Release version (e.g. "3.1.0") whose update prompt the user chose not to see again.</summary>
        public string DismissedUpdateVersion { get; set; } = string.Empty;
    }
}
