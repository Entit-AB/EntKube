namespace EntKube.Web.Data;

/// <summary>
/// The four support windows of §9 — the hours during which the supplier is staffed and
/// during which the SLA clocks of §14.4 run. Chosen per application in Annex A; the most
/// extensive one in the portfolio sets the window fee (§10.1).
/// </summary>
public enum SupportWindow
{
    /// <summary>Office hours — working days 08:00–17:00. No on-call.</summary>
    S1 = 1,

    /// <summary>Extended office hours — working days 05:00–22:00.</summary>
    S2 = 2,

    /// <summary>Extended office hours incl. weekends and public holidays — all days 05:00–22:00.</summary>
    S3 = 3,

    /// <summary>Dygnet runt — 24/7/365.</summary>
    S4 = 4,
}

/// <summary>
/// The §13 time categories. Which one applies is decided by <em>when the work was done</em>,
/// not by which support window the application bought — §13 is explicit about that, and it
/// is the mistake most likely to be made when reading the two tables side by side.
/// </summary>
public enum SupportTimeCategory
{
    /// <summary>Ordinary — working days 08:00–17:00.</summary>
    Ordinary = 0,

    /// <summary>Evening/morning — working days 05:00–08:00 and 17:00–22:00.</summary>
    EveningMorning = 1,

    /// <summary>Weekend and public holiday — 05:00–22:00.</summary>
    WeekendOrRedDay = 2,

    /// <summary>Night — 22:00–05:00, every day of the week.</summary>
    Night = 3,

    /// <summary>
    /// Call-out — work on a P1 incident outside the application's chosen support window.
    /// Not derivable from the clock alone: it depends on which window was bought and on the
    /// ticket's priority, so callers state it rather than the calendar inferring it.
    /// </summary>
    Callout = 4,
}

/// <summary>
/// Who wrote the application, which decides whether Del 2 or Del 3 of the agreement governs
/// it — and with that, whether there is an on-boarding fee and a guarantee period.
/// </summary>
public enum ContractOrigin
{
    /// <summary>Del 2 — developed by the customer or by another supplier.</summary>
    ExternallyDeveloped = 0,

    /// <summary>Del 3 — developed and delivered by the supplier.</summary>
    SupplierDeveloped = 1,
}

/// <summary>
/// The service level of §10.2, which sets the application's knowledge fee.
/// </summary>
public enum ManagementLevel
{
    /// <summary>Enkel — static or simple dynamic web application, few or no integrations.</summary>
    Simple = 0,

    /// <summary>Standard — business logic, 1–3 integrations, a database, user management.</summary>
    Standard = 1,

    /// <summary>Komplex — many integrations, high data volume, real-time or high security demands.</summary>
    Complex = 2,

    /// <summary>
    /// Instans — a further deployment of a parent application already under management
    /// (§10.2.1). Same code and image, its own monitoring, alarms, certificates and config.
    /// </summary>
    Instance = 3,

    /// <summary>Needs investigation — cannot be classified without a technical review.</summary>
    NeedsInvestigation = 4,
}

/// <summary>
/// How work is paid for, chosen per portfolio in Annex B. It never replaces the
/// base fee, which is charged under either model (§10).
/// </summary>
public enum PricingModel
{
    /// <summary>Model A — a hour bank of hours bought monthly at a discount (§11).</summary>
    HourBank = 0,

    /// <summary>Model B — time and materials, every hour billed in arrears (§12).</summary>
    TimeAndMaterials = 1,
}

/// <summary>Which side of the agreement a contact belongs to.</summary>
public enum ContractParty
{
    Customer = 0,
    Supplier = 1,
}

/// <summary>
/// The roles the agreement names. §23 requires a customer contact with a mandate plus a
/// deputy; §14.5 requires named people at escalation levels 2 and 3 on both sides, recorded
/// in Annex B and kept current by each party.
/// </summary>
public enum ContractContactRole
{
    /// <summary>The technical contact with a mandate to prioritise and approve (§23).</summary>
    TechnicalContact = 0,

    /// <summary>The deputy §23 also requires.</summary>
    Deputy = 1,

    /// <summary>Applikationsansvarig — escalation level 2 on the customer side (§14.5).</summary>
    ApplicationOwner = 2,

    /// <summary>Escalation level 2: the supplier's owner of the management agreement (§14.5).</summary>
    EscalationLevel2 = 3,

    /// <summary>Escalation level 3: the supplier's VD, the customer's IT manager (§14.5).</summary>
    EscalationLevel3 = 4,

    /// <summary>Where invoices and statements go (§20).</summary>
    Billing = 5,
}

/// <summary>
/// The kinds of amount Annex C carries. The price annex is replaced wholesale by a new
/// signed version rather than edited in place, so the entries are data rather than columns.
/// </summary>
public enum PriceKind
{
    /// <summary>Window fee per support window, C.1. Key: the <see cref="SupportWindow"/>.</summary>
    WindowFee = 0,

    /// <summary>Knowledge fee per service level, C.2. Key: the <see cref="ManagementLevel"/>.</summary>
    KnowledgeFee = 1,

    /// <summary>Hour bank tier, C.3. Key: the number of hours; the row also carries them numerically.</summary>
    HourBankTier = 2,

    /// <summary>Hourly rate per time category, C.4. Key: the <see cref="SupportTimeCategory"/>.</summary>
    HourlyRate = 3,

    /// <summary>One-off fees, C.5 — on-boarding per level, deploying a new instance.</summary>
    OneOffFee = 4,
}
