using EntKube.Installer;

namespace EntKube.Web.Tests;

/// <summary>
/// Finding Docker Compose.
///
/// This exists because the check used to accept only the CLI plugin and reject anything named
/// <c>docker-compose</c> as "the end-of-life standalone binary" — which is wrong. Compose v2 ships
/// in both forms, and a v2 standalone build runs these files correctly. The old check refused
/// working hosts and told their operators something false about why.
///
/// The version is the only thing that settles it, so these tests are about reading it correctly.
/// </summary>
public class InstallerComposeTests
{
    [Theory]
    [InlineData("2.29.7", 2)]
    [InlineData("v2.29.7", 2)]
    [InlineData("2.0.0-rc.1", 2)]
    [InlineData("  2.29.7  \n", 2)]
    [InlineData("1.29.2", 1)]
    [InlineData("v1.29.2", 1)]
    [InlineData("10.1.0", 10)]
    public void The_major_version_is_read_from_whatever_form_it_is_printed_in(string text, int expected) =>
        Assert.Equal(expected, Docker.MajorVersion(text));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("docker: 'compose' is not a docker command.")]
    [InlineData("command not found")]
    public void Output_that_is_not_a_version_reads_as_no_version(string text) =>
        Assert.Equal(-1, Docker.MajorVersion(text));

    [Fact]
    public void A_version_that_cannot_be_read_is_never_treated_as_v2()
    {
        // The guard that matters: an unparseable answer must not fall through to "good enough".
        Assert.True(Docker.MajorVersion("docker: 'compose' is not a docker command.") < 2);
        Assert.True(Docker.MajorVersion(string.Empty) < 2);
    }

    [Fact]
    public void Both_invocations_describe_themselves_the_way_they_are_typed()
    {
        Assert.Equal("docker compose", ComposeInvocation.Plugin("2.29.7").Display);
        Assert.Equal("docker-compose", ComposeInvocation.Standalone("2.29.7").Display);
    }

    [Fact]
    public void The_plugin_form_runs_compose_as_a_docker_subcommand()
    {
        ComposeInvocation plugin = ComposeInvocation.Plugin("2.29.7");

        Assert.Equal("docker", plugin.File);
        Assert.Equal(["compose"], plugin.Prefix);
    }

    [Fact]
    public void The_standalone_form_runs_the_binary_directly()
    {
        ComposeInvocation standalone = ComposeInvocation.Standalone("2.29.7");

        Assert.Equal("docker-compose", standalone.File);
        Assert.Empty(standalone.Prefix);
    }

    // ── Resolution against a scripted target ─────────────────────────────────────────────────────

    [Fact]
    public void The_plugin_is_preferred_when_both_are_present()
    {
        FakeExecutor target = new();
        target.Respond("docker compose version --short", 0, "2.29.7");
        target.Respond("docker-compose version --short", 0, "2.29.7");

        ComposeResolution resolution = new Docker(target).ResolveCompose();

        Assert.True(resolution.Found);
        Assert.Equal("docker compose", resolution.Invocation!.Display);
    }

    [Fact]
    public void A_standalone_v2_is_accepted_when_the_plugin_is_absent()
    {
        // The reported bug: `docker-compose --version` shows v2, and the installer said no v2.
        FakeExecutor target = new();
        target.Respond("docker compose version --short", 1, "docker: 'compose' is not a docker command.");
        target.Respond("docker-compose version --short", 0, "2.29.7");

        ComposeResolution resolution = new Docker(target).ResolveCompose();

        Assert.True(resolution.Found);
        Assert.Equal("docker-compose", resolution.Invocation!.Display);
        Assert.Equal("2.29.7", resolution.Invocation.Version);
    }

    [Fact]
    public void A_standalone_v1_is_refused()
    {
        FakeExecutor target = new();
        target.Respond("docker compose version --short", 1, "docker: 'compose' is not a docker command.");
        target.Respond("docker-compose version --short", 0, "1.29.2");

        ComposeResolution resolution = new Docker(target).ResolveCompose();

        Assert.False(resolution.Found);
        Assert.Equal("1.29.2", resolution.StandaloneAttempt!.StdOut.Trim());
    }

    [Fact]
    public void Compose_that_works_only_without_sudo_is_identified_as_such()
    {
        // A plugin installed for the login user lives under that user's HOME, and sudo runs with
        // root's — so it works by hand and not through the installer.
        FakeExecutor target = new() { ElevationInUse = true };
        target.Respond("docker compose version --short", 1, "docker: 'compose' is not a docker command.");
        target.Respond("docker-compose version --short", 127, "not found");
        target.RespondUnelevated("docker compose version --short", 0, "2.29.7");

        ComposeResolution resolution = new Docker(target).ResolveCompose();

        Assert.False(resolution.Found);
        Assert.True(resolution.WorksWithoutElevation);
    }

    [Fact]
    public void Without_elevation_in_use_the_unelevated_probe_is_not_run()
    {
        // Nothing to learn from it, and it is a round trip on a connection that has already failed
        // twice.
        FakeExecutor target = new() { ElevationInUse = false };
        target.Respond("docker compose version --short", 1, "nope");
        target.Respond("docker-compose version --short", 127, "not found");

        ComposeResolution resolution = new Docker(target).ResolveCompose();

        Assert.False(resolution.WorksWithoutElevation);
        Assert.Empty(target.UnelevatedCalls);
    }

    [Fact]
    public void Resolution_happens_once_and_is_reused()
    {
        // Each probe is a round trip over SSH; repeating them for every Docker call would be
        // noticeable on a slow link.
        FakeExecutor target = new();
        target.Respond("docker compose version --short", 0, "2.29.7");

        Docker docker = new(target);
        docker.ResolveCompose();
        docker.ResolveCompose();

        Assert.Equal(1, target.Calls.Count(c => c.StartsWith("docker compose version")));
    }

    /// <summary>A target whose command responses are scripted, so resolution can be tested exactly.</summary>
    private sealed class FakeExecutor : IExecutor
    {
        private readonly Dictionary<string, ExecResult> _responses = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ExecResult> _unelevated = new(StringComparer.Ordinal);

        public List<string> Calls { get; } = [];

        public List<string> UnelevatedCalls { get; } = [];

        public string Target => "test-target";

        public bool IsLocal => false;

        public bool ElevationInUse { get; init; }

        public void Respond(string command, int exit, string output) =>
            _responses[command] = new ExecResult(exit, exit == 0 ? output : string.Empty,
                exit == 0 ? string.Empty : output);

        public void RespondUnelevated(string command, int exit, string output) =>
            _unelevated[command] = new ExecResult(exit, exit == 0 ? output : string.Empty,
                exit == 0 ? string.Empty : output);

        public ExecResult Run(
            string file, IReadOnlyList<string> args, TimeSpan? timeout = null, Action<string>? onLine = null)
        {
            string key = string.Join(' ', [file, .. args]);
            Calls.Add(key);

            return _responses.TryGetValue(key, out ExecResult? result)
                ? result
                : new ExecResult(127, string.Empty, "command not found");
        }

        public ExecResult RunUnelevated(string file, IReadOnlyList<string> args, TimeSpan? timeout = null)
        {
            string key = string.Join(' ', [file, .. args]);
            UnelevatedCalls.Add(key);

            return _unelevated.TryGetValue(key, out ExecResult? result)
                ? result
                : new ExecResult(127, string.Empty, "command not found");
        }

        public void WriteFile(string path, string content, bool secret = false) { }

        public string? ReadFile(string path) => null;

        public bool FileExists(string path) => false;

        public void EnsureWritableDirectory(string path) { }

        public bool IsPortFree(int port) => true;

        public int? ProbeHttp(string url) => null;

        public void Dispose() { }
    }
}
