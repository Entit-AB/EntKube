namespace EntKube.Web.Services.ClusterChanges;

/// <summary>
/// What a command executed inside a pod is going to do — stated by the caller, because nothing
/// else can know.
///
/// <para><b>Why this exists.</b> Every Kubernetes mutation that goes through
/// <c>IKubernetesClientFactory</c> raises an acknowledgment, because the factory raises it itself.
/// Nine verbs on that same factory did not, and they carry the most consequential writes in the
/// product: <c>CREATE ROLE</c>, <c>CREATE DATABASE</c>, <c>DROP DATABASE</c>,
/// <c>ALTER ROLE … PASSWORD</c>, <c>REASSIGN OWNED</c>, Mongo <c>createUser</c>/<c>dropUser</c> and
/// <c>rabbitmqctl import_definitions</c>. 30 call sites across 8 services, none gated, none
/// recorded — and there is no outcome to record when nothing asked. See
/// docs/decomposition.md §5.4.2.</para>
///
/// <para><b>Why the caller declares instead of the factory deciding.</b> Reads and writes share
/// the verbs. <c>ExecuteSqlOnPodAsync</c> carries <c>CREATE ROLE … CREATE DATABASE</c> at one call
/// site and <c>SELECT 1;</c> at another, so gating the verb would make reads prompt — the defect
/// #307 removed from the Postgres detail panel, reintroduced across eight services.</para>
///
/// <para><b>And why not a classifier.</b> A regex over the SQL left 9 of the 30 undecidable and
/// mis-read at least one: <c>ExecuteMongoWithOutputAsync</c> — the <em>output-returning</em>
/// variant, which looks like a read from its signature alone — performs a password rotation. A
/// classifier that answers "read" for a write produces an unasked, unrecorded change <b>while
/// appearing to have checked</b>, which is strictly worse than honest silence.</para>
/// </summary>
public enum ChangeIntent
{
    /// <summary>
    /// The command observes and changes nothing.
    ///
    /// <para>⚠️ <b>A claim, not a hint.</b> This is the value to be suspicious of, and it is
    /// deliberately auditable: <c>ExecChangeIntentTests</c> lists every site that claims it, which
    /// is a short list a human can actually check, and the claim is visible in the diff that
    /// introduces it. A wrong <c>Read</c> is the one way this design fails.</para>
    ///
    /// <para>Whether a <c>Read</c> claim should be <em>trusted</em> or merely quieter is an open
    /// question — <c>TestConnectionCoreAsync</c> runs <c>SELECT 1;</c> to check credentials, which
    /// is a read by any reading and also the cheapest possible place to hide something. Nothing
    /// here settles that; it makes the claims enumerable so the question can be asked of a list.</para>
    /// </summary>
    Read,

    /// <summary>
    /// The command changes state in the cluster or in a datastore it reaches.
    ///
    /// <para>Includes anything whose effect outlives the call: DDL, DML, user and role management,
    /// credential rotation, and replacing a broker's topology. If a statement's effect is not
    /// obvious, it is this one — the asymmetry is deliberate, because an extra acknowledgment
    /// costs an operator a click and a missed one costs an unrecorded change.</para>
    /// </summary>
    Change,
}
