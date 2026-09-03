namespace EntKube.Installer;

/// <summary>
/// An existing compose network, carried over rather than redeclared.
///
/// Compose stamps every network it creates with a <c>com.docker.compose.network</c> label holding
/// the KEY it had in the file — not its name. So a deployment whose file said
///
///   networks:
///     entkube:
///       name: entkube
///
/// owns a network labelled "entkube", and a file that says
///
///   networks:
///     default:
///       name: entkube
///
/// is refused against it with "network entkube was found but has incorrect label
/// com.docker.compose.network set to \"entkube\" (expected: \"default\")" — even though both name the
/// same network. The two are not interchangeable, and the installer cannot impose its own key on a
/// network somebody else created.
/// </summary>
public sealed record AdoptedNetwork(string Key, string Name, bool External)
{
    /// <summary>
    /// True when services must attach to it explicitly. Compose joins services to <c>default</c>
    /// automatically; any other key has to be named on each service, or they silently end up on a
    /// second network and stop resolving each other by hostname.
    /// </summary>
    public bool NeedsExplicitAttachment => Key != "default";

    public string Describe => External
        ? $"external network \"{Name}\" (key \"{Key}\")"
        : $"network \"{Name}\" (declared as \"{Key}\")";
}
