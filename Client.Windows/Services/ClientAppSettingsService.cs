using Client.Windows.Models;
using System.IO;
using System.Text.Json;

namespace Client.Windows.Services
{
    public class ClientAppSettingsService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        private readonly string _settingsPath;

        public ClientAppSettingsService()
        {
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _settingsPath = Path.Combine(appDataPath, "NOX", "settings.json");
        }

        public ClientAppSettings Load()
        {
            if (!File.Exists(_settingsPath))
                return new ClientAppSettings();

            string json = File.ReadAllText(_settingsPath);

            return JsonSerializer.Deserialize<ClientAppSettings>(json)
                ?? new ClientAppSettings();
        }

        public void Save(ClientAppSettings settings)
        {
            string? directory = Path.GetDirectoryName(_settingsPath);

            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(_settingsPath, json);
        }

        public void ClearRememberedServer()
        {
            ClientAppSettings settings = Load();
            settings.RememberServer = false;
            settings.RememberedServerUrl = string.Empty;
            Save(settings);
        }
    }
}
