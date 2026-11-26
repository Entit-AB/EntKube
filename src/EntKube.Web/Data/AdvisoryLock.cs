using System.Globalization;

namespace EntKube.Web.Data;

/// <summary>
/// A database-held lock that identifies which instance is doing something exactly one of them
/// should be doing.
///
/// <para>⭐ <b>Generalised out of <see cref="MigrationLock"/>, which already had all of this for
/// one purpose.</b> docs/decomposition.md §9.4 states "No lease primitive. No leader election, no
/// advisory lock, no distributed lock" — that was true when it was written and is not true now:
/// migrations have taken a real advisory lock since the two-instance work. The only thing stopping
/// a second purpose from using it was the hardcoded key, so the key is a parameter and
/// <c>MigrationLock</c> keeps its own surface.</para>
///
/// <para><b>Why a held lock rather than a heartbeat row.</b> The property that matters is that it
/// is <b>released when the connection closes</b>: an instance that is killed must not leave the
/// installation looking like somebody is still sweeping. A row with a timestamp needs a staleness
/// rule, and a staleness rule needs a number that is wrong in one direction or the other. It also
/// needs a migration, and <c>docs/migrations-defect.md</c> is a standing argument for not reaching
/// for one when something already works.</para>
///
/// <para>⚠️ The cost is one pinned connection per holder, for the lifetime of the hold. That is
/// the price of the self-release above, and it is why only genuinely installation-wide questions
/// should get a key here.</para>
/// </summary>
public static class AdvisoryLock
{
    /// <summary>
    /// The keys in use. Derived from "ENTKUBE" so they are recognisable in <c>pg_locks</c>, and
    /// fixed because every instance has to ask for the same number.
    ///
    /// <para>⚠️ One key per question. Two purposes sharing a key would make the first holder look
    /// like it was answering the second question too.</para>
    /// </summary>
    public const long MigrationsKey = 0x45_4E_54_4B_55_42_45_01;

    /// <summary>Held by the instance that runs the work which must happen on exactly one.</summary>
    public const long SingletonWorkKey = 0x45_4E_54_4B_55_42_45_02;

    public const string MigrationsResource = "EntKube:Migrations";
    public const string SingletonWorkResource = "EntKube:SingletonWork";

    /// <summary>
    /// A non-blocking attempt, or null when this provider takes no lock.
    ///
    /// <para>⚠️ Non-blocking on purpose. A caller that needs to wait polls with a bounded budget,
    /// so a wedged holder surfaces as something an orchestrator can restart rather than an
    /// instance hanging with no further log line. <c>pg_advisory_lock</c> and a
    /// <c>@LockTimeout</c> of -1 would both wait forever.</para>
    /// </summary>
    public static string? TryAcquireSql(string? providerName, long key, string resourceName)
        => providerName switch
        {
            "Npgsql.EntityFrameworkCore.PostgreSQL" => $"SELECT pg_try_advisory_lock({Sql(key)})",

            // Returns >= 0 when granted, -1 on timeout. SELECTed so ExecuteScalar can read it.
            // @LockOwner = 'Session' rather than 'Transaction' so the hold outlives the
            // transaction that took it — a transaction-owned lock is released by the first commit.
            "Microsoft.EntityFrameworkCore.SqlServer" =>
                "DECLARE @r int; EXEC @r = sp_getapplock "
                + $"@Resource = '{resourceName}', @LockMode = 'Exclusive', "
                + "@LockOwner = 'Session', @LockTimeout = 0; SELECT @r;",

            _ => null,
        };

    /// <summary>The matching release, or null when nothing was taken.</summary>
    public static string? ReleaseSql(string? providerName, long key, string resourceName)
        => providerName switch
        {
            "Npgsql.EntityFrameworkCore.PostgreSQL" => $"SELECT pg_advisory_unlock({Sql(key)})",
            "Microsoft.EntityFrameworkCore.SqlServer" =>
                $"EXEC sp_releaseapplock @Resource = '{resourceName}', @LockOwner = 'Session';",
            _ => null,
        };

    /// <summary>
    /// A key as SQL text. ⚠️ Always invariant: the current culture is the host's, and sv-SE writes a
    /// negative number with U+2212 (MINUS SIGN), which Postgres reads as a column name
    /// (<c>42703: column "−5306212924162610147" does not exist</c>), so no lock with a negative key
    /// could ever be taken there.
    /// </summary>
    private static string Sql(long key) => key.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether a provider that takes no lock is expected to, or is one nobody has taught this.
    ///
    /// <para>⚠️ The distinction is the point. SQLite silently taking no lock is correct — it is a
    /// single file used as the local-development fallback, and two processes sharing one SQLite
    /// file is not a deployment this supports. An UNRECOGNISED provider silently taking no lock is
    /// a gap, and the difference must reach a log rather than being folded into one branch.</para>
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
