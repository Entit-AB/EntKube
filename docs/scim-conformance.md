# SCIM 2.0 conformance

EntKube's SCIM surface audited against RFC 7643 (schema) and RFC 7644 (protocol), dimension by
dimension, with every claimed defect independently refuted before it was believed. **28 claims, 3
refuted, 25 standing.** This file records all 25 and says which are fixed, which are deliberate,
and which are deferred — so the deferred ones are a decision rather than an oversight.

Routes: `/scim/v2/ServiceProviderConfig` and six on `/scim/v2/Users`. Groups are deliberately not
implemented — tenant access comes from OIDC group claims at login, and a second disagreeing source
of group membership is how people end up with access nobody can explain.

## Why this was worth auditing at all

PATCH had **no test of any kind**, and it is the operation a directory deprovisions with: Entra
sends `replace active=false` rather than a DELETE. Every defect below in the PATCH path therefore
had the same shape — *the request succeeds and nothing happens* — which is the worst available
outcome, because the directory records a successful deprovision and never retries.

## Fixed

| What was wrong | Why it mattered |
|---|---|
| **A valueless `replace active` defaulted to ENABLED.** `ReadBool(value, true)` | A deprovision arriving without a value **reactivated** the account. Guessing the other way is equally wrong — it disables someone nobody asked to disable — so the operation is now refused with `invalidValue`. RFC 7644 §3.5.2 requires a value for `replace` anyway. |
| **A schema-qualified path was not recognised.** `urn:ietf:params:scim:schemas:core:2.0:User:active` | That form of deprovision matched nothing and answered **200 OK**. Only the *core* user prefix is stripped: doing the same to the enterprise extension URN would conflate two different attributes. |
| **`Operations` was matched case-sensitively** | SCIM attribute names are case-insensitive (RFC 7643 §2.1), and this file already handled that for paths and field keys. A connector sending `operations` was told its PatchOp had no Operations array and could never deprovision anyone. |
| **`PatchAsync` discarded the `IdentityResult`** | A rename Identity rejected — duplicate userName, a validator — answered 200 OK with the *old* name in the body. The directory recorded an update that never happened. Now 409 `uniqueness` for a duplicate, 400 `invalidValue` otherwise. |
| **A non-string `op`/`path`/`value` threw out of the handler** | `GetValue<string>()` throws on a JSON number, and the exception escaped — producing the application's **HTML error page** instead of a SCIM error envelope, which is the one thing `Respond` exists to prevent. |
| **`scimType` was never emitted, and `ScimResult` could not carry one** | RFC 7644 §3.12's error keyword is OPTIONAL, and worth emitting: a directory administrator never sees our HTTP status, only what their connector could make of the body. A 409 now says `uniqueness`; a refused filter says `invalidFilter`. |

The PATCH interpretation moved out of `PatchAsync` into `ScimUserService.Plan`, a pure function, so
these decisions are testable without standing up a `UserManager`. That is why they had no tests.

## Deliberate — do not "fix" these

- **An attribute this service does not store is IGNORED, not refused.** Entra sends PATCH for
  `name.givenName` and `displayName` on ordinary profile edits. RFC 7644 would permit a 400
  `invalidPath`, but answering that makes Entra mark the user as failed and retry forever.
  Tolerance is correct here; what is never tolerated is guessing a value.
- **`remove` is skipped.** It has no meaning on a single-valued user attribute.
- **DELETE deactivates rather than destroys**, so a re-provision cannot silently create a
  different person wearing the same name.
- **Only a single `eq` filter is supported**, and other forms are refused rather than ignored.

## Refuted — claimed defects that are not

- **ServiceProviderConfig omits `meta`.** Not required; RFC 7643 §5's example is non-normative.
  Publishing `meta.version` would also contradict `etag.supported = false`.
- **`meta.location` is relative.** RFC 7643 §3.1 requires it to *equal* `Content-Location` **if
  that header is sent**; nothing requires the header. With no `Content-Location` there is nothing
  for it to disagree with, and §3.1 says only "the URI of the resource being returned".
- **The PatchOp `schemas` URN is never read.** The RFC does not oblige a server to reject a body
  that omits it, so accepting one is leniency. The unused constant is dead code, not a behaviour.

## Deferred, with the reason

### Next: `externalId` is thrown away (the one breaks-provisioning finding)

```csharp
"externalid" => query.Where(u => u.UserName == parsed.Value),
```

`externalId` is the directory's own immutable identifier — an Entra object GUID, not an email — so
answering the filter against `userName` returns a false **"no such user"**. It is also discarded on
create and never echoed back, so it could never match anything. A connector that anchors on
`externalId` therefore cannot tell that a user it already provisioned exists, and creates a
duplicate.

Not in this change because it needs a column on `ApplicationUser` and migrations in three
providers, and `docs/migrations-defect.md` records that this is delicate ground: ten migrations are
invisible to EF for want of a `.Designer.cs` partial, and **SQL Server migrations fail on the sixth
of 121**. A schema change deserves its own change with its own care, not a bundle with behaviour
fixes.

⚠️ It also needs a migration plan of its own: users provisioned *before* the column exists have no
`externalId`, so a filter against the new column alone would return nothing for them and duplicate
the entire existing directory on first sync.

### Pagination edge cases

- `count=0` returns one resource. RFC 7644 §3.4.2.4: 0 means return no resources except
  `totalResults`. `Math.Clamp(count, 1, 200)` has a floor of 1.
- A negative `count` returns exactly one rather than the default page; the RFC says a negative
  value is "unspecified".
- `startIndex` is echoed unnormalised, so `?startIndex=0` reports `"startIndex": 0` where the RFC
  says a value below 1 shall be interpreted as 1. The *skip* is clamped; the echo is not — one
  clamp at the boundary closes both.
- `startIndex=int.MinValue` wraps through unchecked `startIndex - 1` to `int.MaxValue` and returns
  an empty page instead of the first one.

Reachable by a hand-rolled or fuzzing client; Entra and Okta send `>= 1` or omit it.

### Error-envelope coverage outside the handler

- **401 and 403 on all seven routes return RFC 7807 ProblemDetails**, not the SCIM envelope,
  because `RequireApiScope` is shared with the rest of the public API.
- **Any empty-bodied status on a `/scim/v2` route returns an HTML Razor page**, because
  `UseStatusCodePagesWithReExecute("/not-found")` is installed globally and ahead of endpoint
  execution. A non-integer `startIndex` fails model binding before the handler runs and lands here.

Both need a SCIM-specific branch in middleware that is shared with the public API, which is a
wider change than this one.

### Smaller

- `bulk` omits `maxPayloadSize`, a required sub-attribute. Harmless while `bulk.supported` is
  false.
- A 201 create carries no `Location` header, and `ScimResult` has no way to carry one.
- `meta` omits `created`/`lastModified`, and no column exists to source them from.
- No `/Schemas` or `/ResourceTypes` discovery endpoints. RFC 7644 §4 introduces all three as
  endpoints that MAY be retrieved, so the SHALL that governs their response format does not compel
  the routes to exist.
