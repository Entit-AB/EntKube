namespace EntKube.Web.Data;

/// <summary>
/// The statements that make one instance wait while another migrates.
///
/// <para>⭐ Extracted from <c>Program.cs</c> so the per-provider mapping can be asserted. Startup
/// wiring is unreachable by any test, which is how a decision that only matters with two
/// instances ends up with nothing watching it — the same reason
/// <see cref="EntKube.Web.Services.SharedKeyRing"/> exists.</para>
///
/// <para><b>Why there is no portable answer.</b> Postgres has one global advisory-lock space per
/// database addressed by a number; SQL Server has named application locks; SQLite has neither,
/// and does not need one — it is a single file used as the local-development fallback, and two
/// processes sharing one SQLite file is not a deployment this supports.</para>
///
/// <para>⚠️ Both forms are released when the connection closes. That is the property that
/// matters: an instance killed mid-migration must not leave the next one waiting forever. It is
/// why the SQL Server lock asks for <c>@LockOwner = 'Session'</c> rather than
/// <c>'Transaction'</c> — the migration is several transactions, and a transaction-owned lock
/// would be released by the first one that committed.</para>
/// </summary>
public static class MigrationLock
{
    /// <summary>
    /// The advisory-lock number, which is the only thing identifying the lock. Derived from
    /// "ENTKUBE" so it is recognisable in <c>pg_locks</c>, and fixed because both instances have
    /// to ask for the same one.
    /// </summary>
    public const long Key = 0x45_4E_54_4B_55_42_45_01;

    /// <summary>The name SQL Server addresses the same lock by.</summary>
    public const string ResourceName = "EntKube:Migrations";

    /// <summary>
    /// A non-blocking attempt, or null when this provider takes no lock.
    ///
    /// <para>⚠️ Non-blocking on purpose: the caller polls with a bounded budget, so a wedged
    /// holder surfaces as a failed start the orchestrator can restart rather than an instance
    /// hanging with no further log line. <c>pg_advisory_lock</c> and a <c>@LockTimeout</c> of
    /// -1 would both wait forever.</para>
    /// </summary>
    public static string? TryAcquireSql(string? providerName) => providerName switch
    {
        "Npgsql.EntityFrameworkCore.PostgreSQL" => $"SELECT pg_try_advisory_lock({Key})",

        // Returns >= 0 when granted, -1 on timeout. SELECTed so ExecuteScalar can read it.
        "Microsoft.EntityFrameworkCore.SqlServer" =>
            "DECLARE @r int; EXEC @r = sp_getapplock "
            + $"@Resource = '{ResourceName}', @LockMode = 'Exclusive', "
            + "@LockOwner = 'Session', @LockTimeout = 0; SELECT @r;",

        _ => null,
    };

    /// <summary>The matching release, or null when nothing was taken.</summary>
    public static string? ReleaseSql(string? providerName) => providerName switch
    {
        "Npgsql.EntityFrameworkCore.PostgreSQL" => $"SELECT pg_advisory_unlock({Key})",
        "Microsoft.EntityFrameworkCore.SqlServer" =>
            $"EXEC sp_releaseapplock @Resource = '{ResourceName}', @LockOwner = 'Session';",
        _ => null,
    };

    /// <summary>
    /// Whether a provider that takes no lock is expected to, or is one nobody has taught this.
    ///
    /// <para>⚠️ The distinction is the point. SQLite silently taking no lock is correct; an
    /// unrecognised provider silently taking no lock is a gap, and the difference must reach a
    /// log rather than being folded into one "no lock here" branch.</para>
    /// </summary>
    public static bool NeedsNoLock(string? providerName)
        => providerName == "Microsoft.EntityFrameworkCore.Sqlite";

    /// <summary>
    /// Reads whatever the acquire statement returned. Postgres answers with a boolean,
    /// <c>sp_getapplock</c> with an int that is >= 0 when granted.
    /// </summary>
    public static bool WasGranted(object? scalar) => scalar switch
    {
        bool b => b,
        int i => i >= 0,
        long l => l >= 0,
        _ => false,
    };
}
