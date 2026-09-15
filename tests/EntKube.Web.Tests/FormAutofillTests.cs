using EntKube.Web.Components;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The attribute set that keeps browser password managers out of the configuration forms.
///
/// <para>Worth a test because the failure is silent and expensive. A manager that fills a saved
/// login into a component form writes the stored username into the nearest text box — which, for
/// Harbor, was the Redis address, so the registry's core dialled the operator's mail provider on
/// port 6379 and crash-looped. Nothing in the form, the values or the install says where the value
/// came from.</para>
/// </summary>
public class FormAutofillTests
{
    /// <summary>
    /// The one that catches a well-meaning simplification: Chrome ignores <c>autocomplete="off"</c>
    /// on a form it has decided is a login, and honours "new-password" — which is also what stops it
    /// hunting for a username box to fill beside it.
    /// </summary>
    [Fact]
    public void A_password_box_says_new_password_because_off_is_ignored()
    {
        FormAutofill.Password["autocomplete"].Should().Be("new-password");
        FormAutofill.Fields["autocomplete"].Should().Be("off");
    }

    /// <summary>
    /// Each third-party manager reads its own attribute and nothing else, so dropping one of these
    /// re-opens the hole for everyone using that manager — invisibly, since the others still behave.
    /// </summary>
    [Theory]
    [InlineData("data-lpignore")]    // LastPass
    [InlineData("data-1p-ignore")]   // 1Password
    [InlineData("data-bwignore")]    // Bitwarden
    [InlineData("data-form-type")]   // Dashlane
    public void Every_manager_gets_the_opt_out_it_reads(string attribute)
    {
        FormAutofill.Fields.Should().ContainKey(attribute);
        FormAutofill.Password.Should().ContainKey(attribute);
    }
}
