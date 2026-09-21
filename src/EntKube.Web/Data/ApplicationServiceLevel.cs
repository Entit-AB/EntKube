namespace EntKube.Web.Data;

/// <summary>
/// One entry in an application's classification history: the förvaltningsnivå and support
/// window that applied from a given date.
///
/// <para>A dated series rather than two columns on <see cref="ApplicationContract"/>,
/// because the classification changes and money follows it. §4.1 lets the supplier demand
/// reclassification when the technical state turns out to differ from what on-boarding
/// found, §16.2 reviews level and window quarterly, §2 lets the customer change window at a
/// quarter boundary, and §10.1.1 binds S3 and S4 for twelve months. An invoice for March
/// has to be explicable in March's terms after someone has changed the level in June.</para>
/// </summary>
public class ApplicationServiceLevel
{
    public Guid Id { get; set; }

    public Guid ApplicationContractId { get; set; }

    /// <summary>The förvaltningsnivå that applied from <see cref="EffectiveFrom"/>.</summary>
    public ManagementLevel Level { get; set; }

    /// <summary>
    /// The support window that applied. Null means inherited from the moderapplikation,
    /// which §10.2.1 makes the default for an instance unless something else is stated.
    /// </summary>
    public SupportWindow? SupportWindow { get; set; }

    /// <summary>
    /// The date this classification took effect — not the date it was typed in. A change
    /// agreed at a quarterly review is usually backdated or forward-dated to the quarter
    /// boundary §2 requires.
    /// </summary>
    public DateTime EffectiveFrom { get; set; }

    /// <summary>
    /// Why it changed. §14.3's habit of requiring written reasons for a priority change is
    /// worth borrowing here: a reclassification moves the monthly fee, and "because someone
    /// changed it" is not an answer at the next review.
    /// </summary>
    public string Reason { get; set; } = "";

    public string? RecordedBy { get; set; }

    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public ApplicationContract Contract { get; set; } = null!;
}
