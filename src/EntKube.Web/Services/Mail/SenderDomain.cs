namespace EntKube.Web.Services.Mail;

/// <summary>
/// Reading the domain off a sender, and deciding which registered domain claims it.
///
/// <para>Pure, and separate from the database work, because the rule is where the mistakes
/// are: a suffix match that forgets the dot boundary places <c>notentit.example</c> with
/// <c>entit.example</c>, and two customers registering overlapping domains has to resolve the
/// same way every time or the same sender lands in different places on different days.</para>
/// </summary>
public static class SenderDomain
{
    /// <summary>
    /// Domains that belong to everybody, so registering one would claim every consumer
    /// address for a single customer.
    ///
    /// <para>Refused rather than warned about. The mistake is invisible once made — mail
    /// simply starts arriving under the wrong customer, already triaged, and the register
    /// that caused it is not the first place anyone looks. A customer whose staff really
    /// do write from personal addresses is served by adding those addresses to the §23
    /// contacts, which is exact and cannot capture a stranger.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> PublicProviders = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com", "outlook.com", "hotmail.com", "hotmail.se",
        "live.com", "live.se", "msn.com", "yahoo.com", "yahoo.se", "icloud.com",
        "me.com", "aol.com", "protonmail.com", "proton.me", "gmx.com", "gmx.net",
        "mail.com", "zoho.com", "yandex.com", "telia.com", "bredband.net",
        "comhem.se", "spray.se", "tele2.se",
    };

    /// <summary>
    /// The domain part of an address, lower-cased — or null when there is not one.
    /// </summary>
    public static string? Of(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        int at = address.LastIndexOf('@');

        if (at < 0 || at == address.Length - 1)
        {
            return null;
        }

        string domain = address[(at + 1)..].Trim().TrimEnd('.').ToLowerInvariant();

        return domain.Length == 0 ? null : domain;
    }

    /// <summary>
    /// Tidies a domain as typed into the register: an address, a URL or a leading dot or @
    /// all reduce to the domain itself. Null when nothing usable is left.
    /// </summary>
    public static string? Normalise(string? entered)
    {
        if (string.IsNullOrWhiteSpace(entered))
        {
            return null;
        }

        string value = entered.Trim().ToLowerInvariant();

        // Somebody pasting a whole address, or a URL, meant the domain in it.
        if (value.Contains('@', StringComparison.Ordinal))
        {
            return Of(value);
        }

        value = value.TrimStart('@', '*', '.').TrimEnd('.', '/');

        if (value.StartsWith("http://", StringComparison.Ordinal))
        {
            value = value["http://".Length..];
        }
        else if (value.StartsWith("https://", StringComparison.Ordinal))
        {
            value = value["https://".Length..];
        }

        value = value.Split('/')[0].Trim();

        // A domain has to have a dot in it, or it is a hostname on somebody's LAN and would
        // match far more than whoever typed it intended.
        return value.Length == 0 || !value.Contains('.', StringComparison.Ordinal) ? null : value;
    }

    /// <summary>
    /// Whether a registered domain claims a sender's domain: the same domain, or a
    /// subdomain of it.
    ///
    /// <para>The dot matters. Without it, <c>entit.example</c> would claim <c>notentit.example</c> —
    /// a domain anybody can register, which would then be triaged straight into a
    /// customer's queue.</para>
    /// </summary>
    public static bool Claims(string registered, string senderDomain) =>
        senderDomain.Equals(registered, StringComparison.OrdinalIgnoreCase)
        || senderDomain.EndsWith($".{registered}", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Which of several registered domains claims a sender. The longest wins, so a customer
    /// who registers <c>it.entit.example</c> keeps their subdomain's mail even where another
    /// holds <c>entit.example</c> — and, more importantly, the answer does not depend on the
    /// order rows came back in.
    /// </summary>
    public static T? BestMatch<T>(
        IEnumerable<T> registered, Func<T, string> domainOf, string? senderDomain)
    {
        if (string.IsNullOrWhiteSpace(senderDomain))
        {
            return default;
        }

        return registered
            .Where(r => Claims(domainOf(r), senderDomain))
            .OrderByDescending(r => domainOf(r).Length)
            .ThenBy(r => domainOf(r), StringComparer.Ordinal)
            .FirstOrDefault();
    }
}
