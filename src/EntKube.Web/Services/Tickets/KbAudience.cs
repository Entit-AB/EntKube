using System.Linq.Expressions;
using EntKube.Web.Data;

namespace EntKube.Web.Services.Tickets;

/// <summary>
/// The ONE place that says which knowledge-base answers may reach a customer (helpdesk phase 11).
///
/// <para><b>The leak axis is customers, not models.</b> Nothing in the knowledge base reaches an
/// API or a model; what can reach a customer is the portal, and <c>??</c>, which inserts an answer
/// into a reply or a note a customer may read. An application's runbook (access notes, known
/// defects) must never take either road, so both predicates refuse any answer with an
/// <see cref="KbAnswer.AppId"/> whatever its visibility, and neither admits
/// <see cref="KbVisibility.Operations"/>. The CHECK constraint on KbAnswers is a third, separate
/// guard: it stops an application's answer being stored with a customer-facing visibility at all.</para>
///
/// <para>Every method that reads <c>KbAnswers</c> for a customer-facing purpose must go through
/// these (KbAnswerReadersRatchetTests holds the list).</para>
/// </summary>
public static class KbAudience
{
    /// <summary>What <c>??</c> may offer, and insert, on a ticket of <paramref name="customerId"/>.</summary>
    public static Expression<Func<KbAnswer, bool>> OfferableInReplies(Guid tenantId, Guid? customerId, DateTime now) =>
        a => a.TenantId == tenantId
             && a.AppId == null
             && (a.Visibility == KbVisibility.Internal
                 || (a.Visibility == KbVisibility.Public && (a.PublishAt == null || a.PublishAt <= now)))
             && (a.CustomerId == null || a.CustomerId == customerId);

    /// <summary>What <paramref name="customerId"/> sees in the portal: public, published, theirs or everybody's.</summary>
    public static Expression<Func<KbAnswer, bool>> VisibleInPortal(Guid tenantId, Guid customerId, DateTime now) =>
        a => a.TenantId == tenantId
             && a.AppId == null
             && a.Visibility == KbVisibility.Public
             && (a.PublishAt == null || a.PublishAt <= now)
             && (a.CustomerId == null || a.CustomerId == customerId);
}
