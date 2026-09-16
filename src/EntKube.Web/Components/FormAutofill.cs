namespace EntKube.Web.Components;

/// <summary>
/// Attributes that keep a browser's password manager out of a configuration form.
///
/// <para>These forms are a column of text boxes above a password box, which is exactly the shape a
/// saved login is filled into: the manager writes the stored username into the nearest preceding
/// text input and the password into the password input, and Blazor's change handler records both as
/// if they had been typed. Nothing downstream can tell the difference.</para>
///
/// <para>That is not hypothetical. A Harbor was installed with its Redis address set to the
/// operator's email address — which resolves — so the registry's core spent every restart dialling a
/// mail provider on port 6379, and the chart, told it had an external Redis, ran none of its own.
/// The address appeared nowhere but a pod log.</para>
///
/// <para>Why the whole set rather than <c>autocomplete</c> alone: Chrome ignores "off" on a form it
/// has decided is a login, and each third-party manager keys off its own attribute.
/// <see cref="Password"/> carries "new-password", the one value Chrome honours on a password box —
/// and honouring it also stops it looking for a username box to fill.</para>
/// </summary>
public static class FormAutofill
{
    /// <summary>For text, number and other non-password inputs.</summary>
    public static readonly Dictionary<string, object> Fields = new()
    {
        ["autocomplete"] = "off",
        ["data-lpignore"] = "true",
        ["data-1p-ignore"] = "true",
        ["data-bwignore"] = "true",
        ["data-form-type"] = "other"
    };

    /// <summary>For password inputs, where "off" is ignored but "new-password" is not.</summary>
    public static readonly Dictionary<string, object> Password = new(Fields)
    {
        ["autocomplete"] = "new-password"
    };
}
