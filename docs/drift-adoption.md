# Adopting live state into a deployment's manifests

Drift has two honest answers. **Overwrite** re-applies the stored manifests and
discards the out-of-band change. **Adopt** does the opposite: it keeps the cluster as
it is and pulls the change back into the stored manifests, so the next sync stops
reverting it.

Adopt changes only EntKube's stored desired state. It never touches the cluster.

## Why a live object is not a manifest

The API server owns a large part of every object it serves back. Storing one verbatim
produces desired state that is unreadable, that re-applies with immutable-field errors,
and that cannot be applied to a second cluster at all — which is most of what a stored
manifest is for.

Removed before storing:

| Removed | Why |
|---|---|
| `status` | Entirely server-computed. Describes what happened, not what was wanted. |
| `metadata.uid`, `resourceVersion`, `generation`, `creationTimestamp`, `managedFields` | Identity and history of *this* object on *this* cluster. |
| `metadata.ownerReferences` | The object is created by a controller; it should not be applied directly. |
| `kubectl.kubernetes.io/last-applied-configuration` | A full copy of the previous manifest. Keeping it nests the object inside itself and grows on every apply. |
| `deployment.kubernetes.io/revision` and similar | Controller bookkeeping. |
| Service `clusterIP`, `clusterIPs`, `nodePort` | Allocated by this cluster. Immutable, and unavailable anywhere else. |
| PVC `spec.volumeName` | Names a PersistentVolume that exists only here. |
| ServiceAccount `secrets` | Auto-created token references. |

The guiding rule is to remove **only** what is unambiguously server-owned or bound to
this cluster. Over-stripping silently discards something the operator set, and a
manifest that quietly lost a field is worse than a verbose one — so anything ambiguous
is kept, and anything removed is listed in the proposal.

## Secrets are refused

A `Secret`'s data *is* the secret. Copying it into a stored manifest would put
credentials in the deployment's YAML — readable in the editor, in the database, and in
any export — when EntKube has a vault precisely so that never happens.

Adopting the object with the data stripped would be worse: the next apply would
overwrite the live Secret with an empty one.

So Secrets are refused outright, their stored manifest is left untouched, and the
proposal says so.

## Per resource, never wholesale

Adoption matches each stored manifest to its live object by kind and name and offers
them one at a time. It never replaces the whole manifest set with a dump of live state.

That matters because EntKube **prunes** resources that disappear from a manifest set.
With wholesale replacement, anything that could not be adopted — a Secret above all —
would vanish from the manifests, and "lost from the manifests" becomes "deleted from
the cluster" on the next apply.

Statuses in the proposal:

| Status | Meaning |
|---|---|
| **Differs** | Live state differs. The only status that can be selected. |
| **Matches** | Already identical; adopting would be a no-op. |
| **Cannot adopt** | A Secret. Left exactly as it is. |
| **Not on cluster** | The manifest describes something that is gone. Adopting cannot represent a deletion — remove it from the manifests instead. |
| **Unreadable** | The live object could not be fetched. |

## Confirming

Nothing is written until you select entries and confirm. The proposal shows the stored
manifest and the proposed one side by side.

**There is no manifest history to undo this with.** Read the comparison before
confirming.

On confirm the proposal is rebuilt server-side rather than trusting YAML posted back
from the browser: this writes desired state, and accepting arbitrary content from a
client would make that an open door. It also means a resource that changed again
between preview and confirm is adopted as it is now, not as it was.
