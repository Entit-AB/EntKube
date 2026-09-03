# Database provider defects

Two separate problems, found by diffing schemas built from migrations against schemas
built from the EF model. **Postgres is fine. SQLite had two missing objects, now
repaired. SQL Server does not work at all** — see the last section.

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

## The other two providers — now measured

The same diff was run against real Postgres 16 and SQL Server 2022 containers: one
database built with `dotnet ef database update`, another with `EnsureCreated()`, then
compared column by column.

### Postgres: clean

126 tables, 1220 columns from migrations. **No missing tables, no missing columns.**

The ten invisible migrations do no harm here because later, properly-generated
migrations happened to create the same schema — `AddDeploymentGitUrl` never ran, but
`AddClusterServers` included `GitUrl` because the snapshot had already drifted.

The only difference is `AspNetUserPasskeys`, present from migrations and absent from
the model-built database. That is an artifact of the comparison, not a defect: the
Identity passkey table only appears when the store schema version is v3, which
`Program.cs` and `DesignTimeDbContextFactories` both set but a bare `EnsureCreated()`
harness does not.

### SQL Server: does not work at all

`dotnet ef database update` **fails on the sixth migration of 121**:

```
Introducing FOREIGN KEY constraint 'FK_VaultSecrets_ClusterComponents_ComponentId'
on table 'VaultSecrets' may cause cycles or multiple cascade paths.
```

This is not the invisible-migrations problem. `VaultSecrets` has three cascading
foreign keys — to `Apps`, `ClusterComponents` and `SecretVaults` — and SQL Server
rejects a schema where a delete can reach the same row by more than one cascade path.
Postgres and SQLite both permit it.

**The model itself is equally incompatible.** Building the schema directly from the
model with `EnsureCreated()`, bypassing migrations entirely, fails the same way on a
*different* relationship:

```
Introducing FOREIGN KEY constraint 'FK_CustomerGitCredentials_Tenants_TenantId'
on table 'CustomerGitCredentials' may cause cycles or multiple cascade paths.
```

So this cannot be repaired by fixing a migration. The EF model declares 156 explicit
`DeleteBehavior.Cascade` relationships (plus EF's own defaults for required
relationships), and several of them converge on the same tables.

`Program.cs` accepts `DatabaseProvider: "SqlServer"`, and a full 121-migration history
is maintained for it — so the provider is offered, and has apparently never worked.

### What to do about SQL Server

Not decided here, because each option changes something only you should choose:

1. **Make cascades provider-specific.** Apply a convention in
   `SqlServerApplicationDbContext` that emits `NO ACTION` where a cascade path would
   converge. Referential cleanup then has to happen in code for those relationships.
   Note that `DeleteTenantAsync` already performs ordered transactional deletes to
   handle `Restrict` FKs, so the application is not wholly dependent on cascades.
2. **Change the model's delete behaviour** for the converging relationships on every
   provider. Simpler to reason about, but it changes delete semantics for Postgres and
   SQLite users too.
3. **Stop offering SQL Server** until one of the above is done — remove the `case
   "SqlServer"`, and the migration folder with it. Honest, and prevents someone
   discovering this in the middle of a deployment.

Whichever is chosen, the current state is the worst of the three: the provider is
selectable, appears supported, and fails partway through creating the schema.
