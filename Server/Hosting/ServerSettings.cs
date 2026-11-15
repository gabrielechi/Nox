using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Server.Hosting;

/// <summary>
/// Reads the "Server" section of appsettings.json (the only configuration file) and turns it into the
/// values the rest of the server already uses: database connection, storage folder, JWT key, listening
/// addresses. Values given as environment variables (e.g. Server__HttpPort=8080) win over the file.
/// </summary>
public sealed class ServerSettings
{
    private const string JwtKeyFileName = "jwt-secret.key";
    // Placeholder of the old appsettings.json: never accept it as a real signing key.
    private const string OldPlaceholderKey = "CHANGE_ME_DEV_SECRET_KEY_AT_LEAST_32_CHARS_LONG";

    public string ListenAddress { get; private init; } = "0.0.0.0";
    public int HttpPort { get; private init; }
    public int HttpsPort { get; private init; }
    public string CertificatePath { get; private init; } = string.Empty;
    public string CertificatePassword { get; private init; } = string.Empty;
    public string DataDirectory { get; private init; } = string.Empty;
    public string DatabasePath { get; private init; } = string.Empty;
    public string StoragePath { get; private init; } = string.Empty;
    public string LogDirectory { get; private init; } = string.Empty;
    public bool LogToFile { get; private init; }
    public bool ShowInboxPolling { get; private init; }
    public int LogRetentionDays { get; private init; }
    public int LoginAttemptsPerMinute { get; private init; }
    public int UsernameChecksPerMinute { get; private init; }

    /// <summary>Azure App Service (the noxft deployment): it chooses port and paths by itself.</summary>
    public bool IsAzureAppService { get; private init; }

    /// <summary>True when ports come from this file (not from Azure, launchSettings or ASPNETCORE_URLS).</summary>
    public bool UsesConfiguredEndpoints { get; private init; }

    public static ServerSettings Load(ConfigurationManager configuration)
    {
        IConfigurationSection server = configuration.GetSection("Server");
        bool azure = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME"));
        bool urlsFromEnvironment = !string.IsNullOrWhiteSpace(configuration["urls"]) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_HTTPS_PORTS"));

        // Data: a folder chosen in the file; otherwise "data" next to the server. On Azure the
        // previous paths (/home/data/e2ee.db, /home/storage) are kept, so existing data stays in use.
        string configuredData = server["DataDirectory"]?.Trim() ?? string.Empty;
        string dataDirectory, databasePath, storagePath;
        if (configuredData.Length > 0)
        {
            dataDirectory = Path.GetFullPath(configuredData, AppContext.BaseDirectory);
            databasePath = Path.Combine(dataDirectory, "e2ee.db");
            storagePath = Path.Combine(dataDirectory, "storage");
        }
        else if (azure)
        {
            dataDirectory = "/home/data";
            databasePath = "/home/data/e2ee.db";
            storagePath = "/home/storage";
        }
        else
        {
            dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
            databasePath = Path.Combine(dataDirectory, "e2ee.db");
            storagePath = Path.Combine(dataDirectory, "storage");
        }

        return new ServerSettings
        {
            ListenAddress = string.IsNullOrWhiteSpace(server["ListenAddress"]) ? "0.0.0.0" : server["ListenAddress"]!.Trim(),
            HttpPort = server.GetValue("HttpPort", 5000),
            HttpsPort = server.GetValue("HttpsPort", 0),
            CertificatePath = server["CertificatePath"]?.Trim() ?? string.Empty,
            CertificatePassword = server["CertificatePassword"] ?? string.Empty,
            DataDirectory = dataDirectory,
            DatabasePath = databasePath,
            StoragePath = storagePath,
            LogDirectory = Path.Combine(dataDirectory, "logs"),
            LogToFile = configuration.GetValue("Log:ToFile", true),
            ShowInboxPolling = configuration.GetValue("Log:ShowInboxPolling", false),
            LogRetentionDays = Math.Max(0, configuration.GetValue("Log:RetentionDays", 30)),
            LoginAttemptsPerMinute = Math.Max(1, configuration.GetValue("Security:LoginAttemptsPerMinute", 5)),
            UsernameChecksPerMinute = Math.Max(1, configuration.GetValue("Security:UsernameChecksPerMinute", 10)),
            IsAzureAppService = azure,
            UsesConfiguredEndpoints = !azure && !urlsFromEnvironment,
        };
    }

    /// <summary>
    /// Feeds the values to the existing code (connection string, storage, JWT) unless an environment
    /// variable already sets them, and creates the data folders.
    /// </summary>
    public void Apply(ConfigurationManager configuration)
    {
        Directory.CreateDirectory(DataDirectory);
        var values = new Dictionary<string, string?>();

        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("DefaultConnection")))
            values["ConnectionStrings:DefaultConnection"] = $"Data Source={DatabasePath}";
        if (string.IsNullOrWhiteSpace(configuration["Storage:RootPath"]))
            values["Storage:RootPath"] = StoragePath;
        if (string.IsNullOrWhiteSpace(configuration["Jwt:Issuer"]))
            values["Jwt:Issuer"] = "Nox.Server";
        if (string.IsNullOrWhiteSpace(configuration["Jwt:Audience"]))
            values["Jwt:Audience"] = "Nox.Client";
        if (string.IsNullOrWhiteSpace(configuration["Jwt:ExpirationMinutes"]))
            values["Jwt:ExpirationMinutes"] = "60";

        string? key = configuration["Jwt:SecretKey"];
        if (string.IsNullOrWhiteSpace(key) || key == OldPlaceholderKey || key.Length < 32)
            values["Jwt:SecretKey"] = LoadOrCreateJwtKey();

        configuration.AddInMemoryCollection(values);
    }

    /// <summary>Signing key for login tokens: created once with random bytes and kept in the data folder.</summary>
    private string LoadOrCreateJwtKey()
    {
        string path = Path.Combine(DataDirectory, JwtKeyFileName);
        if (File.Exists(path))
        {
            string existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 32) return existing;
        }

        string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        File.WriteAllText(path, key);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return key;
    }

    /// <summary>Address and ports from the file (only when no one else decides them).</summary>
    public void ConfigureKestrel(Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions options)
    {
        IPAddress address = ListenAddress switch
        {
            "0.0.0.0" or "*" or "" => IPAddress.Any,
            "::" => IPAddress.IPv6Any,
            "localhost" => IPAddress.Loopback,
            _ => IPAddress.Parse(ListenAddress)
        };

        if (HttpPort > 0)
            options.Listen(address, HttpPort);

        if (HttpsPort > 0)
        {
            if (string.IsNullOrWhiteSpace(CertificatePath))
                throw new InvalidOperationException(Logging.ServerText.L(
                    "Server:HttpsPort is set but Server:CertificatePath (.pfx certificate) is missing.",
                    "Server:HttpsPort è impostata ma manca Server:CertificatePath (certificato .pfx)."));
            string certificate = Path.GetFullPath(CertificatePath, AppContext.BaseDirectory);
            options.Listen(address, HttpsPort, listen => listen.UseHttps(certificate, CertificatePassword));
        }

        if (HttpPort <= 0 && HttpsPort <= 0)
            throw new InvalidOperationException(Logging.ServerText.L(
                "Set at least one of Server:HttpPort and Server:HttpsPort.",
                "Imposta almeno una tra Server:HttpPort e Server:HttpsPort."));
    }

    /// <summary>Addresses the server answers on (localhost and the network IPs), shown in the startup banner.</summary>
    public IReadOnlyList<string> ListeningAddresses()
    {
        var result = new List<string>();
        bool everywhere = ListenAddress is "0.0.0.0" or "*" or "" or "::";
        bool localOnly = ListenAddress is "127.0.0.1" or "localhost" or "::1";

        void Add(string scheme, int port)
        {
            if (port <= 0) return;
            if (everywhere || localOnly) result.Add($"{scheme}://localhost:{port}");
            if (localOnly) return;
            IEnumerable<IPAddress> addresses = everywhere ? LanAddresses() : [IPAddress.Parse(ListenAddress)];
            foreach (IPAddress ip in addresses)
                result.Add($"{scheme}://{ip}:{port}");
        }

        Add("http", HttpPort);
        Add("https", HttpsPort);
        return result;
    }

    private static IEnumerable<IPAddress> LanAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                              nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Select(unicast => unicast.Address)
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip) &&
                             !ip.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Distinct()
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }
}
