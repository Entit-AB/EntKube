namespace EntKube.Web.Services.Scim;

/// <summary>A parsed SCIM filter, or the reason it could not be parsed.</summary>
public sealed record ScimFilterResult
{
    public required bool IsSupported { get; init; }

    /// <summary>Attribute being compared, lowercased, e.g. "username".</summary>
    public string? Attribute { get; init; }

    /// <summary>The value it is compared against, with quotes removed.</summary>
    public string? Value { get; init; }

    /// <summary>Why the filter could not be honoured, for a 400 response.</summary>
    public string? Error { get; init; }

    /// <summary>True when no filter was supplied at all — list everything.</summary>
    public bool IsEmpty { get; init; }

    public static ScimFilterResult Empty() => new() { IsSupported = true, IsEmpty = true };

    public static ScimFilterResult Match(string attribute, string value) =>
        new() { IsSupported = true, Attribute = attribute, Value = value };

    public static ScimFilterResult Unsupported(string error) =>
        new() { IsSupported = false, Error = error };
}

/// <summary>
/// Parses the subset of the SCIM filter grammar that identity providers actually send.
///
/// The full grammar has and/or/not, grouping, and ten operators. Entra, Okta and
/// OneLogin overwhelmingly send exactly one shape when provisioning:
/// <c>userName eq "someone@example.com"</c>. Supporting that properly is worth more
/// than supporting all of it badly.
///
/// The rule that matters: <b>an unsupported filter is an error, never ignored.</b>
/// Silently dropping a filter turns "find this one user" into "here is every user" —
/// and a provisioning client that asked whether a user exists, and got a list back,
/// concludes something false about who is already there. Depending on the client that
/// means a duplicate account or an update written onto the wrong person.
/// </summary>
public static class ScimFilter
{
    /// <summary>Attributes a filter may be applied to. Anything else is refused rather than guessed at.</summary>
    private static readonly HashSet<string> Filterable =
        new(StringComparer.OrdinalIgnoreCase) { "username", "externalid", "id", "active" };

    public static ScimFilterResult Parse(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return ScimFilterResult.Empty();
        }

        string text = filter.Trim();

        // Reject compound filters explicitly. Treating "a eq x and b eq y" as just
        // "a eq x" would silently widen the result set.
        foreach (string keyword in new[] { " and ", " or ", "not(", "(" })
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return ScimFilterResult.Unsupported(
                    "Only a single 'attribute eq \"value\"' filter is supported; "
                    + "compound filters are not.");
            }
        }

        // attribute SP "eq" SP value
        string[] parts = text.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            return ScimFilterResult.Unsupported($"Could not parse the filter '{filter}'.");
        }

        if (!string.Equals(parts[1], "eq", StringComparison.OrdinalIgnoreCase))
        {
            // co, sw, pr, gt… all exist in the spec. Honouring them as equality would
            // return the wrong users, so refuse instead.
            return ScimFilterResult.Unsupported(
                $"Only the 'eq' operator is supported, not '{parts[1]}'.");
        }

        string attribute = parts[0];
        if (!Filterable.Contains(attribute))
        {
            return ScimFilterResult.Unsupported(
                $"Filtering on '{attribute}' is not supported. "
                + $"Supported attributes: {string.Join(", ", Filterable.Order())}.");
        }

        string value = parts[2].Trim();

        // Values are normally quoted; booleans (active eq true) are not.
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1];
        }

        return value.Length == 0
            ? ScimFilterResult.Unsupported("The filter value is empty.")
            : ScimFilterResult.Match(attribute.ToLowerInvariant(), value);
    }
}
