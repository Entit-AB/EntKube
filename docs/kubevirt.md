# Virtual machines (KubeVirt)

EntKube runs virtual machines through KubeVirt, which schedules each VM as a pod next to containers.
KubeVirt and its disk importer, CDI, are catalog components. The machines themselves are created and
operated under **Services › Virtual Machines**.

## What gets installed

| Piece | Where it comes from |
| --- | --- |
| KubeVirt operator | Catalog component `kubevirt-operator`: release manifest `kubevirt-operator.yaml` v1.9.1, namespace `kubevirt` |
| KubeVirt | Catalog component `kubevirt`: the `KubeVirt` resource the operator deploys virt-api, virt-controller and virt-handler from |
| CDI operator | Catalog component `kubevirt-cdi-operator`: release manifest `cdi-operator.yaml` v1.66.1, namespace `cdi` |
| CDI | Catalog component `kubevirt-cdi`: the `CDI` resource the operator deploys the importer from |

KubeVirt ships no official Helm chart, so each project is two entries: a pinned `ManifestUrl` for the
operator and a `Manifest` for the resource that switches it on. Installing an operator on its own
deploys nothing. The resource entries depend on their operator, and they are detected by a live
instance rather than by the CRD, because the operator brings that CRD with it.

CDI is optional. Without it, VMs can only boot from container disks.

### What the KubeVirt resource sets

| Setting | Value | Why |
| --- | --- | --- |
| `uninstallStrategy` | `BlockUninstallIfWorkloadsExist` | Uninstalling the component deletes this resource. While any VM exists, that deletion is refused rather than stopping every VM on the cluster. |
| `workloadUpdateStrategy` | `LiveMigrate` | After a KubeVirt upgrade, running VMs move onto the new virt-launcher instead of staying on the old one until someone restarts them. |
| `developerConfiguration.useEmulation` | form toggle, default off | Lets KubeVirt run without `/dev/kvm`. See below. |

### Nodes need hardware virtualization

virt-handler runs VMs through `/dev/kvm`. Bare metal has it. A cloud VM has it only when the provider
enables nested virtualization; most OpenStack flavours do not. Without it, every VM stays in
`ErrorUnschedulable` with `Insufficient devices.kubevirt.io/kvm`. The page shows that message on the
VM's row.

The **Software emulation** toggle on the KubeVirt component makes KubeVirt fall back to QEMU's TCG.
VMs then run, but several times slower. Use it for trying things out, not for production.

## Services › Virtual Machines

The section lists the VMs on the environment's clusters and acts on them:

| Action | What reaches the cluster |
| --- | --- |
| Create | The namespace if it is missing, then a `VirtualMachine`. Acknowledged once, as one operation. |
| Start / Stop | A merge patch setting `spec.runStrategy` to `Always` / `Halted`. The patch also clears the deprecated `spec.running`, because KubeVirt rejects a VM that sets both. |
| Restart | Deletes the `VirtualMachineInstance`. KubeVirt boots a fresh one. Refused unless the run strategy is `Always` or `RerunOnFailure`; under `Manual` the same delete would just stop the VM. |
| Pause / Unpause | `PUT` to the instance's `pause` / `unpause` subresource. The guest is frozen in memory, not shut down, and carries on where it was. |
| Delete | Deletes the `VirtualMachine`. Disks it imported are owned by it and go with it. |

Every action goes through the cluster change gate, so the operator sees and acknowledges it first, and
each one is written to the audit log with the actor's name.

### There is no table

A `VirtualMachine` is already a declarative object that KubeVirt reconciles. The cluster is the
record, and the section reads it live. A copy in EntKube's database would be a second source of truth
for the one property that matters, whether the VM is running, and it would drift the first time
someone used `virtctl`. VMs created outside EntKube show up and can be operated like any other.

### Power actions and subresources

Start, stop and restart stay declarative: they go through verbs the gate already covers, and the
dialog can show the patch.

Pause and unpause exist only as subresource `PUT`s (`/apis/subresources.kubevirt.io/v1/…/pause`).
They go through `InvokeSubresourceAsync`, a seam verb added for them with its own gate verb,
`ChangeVerb.Invoke`. The alternative was the k8s SDK's raw `HttpClient`, which neither the gate nor any
ratchet can see. Every path segment is validated as a DNS name, so a caller cannot steer the request
to another path. An invocation always reaches the dialog, because there is nothing to diff, so it
can never be read as "no change".

### Console

Each running (or paused) VM has two consoles, opened from its row:

| Console | Subresource | In the browser |
| --- | --- | --- |
| Screen | `virtualmachineinstances/{name}/vnc` | noVNC 1.7.0, with Ctrl+Alt+Del |
| Serial | `virtualmachineinstances/{name}/console` | xterm.js 6.0.0 |

The browser connects to EntKube at `/vm-console/{tenant}/{cluster}/{namespace}/{vm}/{vnc|serial}`.
EntKube connects to KubeVirt with the cluster's credential under the `plain.kubevirt.io` subprotocol,
and copies bytes both ways. The credential never leaves `KubernetesClientFactory`: the socket is opened
there through the client library's own websocket connect, so a client certificate, a token or an exec
plugin all work.

The endpoint is the security boundary, and it checks everything itself. It is a websocket upgrade,
not a Blazor circuit, so none of the tenant page's checks have run for it:

1. **Signed in.** The cookie, through `RequireAuthorization`.
2. **Same origin.** A websocket handshake carries cookies but is exempt from CORS and antiforgery,
   so without this check a page on another site could open a console as whoever visits it. A
   missing `Origin` is refused.
3. **A member of the tenant in the URL**, through `UserAccessService.HasTenantAccessAsync`. The URL is
   the caller's to write. A stranger gets a 404.
4. **The cluster is that tenant's**, through `ForAsync`, as everywhere else.

A console is **not acknowledged**, because it changes no cluster state and an HTTP request has no
circuit to ask in. It **is audited**: every session that connects writes `vm.console` with the
person's name. If the audit cannot be written, the session is closed rather than handed over.
Closing the panel or leaving the page ends the session.

It sits outside `/api/v1`, so it carries no API token, scope, CLI verb or MCP tool.

The libraries are vendored under `wwwroot/lib/novnc` and `wwwroot/lib/xterm`, with their licences
beside them: MPL-2.0 for noVNC, MIT for its bundled pako, and MIT for xterm.js. They load from the
same origin, so the default CSP needs no change.

### Root disks

| Source | Needs | Persistence |
| --- | --- | --- |
| Container disk (`quay.io/containerdisks/…`) | Nothing | **Ephemeral**: every restart boots the pristine image. |
| Import cloud image (http/https URL) | CDI | A `dataVolumeTemplate` the VM owns. Survives restarts and is deleted with the VM. |

While CDI imports a disk, the VM's status is `Provisioning` and its row shows the import progress.

### Cloud-init

Every VM gets a NoCloud cloud-init disk that sets its hostname. An optional SSH public key goes into
`ssh_authorized_keys`, which cloud-init installs for the image's own default user (`ubuntu`, `debian`,
`fedora`, `cloud-user`…). Naming a user instead would replace that default user, and every distro
names it differently.

The manifest and the cloud-config are built as data and serialized, never interpolated. The image
reference, the URL and the SSH key are typed by a person, so they must stay values whatever
characters they contain.

### Networking

Each VM has one interface on the pod network, using `masquerade`. It is reachable from inside the
cluster at the IP the row shows. Exposing it outside the cluster, for example for SSH, means putting
a Service in front of it, selected by the `kubevirt.io/vm: <name>` label the template carries.

## Not yet

- **API, CLI and MCP.** The section is UI-only for now, as Search is. Adding routes brings in the API
  scope, tenant-reach, CLI-parity and MCP-parity ratchets.
- **Live migration** from the page. The KubeVirt resource still asks for VMs to be live-migrated across
  KubeVirt upgrades, which works when the VM's storage allows it (container disks, or RWX volumes).
