namespace EntKube.Web.Data;

/// <summary>
/// Whether one of our own people is taking support work at the moment.
///
/// <para><b>Why a row at all, rather than a flag on the user.</b> Support duty is per
/// tenant. The same person can be carrying a portfolio for one tenant and have nothing to
/// do with another, and a single flag on the account would put them in both rotas or
/// neither.</para>
///
/// <para><b>Why a row has to exist before anybody is in the rota.</b> Every tenant member
/// is not a technician: somebody only ever looks at the invoices, somebody else was given
/// access for one migration two years ago. Round-robin over "everybody with access" would
/// hand a P1 to whoever that is at three in the morning. So enrolment is a deliberate act,
/// and the absence of a row means "not in the rota" rather than "available".</para>
///
/// <para><b>And why <see cref="IsActive"/> is separate from the row existing.</b> Leaving
/// the rota for a fortnight is not the same as never having been in it, and the difference
/// is the <see cref="Note"/> — a colleague looking at the roster wants to read "parental
/// leave until March", not to find somebody silently missing.</para>
/// </summary>
public class SupportDuty
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>
    /// The account, which is also where the address we write to comes from. Deliberately
    /// an account and not a typed-in name: assigning a ticket now sends mail, and a name
    /// somebody typed has nowhere to send it.
    /// </summary>
    public required string UserId { get; set; }

    /// <summary>
    /// Whether they are taking work right now. False is a normal, expected state and not a
    /// problem to be fixed — see the class remarks.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Why they are not taking work, when they are not. Shown on the roster, so that an
    /// absence reads as an absence.
    /// </summary>
    public string? Note { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Who last changed it. Normally the person themselves; a lead can also stand somebody
    /// down, and which of the two happened is worth being able to see.
    /// </summary>
    public string? UpdatedBy { get; set; }

    public Tenant Tenant { get; set; } = null!;
}
