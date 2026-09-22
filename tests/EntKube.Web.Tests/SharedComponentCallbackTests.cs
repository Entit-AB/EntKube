using System.Reflection;
using Microsoft.AspNetCore.Components;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The callback parameters on the shared components have to be
/// <see cref="EventCallback"/>, not plain delegates.
///
/// <para><b>Why this is worth a test.</b> Blazor re-renders the component that
/// <em>supplied</em> an <see cref="EventCallback"/> once it has run. A
/// <c>Func&lt;Task&gt;</c> gets no such treatment: only the component that owns the click
/// re-renders, so a caller that reloads its data after saving keeps showing the old data
/// until the page is left and returned to.</para>
///
/// <para><c>AsyncButton</c> had exactly that, and the symptom was that saving an annex
/// appeared to do nothing. It is invisible to every other kind of test — the data was
/// saved, the reload ran, and only the screen disagreed — so the guard is a type check.</para>
/// </summary>
public class SharedComponentCallbackTests
{
    public static TheoryData<string, string> CallbackParameters => new()
    {
        { "EntKube.Web.Components.Pages.Shared.AsyncButton", "OnClick" },
        { "EntKube.Web.Components.Pages.Shared.ConfirmDialog", "OnConfirm" },
        { "EntKube.Web.Components.Pages.Shared.ConfirmDialog", "OnCancel" },
    };

    [Theory]
    [MemberData(nameof(CallbackParameters))]
    public void A_shared_components_callback_is_an_EventCallback(string typeName, string parameterName)
    {
        Type component = typeof(EntKube.Web.Data.ApplicationDbContext).Assembly.GetType(typeName)
            ?? throw new InvalidOperationException($"{typeName} was not found — has it moved?");

        PropertyInfo property = component.GetProperty(parameterName)
            ?? throw new InvalidOperationException($"{typeName} has no {parameterName}.");

        property.GetCustomAttribute<ParameterAttribute>()
            .Should().NotBeNull($"{parameterName} is meant to be a component parameter");

        property.PropertyType.Should().Be(
            typeof(EventCallback),
            "a plain delegate does not re-render the component that supplied it, so the "
            + "caller's screen goes stale after it reloads its data");
    }
}
