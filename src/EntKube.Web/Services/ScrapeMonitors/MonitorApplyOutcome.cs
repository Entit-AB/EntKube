namespace EntKube.Web.Services.ScrapeMonitors;

/// <summary>
/// What an apply did, told from the plan the operator looked at and the plan read back afterwards.
///
/// <para><b>A change counts as done when it was planned before and is no longer planned after.</b>
/// One still planned afterwards was not carried out, or did not take, and is counted as left
/// rather than as done. Counting the plan before alone would report a write that failed as made.</para>
///
/// <para>The service does not say what it wrote, so this is the difference between two reads. A
/// change the background sweep made in between the two is counted here as well; the counts are of
/// what is no longer outstanding, which is what the operator pressed the button for.</para>
/// </summary>
public sealed record MonitorApplyOutcome(int Created, int Updated, int Replaced, int Removed, int Left)
{
    public static MonitorApplyOutcome Between(ScrapeMonitorPlan before, ScrapeMonitorPlan after)
    {
        HashSet<MonitorIdentity> stillApplied = [.. after.Apply.Select(m => m.Identity)];
        HashSet<MonitorIdentity> stillPruned = [.. after.Prune];

        List<PlannedMonitor> applied = [.. before.Apply.Where(m => !stillApplied.Contains(m.Identity))];

        return new MonitorApplyOutcome(
            Created: applied.Count(m => m.Change == MonitorChange.Create),
            Updated: applied.Count(m => m.Change == MonitorChange.Update),
            Replaced: applied.Count(m => m.Change == MonitorChange.Replace),
            Removed: before.Prune.Count(m => !stillPruned.Contains(m)),
            Left: after.Apply.Count + after.Prune.Count);
    }

    /// <summary>Nothing done and nothing left.</summary>
    public bool IsEmpty => Created + Updated + Replaced + Removed + Left == 0;

    /// <summary>One sentence: what was done, then what is left, naming only what is not zero.</summary>
    public string Sentence
    {
        get
        {
            List<string> done = [];
            if (Created > 0) done.Add($"{Created} created");
            if (Updated > 0) done.Add($"{Updated} updated");
            if (Replaced > 0) done.Add($"{Replaced} replaced");
            if (Removed > 0) done.Add($"{Removed} removed");

            // Not "nothing was changed": a write can land and still not read back as wanted.
            string first = done.Count > 0 ? $"Applied: {string.Join(", ", done)}."
                : Left > 0 ? "None of the planned changes took effect."
                : "Nothing needed changing.";

            return Left == 0
                ? first
                : $"{first} {Left} change{(Left == 1 ? " is" : "s are")} still outstanding.";
        }
    }
}
