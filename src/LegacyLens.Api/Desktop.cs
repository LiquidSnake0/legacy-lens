using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace LegacyLens.Api;

/// <summary>
/// The one-file version: double-clicked, not deployed.
///
/// Everything else here assumes somebody set a machine up. That is right for a
/// team and wrong for the first conversation, where the sentence that ends the
/// meeting is *where would it run?*. A single file that opens a window and
/// reads a folder answers it before it is asked.
///
/// It is the same program. There is no second implementation of anything: the
/// executable carries the built interface beside the API, serves both from one
/// port, and opens a browser at it. What makes it a desktop application is that
/// nobody had to install a web server, a runtime or a database to get there.
///
/// **The model is not in it, and that is the honest half.** A local model is
/// gigabytes and cannot be handed over on a memory stick. What runs without one
/// is most of this tool: the map, the risk ranking, the mechanical conversions,
/// the usage surface, the framework reading, the assessment. Questions and
/// answers need Ollama, and the interface says so rather than failing at the
/// first click.
/// </summary>
internal static class Desktop
{
    /// <summary>
    /// Whether this process was double-clicked rather than deployed.
    ///
    /// Two conditions, both of which have to hold. The interface has to be
    /// beside the executable, which it only is in the single-file publish. And
    /// nobody may have said where to listen: every deployment here sets
    /// ASPNETCORE_URLS, and a server that opened a browser on the host would be
    /// a surprise at best.
    /// </summary>
    public static bool Wanted(IConfiguration configuration, string contentRoot) =>
        string.IsNullOrWhiteSpace(configuration["ASPNETCORE_URLS"])
        && string.IsNullOrWhiteSpace(configuration["URLS"])
        && Directory.Exists(Path.Combine(contentRoot, "wwwroot"));

    /// <summary>
    /// A port nobody else is on.
    ///
    /// Asked of the operating system rather than picked: a fixed port is the
    /// one thing guaranteed to collide on the machine of somebody who already
    /// runs something, and the failure would be at startup with nothing on
    /// screen to explain it.
    /// </summary>
    public static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>
    /// Opens the reader's own browser, and never fails the run if it cannot.
    ///
    /// The address is printed either way. A machine with no default browser, or
    /// a locked-down one, still has a working program and a line to paste.
    /// </summary>
    public static void Open(string url)
    {
        Console.WriteLine();
        Console.WriteLine($"  Legacy Lens is at {url}");
        Console.WriteLine("  Close this window to stop it.");
        Console.WriteLine();

        try
        {
            // UseShellExecute is what hands the URL to the desktop rather than
            // trying to execute it. Without it this throws on Windows.
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.WriteLine("  Could not open a browser for you. Open the address above.");
        }
    }

    /// <summary>
    /// What the interface should say about the model, before anybody clicks.
    ///
    /// Checked once at startup rather than discovered at the first question.
    /// Somebody handed this at the end of a meeting has ten minutes, and losing
    /// them to an error on the one screen that needed a download is how a
    /// demonstration ends.
    /// </summary>
    public static void ReportModel(bool reachable, string ollamaUrl)
    {
        if (reachable)
        {
            Console.WriteLine($"  A model is answering at {ollamaUrl}.");
            return;
        }

        Console.WriteLine($"  No model at {ollamaUrl}, so questions and answers are off.");
        Console.WriteLine("  Everything that needs no model still works: the map, the risk");
        Console.WriteLine("  ranking, the conversions, what holds a codebase back, and the");
        Console.WriteLine("  assessment. Install Ollama and restart to turn the rest on.");
        Console.WriteLine();
    }
}
