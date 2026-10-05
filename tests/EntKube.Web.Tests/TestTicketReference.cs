using EntKube.Web.Services.Tickets;

namespace EntKube.Web.Tests;

/// <summary>
/// The reference signer the tests use.
///
/// <para>A fixed key rather than a random one, so a token printed in a failure message is
/// the same token on the next run and can be pasted into a new test. It is not a secret
/// and must never be one in production, where the key is derived from
/// <c>Vault:RootKey</c>.</para>
/// </summary>
internal static class TestTicketReference
{
    public static TicketReference Instance { get; } =
        new([.. Enumerable.Range(0, 32).Select(i => (byte)i)]);
}
