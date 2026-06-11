---
applyTo: '**'
---
# EntKube - Copilot Instructions

## Overview

EntKube is a multi-tenant platform for managing shared Kubernetes applications and infrastructure services. Built on .NET 10 with Blazor (Server + WebAssembly), it provides a unified developer portal for provisioning, configuring, and monitoring shared services (MinIO, CloudNativePG, Keycloak, etc.) running on Kubernetes clusters. The application is deployed via Helm with support for Traefik, Istio, and Gateway API ingress options.

## Development Practices

### Pseudocode First (Story-Driven Development)

Pseudocode is not just a planning tool—it IS the documentation. Every method, function, and property should tell a story that anyone can follow, even without coding experience.

#### Core Principles
- **ALWAYS** write pseudocode BEFORE implementation code
- **ALWAYS** write pseudocode as a narrative story that describes what happens step-by-step
- **ALWAYS** interweave code with the story—never dump all story text at the top/bottom and code elsewhere
- **ALWAYS** use plain English that a non-developer can understand
- **NEVER** write code without explaining its purpose in the surrounding narrative

#### Story Format Rules
1. **Start with context**: Who is involved? What are they trying to accomplish?
2. **Describe each step as it happens**: The story unfolds as the code executes
3. **Explain decisions in human terms**: "If the user hasn't logged in, we redirect them" not "if user == null"
4. **Keep code snippets small**: Each snippet solves one specific part of the story
5. **End with the outcome**: What does success look like? What about failure?

#### What Good Pseudocode Looks Like

```csharp
/// <summary>
/// When a workflow reaches an activity node, the engine needs to execute that activity
/// and handle whatever outcome occurs. This method orchestrates that entire journey.
/// </summary>
public async Task<NodeResult> ExecuteActivityAsync(ActivityNode node, WorkflowContext context, CancellationToken ct)
{
    // First, we need to make sure this activity is actually ready to run.
    // An activity might have dependencies on previous steps, required inputs,
    // or conditions that must be met before it can execute.

    ValidationResult validation = await ValidatePrerequisitesAsync(node, context, ct);

    if (!validation.IsValid)
    {
        // The activity can't run yet. We'll tell the workflow engine why,
        // so it can either wait for dependencies or report the problem.
        return NodeResult.Blocked(validation.Reason);
    }

    // Now we gather the inputs this activity needs. These might come from
    // previous activity outputs, workflow variables, or external connections.
    // Think of this like a chef gathering ingredients before cooking.

    ActivityInputs inputs = await ResolveInputsAsync(node, context, ct);

    // With everything ready, we dispatch the activity to its worker service.
    // This is like sending a work order to a specialized department—we hand
    // off the task and wait for them to complete it.

    ActivityResult result = await dispatcher.DispatchAsync(node.ActivityType, inputs, ct);

    // Finally, we interpret what happened and translate it into something
    // the workflow engine understands. Did it succeed? Fail? Need a retry?

    if (result.IsSuccess)
    {
        // Success! We store the outputs so future activities can use them,
        // then tell the engine to move forward to the next node.
        await StoreOutputsAsync(node, result.Outputs, context, ct);
        return NodeResult.Completed(result.Outputs);
    }

    if (result.IsRetryable)
    {
        // Something went wrong, but it might work if we try again.
        // We'll let the engine decide whether to retry based on the workflow's retry policy.
        return NodeResult.Failed(result.Error, canRetry: true);
    }

    // A permanent failure. The workflow will need to handle this—perhaps
    // by running error handlers or notifying someone.
    return NodeResult.Failed(result.Error, canRetry: false);
}
```

#### Anti-Patterns to Avoid

```csharp
// ❌ BAD: Big wall of comments at the top, then uncommented code
/// <summary>
/// This method validates prerequisites, resolves inputs, dispatches the activity,
/// handles success by storing outputs, handles retryable failures, and handles
/// permanent failures. It uses the dispatcher to send work to worker services.
/// </summary>
public async Task<NodeResult> ExecuteActivityAsync(ActivityNode node, WorkflowContext context, CancellationToken ct)
{
    ValidationResult validation = await ValidatePrerequisitesAsync(node, context, ct);
    if (!validation.IsValid)
        return NodeResult.Blocked(validation.Reason);
    ActivityInputs inputs = await ResolveInputsAsync(node, context, ct);
    ActivityResult result = await dispatcher.DispatchAsync(node.ActivityType, inputs, ct);
    if (result.IsSuccess)
    {
        await StoreOutputsAsync(node, result.Outputs, context, ct);
        return NodeResult.Completed(result.Outputs);
    }
    if (result.IsRetryable)
        return NodeResult.Failed(result.Error, canRetry: true);
    return NodeResult.Failed(result.Error, canRetry: false);
}

// ❌ BAD: Technical jargon that requires coding knowledge
// Check null coalesce on validation result IsValid property
// Execute async dispatch with CancellationToken propagation
// Pattern match on result discriminated union

// ❌ BAD: Comments that just repeat the code
// Call ValidatePrerequisitesAsync
ValidationResult validation = await ValidatePrerequisitesAsync(node, context, ct);
// Check if IsValid is false
if (!validation.IsValid)
```

#### The Readability Test
Ask yourself: "Could someone from the business team read this code and understand what it does?" If the answer is no, add more narrative context.

Pseudocode is a critical investment. Time spent writing clear stories saves exponentially more time in debugging, onboarding, and maintenance.

### Domain Driven Design (DDD)

DDD is about modeling the business domain clearly—not about creating endless folders and abstractions. The goal is code that reads like the business process it represents.

#### Core Principles
- Use ubiquitous language that matches business terminology
- Prefer rich domain models over anemic models (behavior lives WITH data)
- Aggregate roots protect invariants
- Use value objects for concepts with identity based on values

#### Co-location Over Separation (Keep Related Code Together)

The most important DDD principle for this codebase: **code that changes together lives together**.

```
# ✅ GOOD: Feature/domain folders with everything needed in one place
Activities/
└── SqlActivity/
    ├── SqlActivity.cs          # The activity implementation
    ├── SqlConnectionValidator.cs   # Validation specific to SQL
    ├── SqlQueryBuilder.cs      # Query building logic
    └── SqlActivity.Tests.cs    # Tests right next to the code

# ❌ BAD: Scattered across technical concern folders
Services/
    └── SqlActivityService.cs
Repositories/
    └── SqlConnectionRepository.cs
Validators/
    └── SqlConnectionValidator.cs
Models/
    └── SqlQuery.cs
Interfaces/
    └── ISqlActivityService.cs
Tests/
    └── SqlActivityServiceTests.cs
```

#### Practical Guidelines

1. **Don't create abstractions until you need them**
   - No `IRepository<T>` for a single implementation
   - No `IService` when only one class will ever implement it
   - Extract interfaces when you actually have multiple implementations or need testability

2. **Domain logic stays with the domain object**
   ```csharp
   // ✅ GOOD: The workflow knows how to validate itself
   public class Workflow
   {
       public ValidationResult Validate() { /* ... */ }
       public void AddNode(WorkflowNode node) { /* ... */ }
   }

   // ❌ BAD: Separate validator class for simple domain rules
   public class WorkflowValidator
   {
       public ValidationResult Validate(Workflow workflow) { /* ... */ }
   }
   ```

3. **One file can contain multiple related types**
   ```csharp
   // SqlActivity.cs - all SQL activity concerns in one file
   namespace Flows.Activities.Sql;

   public class SqlActivity : IActivity { /* ... */ }

   public record SqlQueryResult(DataTable Data, int RowsAffected);

   public class SqlConnectionOptions { /* ... */ }
   ```

4. **Bounded contexts = separate projects, not separate folders**
   - `ClusterManagement` is one bounded context
   - `ServiceProvisioning` is another bounded context
   - Within a context, keep things simple and together

#### The Navigation Test
Ask yourself: "Can I understand this feature by looking at 1-2 files, or do I need to open 7+ files across different folders?" If the latter, consolidate.

### Test Driven Development (TDD)

TDD is **non-negotiable** in this codebase. The test is written **before** the production code exists — no exceptions.

#### The Rule

**Never write implementation code without a failing test that demands it.**

This means: when asked to add a feature, fix a bug, or introduce a new class, the **first thing you produce is a test** — not the implementation. The implementation comes second, and only enough of it to make the test pass.

#### The Cycle (Red → Green → Refactor)

Every change follows this exact sequence:

1. **RED — Write a failing test first**
   Write a test that describes the expected behavior. Run it (mentally or actually). It **must fail** because the production code doesn't exist yet or doesn't handle this case. If the test passes immediately, it's not testing anything new.

2. **GREEN — Write the minimum code to pass**
   Implement just enough production code to make the failing test pass. Do not write extra logic "while you're in there." Do not anticipate future requirements. The test defines the scope — nothing more.

3. **REFACTOR — Clean up while tests stay green**
   Improve the code structure, remove duplication, rename for clarity. Run the tests after every change. If a test breaks, undo the refactor and try again.

Then repeat: write the next failing test, make it pass, refactor.

#### What This Looks Like in Practice

```csharp
// STEP 1 (RED): We need a method that resolves inputs for an activity node.
// Before writing ResolveInputsAsync, we write the test:

[Fact]
public async Task ResolveInputsAsync_WithMappedOutputs_ReturnsMappedValues()
{
    // Arrange — set up a node that expects an input from a previous node's output
    ActivityNode node = new ActivityNode("SendEmail", new Dictionary<string, string>
    {
        ["recipient"] = "{{nodes.LookupUser.output.email}}"
    });

    WorkflowContext context = CreateContextWithNodeOutput("LookupUser", "email", "alice@example.com");

    // Act
    ActivityInputs inputs = await sut.ResolveInputsAsync(node, context, CancellationToken.None);

    // Assert — the input should contain the resolved value, not the template
    inputs["recipient"].Should().Be("alice@example.com");
}

// This test will NOT compile yet — ResolveInputsAsync doesn't exist.
// That's the point. Now we go write it.

// STEP 2 (GREEN): Implement ResolveInputsAsync with just enough logic to pass.
// STEP 3 (REFACTOR): Clean up, then write the next test (e.g., missing output, invalid template).
```

#### Why This Order Matters

- **Tests first** ensures you understand the requirement before writing code
- **Tests first** catches design problems early — if it's hard to test, the design needs work
- **Tests first** prevents "I'll add tests later" (later never comes)
- **Tests first** produces tests that actually verify behavior, not tests retrofitted to match existing code

#### Anti-Patterns to Avoid

```
❌ Writing the implementation first, then adding tests after
❌ Writing a test that passes immediately (it proves nothing)
❌ Writing all tests up front before any implementation (write one at a time)
❌ Skipping the refactor step (technical debt accumulates)
❌ Writing implementation code "while you're in there" beyond what the current test requires
```

#### The Litmus Test
If someone asks "which test drove you to write that line of code?" and you can't point to one — the line shouldn't exist yet.

#### Missing Test Project
If the service or project you are working on does not have a corresponding test project, **create one before writing the first test**. A missing test project is never a reason to skip TDD — it's a two-minute fix:

1. Create an xUnit test project named `<ProjectName>.Tests` (e.g., `ConnectionStore.Tests`)
2. Add it to the solution
3. Add a project reference to the project under test
4. Add the standard test dependencies (`xunit`, `FluentAssertions`, `Moq`, `Microsoft.NET.Test.Sdk`)
5. Then write the failing test as normal

## Code Style

### General
- Target: .NET 10
- Nullable reference types: enabled
- Implicit usings: enabled
- Use file-scoped namespaces
- Prefer `async/await` for all I/O operations
- Use `CancellationToken` for async operations
- **Treat all IDE code style warnings as build errors and fix them**

### Type Declarations
- **ALWAYS use explicit types instead of `var`**
- **NEVER use `var` for any variable declaration**
- For anonymous types, use `object` or `dynamic` instead of `var`
- Example:
  ```csharp
  // CORRECT
  string name = "John";
  List<int> numbers = new List<int>();
  Dictionary<string, object?> dict = new Dictionary<string, object?>();
  object anonymousResult = new { Id = 1, Name = "Test" };

  // INCORRECT - never do this
  var name = "John";
  var numbers = new List<int>();
  var dict = new Dictionary<string, object?>();
  var anonymousResult = new { Id = 1, Name = "Test" };
  ```

### Braces and Control Flow
- **ALWAYS use braces `{}` for all control flow statements**, even single-line bodies
- This applies to: `if`, `else`, `foreach`, `for`, `while`, `do-while`, `using`, `lock`
- Example:
  ```csharp
  // CORRECT
  if (condition)
  {
      DoSomething();
  }

  foreach (string item in items)
  {
      ProcessItem(item);
  }

  using (FileStream stream = new FileStream(path, FileMode.Open))
  {
      ReadData(stream);
  }

  // INCORRECT - never do this
  if (condition)
      DoSomething();

  foreach (var item in items)
      ProcessItem(item);
  ```

### Blank Lines and Spacing
- **ALWAYS add a blank line between code blocks** — after `if`/`else`, `foreach`, `for`, `while`, `using`, `lock`, `try`/`catch`/`finally`, `switch`, and method declarations
- A "block" is anything enclosed in `{}`, including control flow, methods, properties, and type declarations
- Example:
  ```csharp
  // CORRECT — blank line between blocks
  if (connection == null)
  {
      throw new InvalidOperationException("No connection");
  }

  foreach (string item in items)
  {
      ProcessItem(item);
  }

  string result = ComputeResult();
  return result;

  // INCORRECT — no blank line between blocks
  if (connection == null)
  {
      throw new InvalidOperationException("No connection");
  }
  foreach (string item in items)
  {
      ProcessItem(item);
  }
  string result = ComputeResult();
  return result;
  ```

### Naming Conventions
- PascalCase for public members, types, and namespaces
- camelCase for private fields (no underscore prefix)
- Suffix async methods with `Async`
- Prefix interfaces with `I`
- Suffix test classes with `Tests`

### Error Handling
- Use the Result pattern (`ActivityResult`, `Result<T>`) over exceptions for expected failures
- Exceptions are for unexpected/exceptional conditions only
- Always log errors with structured logging
- When adding logging, always respect tenant log level settings. Diagnostic logging should not be enabled everywhere throughout the app when it's not needed - it must be filtered based on tenant settings.

## Architecture

### Microservices with Blazor BFF

EntKube follows a **microservices architecture** with a Blazor BFF (Backend-for-Frontend) as the user-facing entry point. Each service owns its bounded context, has its own data store, and can be deployed and scaled independently. This is NOT a nano-services architecture — we split by meaningful business boundaries, not by technical layers.

#### Core Principles
- **4 services, each with a clear responsibility**: Web (BFF), Clusters, Provisioning, Identity
- **Each service owns its data**: No shared databases between services
- **Services communicate via HTTP APIs**: Simple REST calls between services, with resilient retry policies
- **SharedKernel for contracts only**: Shared types (Result, ApiResponse, base Entity) live in a shared library — but no shared business logic
- **Feature folders over layer folders**: Each feature is a vertical slice (handler + endpoint + related types in one folder)

#### Service Boundaries

| Service | Responsibility | Port (dev) |
|---------|---------------|-------------|
| **EntKube.Web** | Blazor BFF — serves UI, proxies API calls to backend services, owns user auth session | 5000 |
| **EntKube.Clusters** | Kubernetes cluster registration, health monitoring, API connectivity | 5010 |
| **EntKube.Provisioning** | Shared service lifecycle (MinIO, CNPG, Keycloak) — provisioning, reconciliation, teardown | 5020 |
| **EntKube.Identity** | Tenant management, user membership, roles, Keycloak integration | 5030 |

#### Anti-Patterns to Avoid
```
# ❌ BAD: Nano-services — splitting too granularly
MinIOService/
CloudNativePGService/
KeycloakService/
HealthCheckService/        # These belong together under "Provisioning"

# ❌ BAD: Shared database between services
# Services must own their own data — cross-service queries go through APIs

# ✅ GOOD: Meaningful service boundaries with feature folders
src/
├── EntKube.Web/                    # Blazor BFF
├── EntKube.Clusters/               # Cluster management service
│   ├── Domain/                     # Aggregates, value objects, repository contracts
│   ├── Features/                   # Vertical slices (RegisterCluster/, GetClusters/, etc.)
│   └── Infrastructure/             # Repository implementations, external integrations
├── EntKube.Provisioning/           # Service provisioning service
│   ├── Domain/
│   ├── Features/
│   └── Infrastructure/
├── EntKube.Identity/               # Identity & tenant service
│   ├── Domain/
│   ├── Features/
│   └── Infrastructure/
└── EntKube.SharedKernel/           # Shared contracts (Result, ApiResponse, base Entity)
```

### Project Structure
```
Solution/
├── src/
│   ├── EntKube.SharedKernel/         # Shared types and contracts between services
│   │   ├── Domain/                   # Result, Entity base class
│   │   └── Contracts/                # ApiResponse envelope, DTOs
│   ├── EntKube.Web/                  # Blazor Server BFF
│   │   ├── Components/               # Razor components, layouts, pages
│   │   ├── Data/                     # EF Core DbContext (Identity only)
│   │   └── wwwroot/                  # Static assets
│   ├── EntKube.Web.Client/           # Blazor WebAssembly client
│   │   └── Pages/                    # Interactive WASM pages
│   ├── EntKube.Clusters/             # Cluster management API
│   │   ├── Domain/                   # KubernetesCluster aggregate
│   │   ├── Features/                 # RegisterCluster/, GetClusters/
│   │   └── Infrastructure/           # Repository implementations
│   ├── EntKube.Provisioning/         # Service provisioning API
│   │   ├── Domain/                   # ServiceInstance aggregate
│   │   ├── Features/                 # ProvisionService/, GetServices/
│   │   └── Infrastructure/           # Repository implementations
│   └── EntKube.Identity/             # Identity & tenant API
│       ├── Domain/                   # Tenant aggregate
│       ├── Features/                 # CreateTenant/
│       └── Infrastructure/           # Repository implementations
├── tests/
│   ├── EntKube.Clusters.Tests/       # Unit + integration tests
│   ├── EntKube.Provisioning.Tests/
│   ├── EntKube.Identity.Tests/
│   └── EntKube.Web.Tests/
├── Charts/                           # Helm charts for deployment
│   └── entkube/
│       ├── Chart.yaml
│       ├── values.yaml
│       └── templates/
└── .gitea/workflows/                 # Gitea Actions CI/CD
```

### Deployment & Infrastructure

#### Kubernetes + Helm
- The application is packaged and deployed via **Helm charts**
- Helm values support multiple ingress strategies: **Traefik**, **Istio**, and **Gateway API**
- Each environment (dev, staging, prod) has its own values override file
- Deployments use `helm upgrade --install` with atomic rollbacks

#### Ingress Configuration
```yaml
# values.yaml — select your ingress strategy
ingress:
  enabled: true
  provider: traefik  # Options: traefik | istio | gatewayapi
  host: entkube.example.com
  tls:
    enabled: true
    secretName: entkube-tls
```

- **Traefik**: Uses IngressRoute CRDs with middleware for rate limiting, auth forwarding
- **Istio**: Uses VirtualService + Gateway resources with mTLS between services
- **Gateway API**: Uses HTTPRoute + Gateway resources (vendor-neutral standard)

#### CI/CD — Gitea Actions
- Code is hosted in **Gitea** and built using **Gitea Actions**
- Every project in the solution has a build workflow that triggers on push/PR
- Workflows build, test, and publish container images to the Gitea container registry
- Helm chart is packaged and pushed to a Helm OCI registry

```yaml
# .gitea/workflows/build.yaml (conceptual structure)
name: Build EntKube
on:
  push:
    branches: [main]
  pull_request:
    branches: [main]

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - run: dotnet restore
      - run: dotnet build --no-restore
      - run: dotnet test --no-build
      - run: docker build -t gitea.example.com/entkube/entkube:${{ github.sha }} .
      - run: docker push gitea.example.com/entkube/entkube:${{ github.sha }}
```

### Communication Patterns
- **Service-to-service**: HTTP REST via typed HttpClient with Polly retry policies
- **BFF-to-service**: The Web BFF proxies user requests to the appropriate backend service
- **Kubernetes API**: The Clusters service communicates with k8s API servers using the official .NET client
- **Async workflows**: Background services within each microservice handle reconciliation loops (e.g., provisioning, health checks)
- **No message bus yet**: Start with synchronous HTTP; extract to async messaging (NATS, RabbitMQ) only when proven necessary

### Database

This solution uses **Entity Framework Core** with dual-provider support: **SQL Server** (default) and **PostgreSQL**. The active provider is selected at startup based on configuration.

#### DbContext Setup
- The application has a base `DbContext` (e.g., `ApplicationDbContext`) targeting SQL Server
- A provider-specific subclass (e.g., `PostgresApplicationDbContext : ApplicationDbContext`) exists for PostgreSQL
- `DbContext` is registered as scoped (one per request)
- The active provider is selected at startup via a `DatabaseProvider` configuration value (`"SqlServer"` or `"Postgres"`)
- SQLite is used for local development convenience

#### Migrations — Dual Provider

When domain models change, migrations **must** be added for **both** SQL Server and PostgreSQL. Each migration produces a `.cs` file and a `.Designer.cs` file.

**Folder layout:**
```
EntKube/
├── Data/
│   └── Migrations/                    # SQL Server migrations (default)
│       ├── 20250115_AddSomething.cs
│       └── 20250115_AddSomething.Designer.cs
└── Data/Migrations/Postgres/          # PostgreSQL migrations
    ├── 20250115_AddSomething.cs
    └── 20250115_AddSomething.Designer.cs
```

**Commands to generate migrations (run from the solution root):**
```bash
# SQL Server (default context)
dotnet ef migrations add <MigrationName> -p EntKube/EntKube -s EntKube/EntKube

# PostgreSQL (Postgres-specific context, output to Postgres folder)
dotnet ef migrations add <MigrationName> -p EntKube/EntKube -s EntKube/EntKube \
    --output-dir Data/Migrations/Postgres \
    --context PostgresApplicationDbContext
```

The project has a `DesignTimeDbContextFactory` (and a Postgres variant) implementing `IDesignTimeDbContextFactory<T>` so the EF tooling can create the context without running the application.

#### Auto-Migration on Startup

Migrations are **automatically applied** when the application starts in `Program.cs`. The pattern uses retry logic with exponential backoff to handle containerized environments where the database may not be immediately available:

```csharp
// In Program.cs after building the app — apply pending migrations before serving traffic
using (IServiceScope scope = app.Services.CreateScope())
{
    DbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    MigrateWithRetry(db, logger);
}
```

This means:
- **Never** rely on a separate migration step in CI/CD — the app migrates itself on boot
- **Always** test that new migrations apply cleanly against both providers before merging
- The testing environment skips auto-migration (uses in-memory or test-configured databases)

#### Rules
- Always generate migrations for **both** providers when models change
- Always include the `.Designer.cs` files — they are required for EF to resolve the migration model snapshot
- Use `ConfigureWarnings` to ignore `PendingModelChangesWarning` when needed during development

## Testing Standards

### Framework
- xUnit for test framework
- FluentAssertions for assertions
- Moq for mocking

### Test Organization
```csharp
[Fact]
public void MethodName_Scenario_ExpectedResult()
{
    // Arrange
    
    // Act
    
    // Assert
}
```

### Coverage Requirements
- Unit tests for all domain logic
- Integration tests for service boundaries
- E2E tests for critical workflows

## Blazor Patterns

- Prefer Blazor patterns over Razor Pages or MVC
- Use component-based architecture
- Server-side rendering with WebAssembly interactivity
- BFF pattern for API proxying to Kubernetes APIs
- Use `BackgroundService` for background tasks (e.g., cluster health polling)
- Implement graceful shutdown via cancellation tokens
- Include health checks for Kubernetes readiness/liveness probes

## Kubernetes Integration

### Managed Services
EntKube manages the lifecycle of shared Kubernetes applications including:
- **MinIO** — Object storage
- **CloudNativePG (CNPG)** — PostgreSQL operator
- **Keycloak** — Identity and access management
- Additional services as needed

### Cluster Communication
- Use the official Kubernetes .NET client (`KubernetesClient`) for API interactions
- Support both in-cluster and out-of-cluster kubeconfig authentication
- Wrap Kubernetes API calls with Polly retry policies for transient failures
- Use watches/informers for real-time resource state updates where appropriate

## Debugging Deployments
- When debugging deployed instances, assume the latest code version is deployed — Helm deployments use `helm upgrade --install --atomic` ensuring clean rollouts
- A version number is visible in the frontend showing the deployed version
