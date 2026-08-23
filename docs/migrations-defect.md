# Ten migrations EF Core cannot see

## What is wrong

Ten migration files under `src/EntKube.Web/Data/Migrations/{Sqlite,Postgres,SqlServer}/`
were hand-written without their `.Designer.cs` partial. That partial is where EF Core
puts the `[Migration("id")]` attribute it discovers migrations by, so **EF never runs
them**. `dotnet ef database update` reports success and silently skips all ten.

```
20260602200000_AddMongoClusterResources
20260603080000_AddCnpgClusterIsExternal
20260609210000_AddDeploymentRouteApplied
20260610120000_AddVaultSecretVersions
20260611000000_AddOpsTeamFeatures
20260611010000_AddAlertIncidentRunbookUrl
20260611020000_AddAlertIncidentEscalation
20260611030000_AddDeploymentGitUrl
20260611040000_AddNotificationProviderConfig
20260611060000_AddAlertRoutingSuppression
```

The same ten are missing their Designer partial in all three providers.

## How it was found and measured

A database built with `dotnet ef database update` was diffed, table by table and
column by column, against one built directly from the model with `EnsureCreated()`.
On SQLite the migration chain applied **117 of 121** files and left exactly:

| Missing | Consequence |
|---|---|
| `AppDeployments.GitUrl` | The app **cannot start** — `GitSyncService` throws `no such column: a.GitUrl` and the host shuts down |
| `NotificationProviderConfigs` table | Notification provider configuration fails when used |
| `AppDeploymentRoutes.ClusterAppliedAt` | Already repaired by an existing startup shim |

Most of the ten are harmless because later, properly-generated migrations happen to
create the same schema. Only the two above actually bite.

## What was done about it

Two idempotent startup repairs, following the pattern already in `Program.cs` for
exactly this class of problem (`EnsureAppEnvironmentNamespaceAsync` and friends):

- `EnsureDeploymentGitUrlAsync`
- `EnsureNotificationProviderConfigsAsync`

Verified: a database created purely by `dotnet ef database update` previously killed
the host at startup; it now starts, both objects are created, and a second start
produces no warnings.

## What was NOT done

**The migrations themselves are still invisible.** Regenerating their Designer
partials would make EF run them — and on any database that already carries the schema
(every production Postgres instance, presumably) the SQLite-style `AddColumn` calls
would then fail with a duplicate-column error and stop the app from starting. Several
of the Postgres versions already use `ADD COLUMN IF NOT EXISTS`, which suggests
somebody hit this before and worked around it per-file.

Fixing it properly needs a decision that depends on facts only you have:

1. Which databases are live, and on which providers.
2. Whether those databases already contain the schema from these ten migrations.

If every live database already has the schema, the clean fix is to regenerate the
Designer partials **and** rewrite the ten `Up()` bodies to be idempotent, so they are
no-ops where the schema exists. If some do not, they need a data-bearing repair first.

Either way the startup repairs above are safe to keep: they are idempotent and will
simply do nothing once the migrations are fixed.

## Also worth checking

Only the SQLite gap could be measured here, because that is the provider that can be
built from scratch locally. **Postgres and SQL Server may be missing different
things**, and the same diff should be run against each before trusting their schemas:
build one database with `dotnet ef database update` and another with `EnsureCreated()`,
then compare tables and columns.
