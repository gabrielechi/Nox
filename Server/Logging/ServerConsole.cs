namespace Server.Logging;

public static class ServerConsole
{
    public static void ShowBanner(IEnumerable<string> addresses, string environment)
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

            NOX | Server di trasferimento file E2EE
            """);
        Console.WriteLine($"Ambiente: {environment} | Ascolto: {string.Join(", ", addresses)}");
        Console.WriteLine("------------------------------------------------------------");
    }
}
