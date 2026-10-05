# EntKube.Contracts

What one module is allowed to know about another.

## Rules

1. **No reference to `EntKube.Web`, ever.** If this project could see the EF entities, the
   contracts would become a second name for the database and the boundary would be
   decorative. `ModuleContractTests` fails the build if the reference appears.
2. **Contracts carry their own types.** A method returns a record defined here, never an
   entity. An entity is a row with navigation properties attached to a change tracker; a
   contract is a promise about data.
3. **One `I{Module}Api` per module**, covering what that module offers everyone else — not
   its internals.
4. **Every method takes a tenant and a `CancellationToken`.** The tenant because nothing in
   EntKube is installation-wide except the installation itself; the token because these all
   become network calls eventually and an un-cancellable network call is a hang.

## Why this exists

`docs/decomposition.md` argues the API is the product: 22 endpoints against ~250 services,
and the same missing contract blocks microservices, BFFs and in-cluster agents alike. These
interfaces are that contract in-process first, so the HTTP surface can be generated from
them rather than hand-maintained — which is how the 22 endpoints came to be an exception
rather than the rule.
