# Single sign-on (OIDC)

EntKube can authenticate through any OpenID Connect provider — Entra ID, Keycloak,
Okta, Google Workspace — and derive tenant access from the groups the provider
asserts.

SSO is **opt-in and config-driven**. With no `Oidc` section, no OIDC scheme is
registered at all: the login page is unchanged and there is no half-configured
provider to misbehave.

## Configuration

```json
{
  "Oidc": {
    "Enabled": true,
    "Authority": "https://keycloak.example.com/realms/entkube",
    "ClientId": "entkube",
    "ClientSecret": "…",
    "DisplayName": "Sign in with SSO",
    "Scopes": ["groups"],
    "GroupsClaim": "groups",
    "AllowUsersWithoutMappedGroups": false
  }
}
```

Register `https://<your-entkube>/signin-oidc` as the redirect URI with your provider.

| Setting | Notes |
|---|---|
| `Authority` | Issuer URL. Discovery is done from here. |
| `GroupsClaim` | Providers disagree — Keycloak `groups`, Entra `groups` (object ids) or `roles`, Okta configurable. |
| `Scopes` | Extra scopes beyond openid/profile/email. A groups claim usually needs one. |
| `RequireHttpsMetadata` | Leave true. Setting it false lets the discovery document be fetched over plain HTTP, which is enough to hand an attacker the login flow. |
| `AllowUsersWithoutMappedGroups` | Default false. A directory-wide SSO app should not let your whole directory create accounts. |

The flow is authorization code with PKCE. Implicit and hybrid are deprecated and leak
tokens through the browser address bar.

Provider settings are configuration rather than database rows on purpose: a
misconfigured provider must not be editable by whoever is already logged in.

## Group mappings

Under **Admin → Single sign-on**, map a provider group to a tenant and a role.

Enter the group value **exactly as the provider emits it** — for Entra that is
usually a group object id, not a display name. Matching is exact; normalising it
would silently match groups you did not intend to grant.

### What reconciliation does

Access is **recomputed on every SSO login**, not just the first. That is what makes
offboarding work: dropping someone from a group in your directory removes their
EntKube access at their next login, with nobody having to remember to do it here too.

Within a tenant that has at least one group mapping, SSO is authoritative —
memberships are added, changed and removed to match the token.

**Memberships in tenants no mapping mentions are never touched.** Deleting an
operator's hand-granted access because an unrelated SSO login did not mention it
would be a spectacular way to lock people out of their own platform, so the two kinds
of grant never interfere.

A user whose groups map to nothing is signed out again with an explanatory message,
rather than being dropped into an empty portal that looks broken — unless
`AllowUsersWithoutMappedGroups` is set.

If the sync itself fails, the login proceeds on whatever access the user already had
and the failure is logged as an error. Locking everyone out of the platform because a
reconciliation query failed would be the worse outcome — but stale access after a
group change is a real security concern, so the log is loud.

## SCIM provisioning

`/scim/v2` accepts SCIM 2.0 user provisioning from a directory connector. It matters
alongside just-in-time provisioning because JIT only reconciles someone when they sign
in — so revoking a person who has just been dismissed takes effect *whenever they next
log in*, which may be never. SCIM lets the directory push the change.

Authenticate the connector with an API token carrying **`scim:provision`** and nothing
else. That scope stands alone deliberately: the token lives inside a directory
connector, it needs to disable accounts, and it has no business reading clusters or
triggering deployments.

| Method | Path | Notes |
|---|---|---|
| GET | `/scim/v2/ServiceProviderConfig` | Capability discovery |
| GET | `/scim/v2/Users` | `?filter=userName eq "..."`, `startIndex`, `count` |
| GET/PUT/PATCH/DELETE | `/scim/v2/Users/{id}` | |
| POST | `/scim/v2/Users` | 409 if the userName exists |

### Two behaviours worth knowing

**Deactivation, not deletion.** `DELETE` and `active: false` both *disable* the
account rather than removing the row. A deleted user takes their tenant memberships,
audit trail and identity with them, so re-provisioning later would silently create a
different person wearing the same name — and any record of what the original did would
be gone. Disabling blocks every sign-in path including SSO, and also stamps the
security stamp so an existing session is invalidated rather than running until its
cookie expires.

**An unsupported filter is a 400, never ignored.** Only a single
`attribute eq "value"` is supported. Silently dropping a filter would turn "find this
one user" into "here is every user", and a connector that asked whether someone exists
then concludes something false about who is already there — which, depending on the
connector, means a duplicate account or an update written onto the wrong person.

### Not implemented

`/Groups`. Tenant access is already derived from OIDC group claims at login, and a
second, disagreeing source of group membership is how people end up with access nobody
can explain. Provision users over SCIM; grant access with group mappings.
