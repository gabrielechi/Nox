using Server.Hosting;
using static Server.Logging.ServerText;

namespace Server.Logging;

public static class ServerConsole
{
    private const string Separator = "------------------------------------------------------------";

    public static void ShowBanner(IEnumerable<string> listening, ServerSettings settings, string configurationFile)
    {
        // Hosted log streams have no interactive console and must retain startup diagnostics.
        if (!Console.IsOutputRedirected)
        {
            try { Console.Clear(); }
            catch (IOException) { }
            catch (PlatformNotSupportedException) { }
        }

        Console.WriteLine("""
             _   _  _____  __   __
            | \ | ||  _  | \ \ / /
            |  \| || | | |  \ V /
            | . ` || | | |  /   \
            | |\  |\ \_/ / / /^\ \
            \_| \_/ \___/  \/   \/

            """);
        // Server version from <Version> in Server.csproj: 3.0 -> "v.3.0", 3.0.1 -> "v.3.0.1".
        Version version = typeof(ServerConsole).Assembly.GetName().Version ?? new Version(0, 0);
        string shown = "v." + version.ToString(version.Build > 0 ? 3 : 2);
        Console.WriteLine(L("NOX | E2EE file transfer server", "NOX | Server di trasferimento file E2EE") + " | " + shown);
        Console.WriteLine(Separator);

        IEnumerable<string> addresses = settings.UsesConfiguredEndpoints ? settings.ListeningAddresses() : listening;
        Console.WriteLine(L("Listening on:  ", "In ascolto su: ") + string.Join(", ", addresses));
        Console.WriteLine(Separator);
        Console.WriteLine(L("Configuration: ", "Configurazione: ") + configurationFile);
        Console.WriteLine(Separator);
        Console.WriteLine(L("Data:          ", "Dati:            ") + settings.DataDirectory);
        Console.WriteLine(Separator);
        if (settings.LogToFile)
        {
            Console.WriteLine(L("Log:           ", "Registro:        ") + settings.LogDirectory);
            Console.WriteLine(Separator);
        }
        Console.WriteLine(L("Press Ctrl+C to stop the server.", "Per fermare il server: Ctrl+C"));
        Console.WriteLine(Separator);
    }
}
