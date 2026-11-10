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
| Delete | Deletes the `VirtualMachine`. Disks it imported are owned by it and go with it. |

Every action goes through the cluster change gate, so the operator sees and acknowledges it first, and
each one is written to the audit log with the actor's name.

### There is no table

A `VirtualMachine` is already a declarative object that KubeVirt reconciles. The cluster is the
record, and the section reads it live. A copy in EntKube's database would be a second source of truth
for the one property that matters, whether the VM is running, and it would drift the first time
someone used `virtctl`. VMs created outside EntKube show up and can be operated like any other.

### Power actions without subresources

KubeVirt's own start, stop and restart endpoints are subresource `PUT`s (`/apis/subresources.kubevirt.io/…`),
and the cluster seam has no verb for an arbitrary API path. The same effects are reachable
declaratively, through verbs the gate already covers, so no new un-gated path was added. Pause,
unpause and the VNC/serial console are subresource-only and not offered.

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
- **Console.** VNC and serial are websocket subresources the seam cannot reach.
- **Live migration** from the page. The KubeVirt resource still asks for VMs to be live-migrated across
  KubeVirt upgrades, which works when the VM's storage allows it (container disks, or RWX volumes).
