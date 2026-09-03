using EntKube.Agent;
using Microsoft.Extensions.Configuration;

// The EntKube egress agent.
//
// Runs inside a network that is allowed to reach some endpoint EntKube cannot,
// and gives EntKube a way to use that permission — without anything being
// published inbound. The agent dials out to EntKube over HTTPS/WebSocket and
// holds the link open; EntKube then asks it to open TCP connections, which it
// grants only for hosts in its own local allowlist.
//
// Traffic is relayed as opaque bytes. TLS is negotiated end-to-end between
// EntKube and the destination, so this process cannot read or modify it.

IConfigurationRoot configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile(Environment.GetEnvironmentVariable("ENTKUBE_AGENT_CONFIG") ?? "agent.json", optional: true)
    .AddEnvironmentVariables("ENTKUBE_")
    .AddCommandLine(args)
    .Build();

AgentOptions options = new();
configuration.Bind(options);

void Log(string message)
    => Console.WriteLine($"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z  {message}");

List<string> problems = options.Validate();

if (problems.Count > 0)
{
    Console.Error.WriteLine("The EntKube agent cannot start:");

    foreach (string problem in problems)
    {
        Console.Error.WriteLine($"  - {problem}");
    }

    Console.Error.WriteLine();
    Console.Error.WriteLine("""
        Configure it with an agent.json next to the binary:

          {
            "ServerUrl": "https://entkube.example.com",
            "Token": "<token generated in EntKube>",
            "AllowedHosts": [
              "identity.example.com",
              "*.citycloud.com"
            ],
            "AllowedPorts": [443]
          }

        AllowedHosts is the security boundary: the agent refuses to connect to
        anything not listed, regardless of what EntKube asks for.
        """);

    return 1;
}

using CancellationTokenSource shutdown = new();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Log("Shutting down...");
    shutdown.Cancel();
};

AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();

Log($"EntKube egress agent starting. Server: {options.ServerUrl}");
Log($"Allowlist: {options.DescribeAllowlist()} on port(s) {string.Join(", ", options.AllowedPorts)}");

AgentClient client = new(options, Log);
await client.RunAsync(shutdown.Token);

Log("Stopped.");
return 0;
