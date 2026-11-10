namespace Client.Core.Models
{
    public class ClientAppSettings
    {
        public string Language { get; set; } = "it";
        public bool RememberServer { get; set; }
        public string RememberedServerUrl { get; set; } = string.Empty;
        public bool UseDefaultDownloadDirectory { get; set; }
        public string DefaultDownloadDirectory { get; set; } = string.Empty;
        // Release version for which the user chose "don't show again" (same key as the Windows client).
        public string DismissedUpdateVersion { get; set; } = string.Empty;
    }
}
