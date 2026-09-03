# terraform-provider-entkube

Manage EntKube's own configuration declaratively — the settings a platform team
would rather keep in version control than click into a UI.

It talks to the same public `/api/v1` surface as every other client and carries an
ordinary scoped API token, so it can do exactly what the token permits and no more.

## Usage

```hcl
terraform {
  required_providers {
    entkube = { source = "entit-ab/entkube" }
  }
}

provider "entkube" {
  # Prefer the environment: a token written into a .tf file ends up in version control.
  # ENTKUBE_URL   = https://entkube.example.com
  # ENTKUBE_TOKEN = ekp_...
}

data "entkube_clusters" "all" {}

resource "entkube_cost_rate" "prod" {
  cluster_id               = [for c in data.entkube_clusters.all.clusters : c.id
                              if c.name == "prod-eu-west-1"][0]
  cpu_core_hour_cost       = 0.031
  memory_gib_hour_cost     = 0.004
  storage_gib_month_cost   = 0.10
  cluster_monthly_overhead = 250
  currency                 = "EUR"
}
```

The token needs `config:read` and `config:write`, plus `fleet:read` for the
`entkube_clusters` data source.

## What it manages

| Type | Name | |
|---|---|---|
| Resource | `entkube_cost_rate` | A cluster's price sheet. Supports import by cluster id. |
| Data source | `entkube_clusters` | Registered clusters, so configurations can reference a cluster by name rather than hard-coding an id. |

This is a deliberately small surface. Most of EntKube's API is read-only by design,
and a resource is only worth adding where declarative management is genuinely better
than the UI — configuration, not operations. Triggering a deployment sync from
Terraform, for instance, would be the wrong shape: it is an action, not a desired state.

## Building and trying it locally

```bash
go build -o /tmp/tfbin/terraform-provider-entkube .

cat > /tmp/tfrc <<'RC'
provider_installation {
  dev_overrides { "entit-ab/entkube" = "/tmp/tfbin" }
  direct {}
}
RC

export TF_CLI_CONFIG_FILE=/tmp/tfrc
export ENTKUBE_URL=https://entkube.example.com ENTKUBE_TOKEN=ekp_...
terraform plan
```

`dev_overrides` skips `terraform init`, so the provider is used straight from disk.

## Behaviour worth knowing

- **Read reflects the server, not the plan.** State is set from what EntKube stored,
  so if a value was clamped or normalised, the next plan shows it as drift rather than
  hiding it.
- **A resource deleted outside Terraform is removed from state** and re-created on the
  next apply — but *only* on an explicit 404. Doing that on a transient error would
  silently discard a resource that still exists.
- **Destroying something already gone succeeds**, so a half-finished destroy can be
  completed rather than being stuck forever.
- **Changing `cluster_id` replaces the resource.** A price sheet is keyed by cluster,
  so retargeting it is a different resource — not a silent repricing of another cluster.
