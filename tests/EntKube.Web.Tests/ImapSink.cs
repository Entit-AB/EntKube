using System.Net;
using System.Net.Sockets;
using System.Text;

namespace EntKube.Web.Tests;

/// <summary>A folder the fake server offers, and the messages in it.</summary>
/// <param name="Name">Its name on the wire.</param>
/// <param name="SpecialUse">
/// The <c>SPECIAL-USE</c> attribute, e.g. <c>\Junk</c>, or null for an ordinary folder. This is the
/// thing worth testing: the Junk sweep finds its folder by attribute rather than by name, because the
/// folder is "Junk" on one server and "Junk Mail" on the next.
/// </param>
public sealed record ImapFolder(string Name, string? SpecialUse)
{
    /// <summary>Messages, in UID order. The index plus one is the UID.</summary>
    public List<string> Messages { get; } = [];
}

/// <summary>
/// An IMAP server that serves a fixed set of folders and messages.
///
/// <para><b>Why a real socket and not a mock.</b> Same reason as <see cref="SmtpSink"/>: the code
/// under test constructs its own <c>ImapClient</c>, so mocking would mean adding an abstraction whose
/// only user is the test, and the abstraction would become the thing verified. What is actually worth
/// knowing about the Junk sweep is whether it finds the Junk folder at all — it looks it up by
/// <c>SPECIAL-USE</c>, and if a server does not advertise that attribute the sweep silently does
/// nothing and a customer's support mail is lost with no trace. That is a protocol fact, and only a
/// conversation can show it.</para>
///
/// <para>It speaks the handful of commands MailKit needs for this flow and nothing else, advertises no
/// extensions beyond <c>SPECIAL-USE</c>, and is not an IMAP implementation. It should never grow into
/// one.</para>
/// </summary>
public sealed class ImapSink : IDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stopping = new();
    private readonly Task loop;

    /// <summary>Folders the server offers. Mutate before connecting.</summary>
    public List<ImapFolder> Folders { get; } =
    [
        new("INBOX", null),
    ];

    /// <summary>Commands the server was sent, for asserting what the client actually asked.</summary>
    public List<string> Commands { get; } = [];

    public int Port { get; }

    public ImapSink()
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        loop = Task.Run(AcceptAsync);
    }

    private async Task AcceptAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await listener.AcceptTcpClientAsync(stopping.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using TcpClient owned = client;
        await using NetworkStream stream = owned.GetStream();
        using StreamReader reader = new(stream, Encoding.ASCII);
        await using StreamWriter writer = new(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

        await writer.WriteLineAsync("* OK [CAPABILITY IMAP4rev1 SPECIAL-USE] ready");

        string? line;
        ImapFolder? selected = null;

        while ((line = await reader.ReadLineAsync()) is not null)
        {
            lock (Commands)
            {
                Commands.Add(line);
            }

            string[] parts = line.Split(' ', 3);
            string tag = parts[0];
            string command = parts.Length > 1 ? parts[1].ToUpperInvariant() : "";
            string rest = parts.Length > 2 ? parts[2] : "";

            switch (command)
            {
                case "CAPABILITY":
                    await writer.WriteLineAsync("* CAPABILITY IMAP4rev1 SPECIAL-USE");
                    await writer.WriteLineAsync($"{tag} OK done");
                    break;

                case "LOGIN":
                case "AUTHENTICATE":
                    await writer.WriteLineAsync($"{tag} OK signed in");
                    break;

                case "LIST":
                case "XLIST":
                    // The attribute is the point: MailKit reads SPECIAL-USE from here, and it is how
                    // GetFolder(SpecialFolder.Junk) resolves a folder whose name it cannot guess.
                    foreach (ImapFolder folder in Folders)
                    {
                        string attributes = folder.SpecialUse is null ? "" : folder.SpecialUse;
                        await writer.WriteLineAsync($"* LIST ({attributes}) \".\" \"{folder.Name}\"");
                    }
                    await writer.WriteLineAsync($"{tag} OK done");
                    break;

                case "SELECT":
                case "EXAMINE":
                    selected = Folders.FirstOrDefault(f =>
                        rest.Trim('"').Equals(f.Name, StringComparison.OrdinalIgnoreCase));

                    if (selected is null)
                    {
                        await writer.WriteLineAsync($"{tag} NO no such folder");
                        break;
                    }

                    await writer.WriteLineAsync($"* {selected.Messages.Count} EXISTS");
                    await writer.WriteLineAsync("* OK [UIDVALIDITY 1] ok");
                    await writer.WriteLineAsync($"* OK [UIDNEXT {selected.Messages.Count + 1}] ok");
                    await writer.WriteLineAsync($"{tag} OK [READ-ONLY] selected");
                    break;

                case "UID":
                    await ServeUidAsync(writer, tag, rest, selected);
                    break;

                case "CLOSE":
                    selected = null;
                    await writer.WriteLineAsync($"{tag} OK closed");
                    break;

                case "LOGOUT":
                    await writer.WriteLineAsync("* BYE");
                    await writer.WriteLineAsync($"{tag} OK done");
                    return;

                default:
                    await writer.WriteLineAsync($"{tag} OK done");
                    break;
            }
        }
    }

    private static async Task ServeUidAsync(
        StreamWriter writer, string tag, string rest, ImapFolder? selected)
    {
        string sub = rest.Split(' ')[0].ToUpperInvariant();

        if (selected is null)
        {
            await writer.WriteLineAsync($"{tag} NO nothing selected");
            return;
        }

        if (sub == "SEARCH")
        {
            // Every message matches. The window the sweep asks for is asserted by inspecting the
            // command rather than by implementing date arithmetic here — the server proving it
            // understands SENTSINCE would only be proving the server.
            IEnumerable<int> uids = Enumerable.Range(1, selected.Messages.Count);
            await writer.WriteLineAsync($"* SEARCH {string.Join(' ', uids)}");
            await writer.WriteLineAsync($"{tag} OK done");
            return;
        }

        if (sub == "FETCH")
        {
            foreach ((string raw, int index) in selected.Messages.Select((m, i) => (m, i)))
            {
                byte[] bytes = Encoding.ASCII.GetBytes(raw);
                await writer.WriteLineAsync(
                    $"* {index + 1} FETCH (UID {index + 1} BODY[] {{{bytes.Length}}}");
                await writer.WriteAsync(raw);
                await writer.WriteLineAsync(")");
            }

            await writer.WriteLineAsync($"{tag} OK done");
            return;
        }

        await writer.WriteLineAsync($"{tag} OK done");
    }

    public void Dispose()
    {
        stopping.Cancel();
        listener.Stop();

        try
        {
            loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Shutting down; the accept loop throwing on a stopped listener is expected.
        }

        stopping.Dispose();
    }
}
