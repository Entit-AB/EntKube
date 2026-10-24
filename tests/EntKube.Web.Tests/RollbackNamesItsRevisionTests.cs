using System.Text;
using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The automatic rollback used to run <c>kubectl rollout undo deployment.apps/x -n ns</c> with no
/// <c>--to-revision</c>.
///
/// <para>That is a <b>relative</b> move: "one step back from wherever you are at the instant this
/// runs". It is right only while nothing else has moved the workload, and nothing guarantees that.
/// A second undo from any source — an operator typing <c>kubectl rollout undo</c> while the
/// watcher is judging, a retry after a partial failure, anything outside this process — steps the
/// workload <em>forward</em> again, off the good revision and back on to the bad release, and
/// reports success. The judgement claim merged earlier stops two EntKube instances doing it; it
/// cannot stop a human or a retry.</para>
///
/// <para>⚠️ <b>Pinning the number is not, on its own, the fix, and these tests are arranged to say
/// why.</b> Kubernetes renumbers on undo: rolling back to revision 4 re-points the Deployment at
/// revision 4's ReplicaSet and renumbers <em>that</em> ReplicaSet to the new maximum, so the
/// number 4 ceases to exist while the bad release keeps its own number. A second attempt that
/// recomputed "the one below current" would compute the bad release and pin it just as
/// confidently. Pinning makes the command absolute; the <b>refusal</b> is what makes a second
/// undo harmless.</para>
///
/// <para>⭐ The tell that a workload has already been rolled back is that the ReplicaSet it is
/// running is not the most recently <em>created</em> one. Creation timestamps survive renumbering;
/// revision numbers do not.</para>
///
/// <para>⚠️ <b>What is NOT claimed here.</b> <c>DeploymentRollout</c> records no revision number,
/// so none of this is "the revision the watch started from" — it is "the revision below the one
/// running now", read at rollback time. See <c>RolloutService.RollbackAsync</c>.</para>
/// </summary>
public class RollbackNamesItsRevisionTests
{
    // ════════════════════════════════════════════════════════════════
    //  The command line
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// The positive half of every absence assertion below: the command really does carry a
    /// revision, so "no relative undo" is a statement about a command that exists.
    /// </summary>
    [Fact]
    public void The_undo_command_names_the_revision_it_wants()
    {
        string args = KubernetesClientFactory.BuildUndoArguments(
            "deployment.apps/api", "billing-ns", 4, "/tmp/kc");

        args.Should().Contain("rollout undo deployment.apps/api");
        args.Should().Contain("-n billing-ns");
        args.Should().Contain("--to-revision=4");
        args.Should().Contain("--kubeconfig=/tmp/kc");
    }

    /// <summary>
    /// ⚠️ <c>--to-revision=0</c> is kubectl's own spelling of "the previous revision" — the exact
    /// relative move this replaces. A zero reaching the command line would look pinned in the
    /// logs and behave relatively against the cluster, which is the worst of both.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_revision_that_is_not_a_real_revision_is_refused_rather_than_emitted(int bogus)
    {
        Action act = () => KubernetesClientFactory.BuildUndoArguments(
            "deployment.apps/api", "billing-ns", bogus, "/tmp/kc");

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// A pinned undo can fail where a relative one could not: the history limit may prune the
    /// ReplicaSet between the plan and the undo. The number has to be in the message, because
    /// "unable to find specified revision" is unactionable without it.
    /// </summary>
    [Fact]
    public void A_pruned_revision_is_reported_with_the_number_that_went_missing()
    {
        string line = KubernetesClientFactory.DescribeUndoFailure(
            "deployment.apps/api", 4, "  error: unable to find specified revision 4 in history\n");

        line.Should().Contain("deployment.apps/api");
        line.Should().Contain("revision 4");
        line.Should().Contain("unable to find specified revision 4 in history");
        line.Should().NotEndWith("\n");
    }

    // ════════════════════════════════════════════════════════════════
    //  The plan
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void A_forward_workload_is_pinned_to_the_revision_below_the_one_it_is_running()
    {
        List<KubernetesClientFactory.WorkloadUndoStep> plan = KubernetesClientFactory.PlanNamespaceUndo(
            Deployments(("api", "uid-api", 5)),
            ReplicaSets(
                ("api-aaa", "uid-api", 4, "2026-10-24T09:00:00Z", null),
                ("api-bbb", "uid-api", 5, "2026-10-24T10:00:00Z", null)));

        KubernetesClientFactory.WorkloadUndoStep step = plan.Should().ContainSingle().Subject;

        step.Workload.Should().Be("deployment.apps/api");
        step.ToRevision.Should().Be(4);
        step.AlreadyRolledBack.Should().BeFalse();
    }

    /// <summary>
    /// ⭐ <b>The central case.</b> Same namespace, one undo later.
    ///
    /// <para>The "after" state is what Kubernetes actually leaves behind: revision 4's ReplicaSet
    /// has been renumbered to 6 and is current, while the bad release's ReplicaSet keeps revision
    /// 5 and its newer creation timestamp. A recomputed relative — or a naively recomputed pin —
    /// would now target revision 5, which <em>is</em> the bad release.</para>
    ///
    /// <para>⚠️ Asserted against the "before" state in the same test on purpose. "No undo is
    /// planned" means nothing unless the same shape of input, one undo earlier, does plan one.</para>
    /// </summary>
    [Fact]
    public void A_workload_someone_has_already_undone_is_left_alone_rather_than_rolled_forward()
    {
        const string before = "2026-10-24T09:00:00Z";
        const string after = "2026-10-24T10:00:00Z";

        // Before anyone undid anything: revision 5 is both the highest number and the newest
        // ReplicaSet, so the rollback is planned and pinned.
        List<KubernetesClientFactory.WorkloadUndoStep> forward = KubernetesClientFactory.PlanNamespaceUndo(
            Deployments(("api", "uid-api", 5)),
            ReplicaSets(
                ("api-good", "uid-api", 4, before, null),
                ("api-bad", "uid-api", 5, after, null)));

        forward.Should().ContainSingle().Which.ToRevision.Should().Be(4);

        // After the undo: the good ReplicaSet was reused and renumbered to 6, so it is current
        // despite being the OLDER object. The bad release still sits at revision 5.
        List<KubernetesClientFactory.WorkloadUndoStep> undone = KubernetesClientFactory.PlanNamespaceUndo(
            Deployments(("api", "uid-api", 6)),
            ReplicaSets(
                ("api-good", "uid-api", 6, before, "4"),
                ("api-bad", "uid-api", 5, after, null)));

        KubernetesClientFactory.WorkloadUndoStep step = undone.Should().ContainSingle().Subject;

        step.ToRevision.Should().BeNull("undoing again would move it on to revision 5, the bad release");
        step.AlreadyRolledBack.Should().BeTrue("the workload is already where the rollback wanted it");
        step.Explanation.Should().Contain("Already rolled back");
    }

    /// <summary>
    /// ⚠️ Kubernetes serialises <c>creationTimestamp</c> to whole seconds, so two ReplicaSets born
    /// in the same second tie and the "is anything newer?" test alone would wave the second undo
    /// through. The <c>revision-history</c> annotation says the same thing from the other side.
    ///
    /// <para>Paired with its own negative: the identical timestamps WITHOUT the annotation are a
    /// genuine forward state and must still be rolled back, or this check would simply disable
    /// the feature for fast deploys.</para>
    /// </summary>
    [Fact]
    public void A_tie_in_creation_timestamps_is_still_caught_by_the_revision_history_annotation()
    {
        const string sameSecond = "2026-10-24T10:00:00Z";

        List<KubernetesClientFactory.WorkloadUndoStep> tiedAndUndone = KubernetesClientFactory.PlanNamespaceUndo(
            Deployments(("api", "uid-api", 6)),
            ReplicaSets(
                ("api-good", "uid-api", 6, sameSecond, "4"),
                ("api-bad", "uid-api", 5, sameSecond, null)));

        tiedAndUndone.Should().ContainSingle().Which.AlreadyRolledBack.Should().BeTrue();

        List<KubernetesClientFactory.WorkloadUndoStep> tiedAndForward = KubernetesClientFactory.PlanNamespaceUndo(
            Deployments(("api", "uid-api", 5)),
            ReplicaSets(
                ("api-good", "uid-api", 4, sameSecond, null),
                ("api-bad", "uid-api", 5, sameSecond, null)));

        KubernetesClientFactory.WorkloadUndoStep forward = tiedAndForward.Should().ContainSingle().Subject;
        forward.AlreadyRolledBack.Should().BeFalse();
        forward.ToRevision.Should().Be(4);
    }

    /// <summary>
    /// A first release, or one whose predecessor the history limit has pruned, has no revision to
    /// name. ⚠️ This refusal is a <b>failure</b>, unlike "already rolled back": the bad release is
    /// still live, so the verdict must be RollbackFailed and somebody must be told. A rollback
    /// that quietly reports success here would be a record of something that did not happen.
    /// </summary>
    [Fact]
    public void A_workload_with_no_earlier_revision_is_refused_and_counted_as_a_failure()
    {
        List<KubernetesClientFactory.WorkloadUndoStep> plan = KubernetesClientFactory.PlanNamespaceUndo(
            Deployments(("api", "uid-api", 1)),
            ReplicaSets(("api-aaa", "uid-api", 1, "2026-10-24T10:00:00Z", null)));

        KubernetesClientFactory.WorkloadUndoStep step = plan.Should().ContainSingle().Subject;

        step.ToRevision.Should().BeNull();
        step.AlreadyRolledBack.Should().BeFalse("the release being rolled back is still running");
        step.Explanation.Should().Contain("nothing to roll back to");
    }

    /// <summary>
    /// ⚠️ History is matched by owner uid, never by name. "api" and "api-worker" share a prefix,
    /// and a prefix match would pin each from the other's ReplicaSets — a rollback to a revision
    /// number that belongs to a different workload.
    /// </summary>
    [Fact]
    public void Each_workload_is_pinned_from_its_own_replicasets()
    {
        List<KubernetesClientFactory.WorkloadUndoStep> plan = KubernetesClientFactory.PlanNamespaceUndo(
            Deployments(("api", "uid-api", 5), ("api-worker", "uid-worker", 12)),
            ReplicaSets(
                ("api-aaa", "uid-api", 4, "2026-10-24T09:00:00Z", null),
                ("api-bbb", "uid-api", 5, "2026-10-24T10:00:00Z", null),
                ("api-worker-ccc", "uid-worker", 11, "2026-10-24T08:00:00Z", null),
                ("api-worker-ddd", "uid-worker", 12, "2026-10-24T10:30:00Z", null)));

        plan.Should().HaveCount(2);
        plan.Single(s => s.Workload == "deployment.apps/api").ToRevision.Should().Be(4);
        plan.Single(s => s.Workload == "deployment.apps/api-worker").ToRevision.Should().Be(11);
    }

    /// <summary>
    /// The target is the greatest revision below the current one, not the current one minus one:
    /// the history limit prunes from the bottom and leaves gaps, so arithmetic on the number would
    /// name a ReplicaSet that is not there and the undo would fail for no reason.
    /// </summary>
    [Fact]
    public void A_gap_in_the_history_does_not_become_a_revision_that_is_not_there()
    {
        List<KubernetesClientFactory.WorkloadUndoStep> plan = KubernetesClientFactory.PlanNamespaceUndo(
            Deployments(("api", "uid-api", 9)),
            ReplicaSets(
                ("api-aaa", "uid-api", 6, "2026-10-24T07:00:00Z", null),
                ("api-bbb", "uid-api", 9, "2026-10-24T10:00:00Z", null)));

        plan.Should().ContainSingle().Which.ToRevision.Should().Be(6);
    }

    /// <summary>
    /// A namespace whose ReplicaSets cannot be seen yields no pinned undo at all — and the
    /// refusals are failures, so the caller reports a failed rollback rather than a silent
    /// success. ⚠️ There is deliberately no degrade to the relative form here: the relative form
    /// is the defect, not the fallback.
    /// </summary>
    [Fact]
    public void A_workload_whose_history_is_invisible_is_never_undone_relatively()
    {
        List<KubernetesClientFactory.WorkloadUndoStep> plan = KubernetesClientFactory.PlanNamespaceUndo(
            Deployments(("api", "uid-api", 5)),
            """{"apiVersion":"v1","kind":"List","items":[]}""");

        KubernetesClientFactory.WorkloadUndoStep step = plan.Should().ContainSingle().Subject;

        step.ToRevision.Should().BeNull();
        step.AlreadyRolledBack.Should().BeFalse();
    }

    // ════════════════════════════════════════════════════════════════
    //  Fixtures
    // ════════════════════════════════════════════════════════════════

    private static string Deployments(params (string Name, string Uid, int Revision)[] items)
    {
        StringBuilder sb = new();
        sb.Append("{\"apiVersion\":\"v1\",\"kind\":\"List\",\"items\":[");

        for (int i = 0; i < items.Length; i++)
        {
            if (i > 0) sb.Append(',');

            sb.Append("{\"kind\":\"Deployment\",\"metadata\":{")
              .Append("\"name\":\"").Append(items[i].Name).Append("\",")
              .Append("\"uid\":\"").Append(items[i].Uid).Append("\",")
              .Append("\"annotations\":{\"deployment.kubernetes.io/revision\":\"")
              .Append(items[i].Revision).Append("\"}}}");
        }

        return sb.Append("]}").ToString();
    }

    private static string ReplicaSets(
        params (string Name, string OwnerUid, int Revision, string Created, string? History)[] items)
    {
        StringBuilder sb = new();
        sb.Append("{\"apiVersion\":\"v1\",\"kind\":\"List\",\"items\":[");

        for (int i = 0; i < items.Length; i++)
        {
            if (i > 0) sb.Append(',');

            sb.Append("{\"kind\":\"ReplicaSet\",\"metadata\":{")
              .Append("\"name\":\"").Append(items[i].Name).Append("\",")
              .Append("\"creationTimestamp\":\"").Append(items[i].Created).Append("\",")
              .Append("\"annotations\":{\"deployment.kubernetes.io/revision\":\"")
              .Append(items[i].Revision).Append('"');

            if (items[i].History is string served)
            {
                sb.Append(",\"deployment.kubernetes.io/revision-history\":\"").Append(served).Append('"');
            }

            sb.Append("},\"ownerReferences\":[{\"kind\":\"Deployment\",\"uid\":\"")
              .Append(items[i].OwnerUid).Append("\"}]}}");
        }

        return sb.Append("]}").ToString();
    }
}
