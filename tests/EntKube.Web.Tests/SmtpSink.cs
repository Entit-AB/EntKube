using System.Net;
using System.Net.Sockets;
using System.Text;
using MimeKit;

namespace EntKube.Web.Tests;

/// <summary>A message that actually went over a socket.</summary>
/// <param name="From">The envelope sender, as the server was told it.</param>
/// <param name="To">The envelope recipient.</param>
/// <param name="Message">The message as parsed back off the wire.</param>
public readonly record struct SentMail(string From, string To, MimeMessage Message);

/// <summary>
/// An SMTP server that accepts everything and keeps what it was given.
///
/// <para><b>Why a real socket and not a mock.</b> The code under test constructs its own
/// <c>SmtpClient</c>, so mocking would mean adding an abstraction whose only user is the
/// test — and the abstraction would then be the thing that was verified. What is actually
/// worth knowing is whether a message leaves: whether the address the tenant configured is
/// a valid sender, whether every recipient is reached, whether a malformed one stops the
/// others. All of that happens at the protocol, and a listener on loopback is enough to
/// see it.</para>
///
/// <para>It speaks only the handful of verbs MailKit needs and advertises no extensions,
/// so the client falls back to a plain conversation. It is not an SMTP implementation and
/// should never grow into one.</para>
/// </summary>
public sealed class SmtpSink : IDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stopping = new();
    private readonly List<SentMail> received = [];
    private readonly Lock guard = new();
    private readonly Task loop;

    /// <summary>Addresses the server refuses, to exercise a partial failure.</summary>
    public HashSet<string> Reject { get; } = new(StringComparer.OrdinalIgnoreCase);

    public SmtpSink()
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        loop = Task.Run(AcceptAsync);
    }

    public int Port { get; }

    public string Host => "127.0.0.1";

    /// <summary>Everything that arrived, in order.</summary>
    public IReadOnlyList<SentMail> Received
    {
        get { lock (guard) { return [.. received]; } }
    }

    public void Dispose()
    {
        stopping.Cancel();
        listener.Stop();

        try { loop.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutting down */ }

        stopping.Dispose();
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
            catch (OperationCanceledException) { return; }
            catch (SocketException) { return; }
            catch (ObjectDisposedException) { return; }

            _ = Task.Run(() => ConverseAsync(client));
        }
    }

    private async Task ConverseAsync(TcpClient client)
    {
        using TcpClient _ = client;
        using NetworkStream stream = client.GetStream();
        using StreamReader reader = new(stream, Encoding.UTF8);
        StreamWriter writer = new(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };

        await writer.WriteLineAsync("220 sink ready");

        string from = "";
        string to = "";

        while (await reader.ReadLineAsync() is string line)
        {
            string verb = line.Split(' ', 2)[0].ToUpperInvariant();

            switch (verb)
            {
                case "EHLO":
                    // No extensions: no STARTTLS to negotiate, no AUTH to attempt.
                    await writer.WriteLineAsync("250 sink");
                    break;

                case "HELO":
                    await writer.WriteLineAsync("250 sink");
                    break;

                case "MAIL":
                    from = Address(line);
                    await writer.WriteLineAsync("250 ok");
                    break;

                case "RCPT":
                    to = Address(line);

                    if (Reject.Contains(to))
                    {
                        await writer.WriteLineAsync("550 no such mailbox");
                        to = "";
                    }
                    else
                    {
                        await writer.WriteLineAsync("250 ok");
                    }

                    break;

                case "DATA":
                    await writer.WriteLineAsync("354 go ahead");
                    await ReadMessageAsync(reader, from, to);
                    await writer.WriteLineAsync("250 queued");
                    break;

                case "QUIT":
                    await writer.WriteLineAsync("221 bye");
                    return;

                case "RSET":
                    from = to = "";
                    await writer.WriteLineAsync("250 ok");
                    break;

                default:
                    await writer.WriteLineAsync("250 ok");
                    break;
            }
        }
    }

    private async Task ReadMessageAsync(StreamReader reader, string from, string to)
    {
        StringBuilder raw = new();

        while (await reader.ReadLineAsync() is string line && line != ".")
        {
            // Dot-stuffing: a line that began with a dot was doubled by the client.
            raw.AppendLine(line.StartsWith("..", StringComparison.Ordinal) ? line[1..] : line);
        }

        if (to.Length == 0)
        {
            return;      // every recipient was refused; nothing was delivered
        }

        using MemoryStream bytes = new(Encoding.UTF8.GetBytes(raw.ToString()));
        MimeMessage message = await MimeMessage.LoadAsync(bytes);

        lock (guard)
        {
            received.Add(new SentMail(from, to, message));
        }
    }

    /// <summary>The address out of <c>MAIL FROM:&lt;a@b&gt;</c> or <c>RCPT TO:&lt;a@b&gt;</c>.</summary>
    private static string Address(string line)
    {
        int open = line.IndexOf('<');
        int close = line.LastIndexOf('>');

        return open >= 0 && close > open ? line[(open + 1)..close] : "";
    }
}
