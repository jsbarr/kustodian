---
status: accepted
---

# Impacted-entity consistency is join/lookup aware, driven by extracted keys

The impacted-entity consistency check treats two required columns as consistent when a matching `join`/`lookup` **key** proves their values equal, not only when they trace to identical leaves. An inner/innerunique join equates both sides; a `leftouter`/`rightouter` (and `lookup`, whose default kind is leftouter) attributes the key to its surviving side; `fullouter`, the semi/anti kinds, and `union` equate nothing. This lets a detection that enriches via `lookup ... on <impactedEntity>` pass, while same-table `union` splits and non-key joined columns still fail.

## Why driven by extracted keys, not the provenance tree

We keep the returned provenance tree faithful to actual data flow (no synthetic equality edges) and put the value-equality reasoning entirely in the consistency check. The check cannot, however, read the collapsible forks off the SDK's `OriginalColumns`: when the same physical column is the key on both sides of a same-table lookup (e.g. `BaseSearch.DeviceId` and `Enrichment.DeviceId`, both pass-throughs of `DeviceEvents.DeviceId`), the SDK returns the **same `ColumnSymbol` object twice** (`ReferenceEquals == true`). Provenance leaf identity is keyed by symbol, so the two sides are indistinguishable and `columnTableRefPos` collapses both to one position — making a faithful-tree-only check unable to tell the base side from the enrichment side.

The only way to recover distinct, correctly-positioned branch identities is to read the operator's `on` clause syntactically during the AST walk, resolving the left-key occurrence separately from the right-key occurrence, and to record the join kind. "Fixing the provenance defect" and "extracting the keys" are therefore the same change, not alternatives.

## Consequences

- The AST walk gains a per-`join`/`lookup` fact: kind plus left/right key occurrence(s) (covering `$left.X == $right.Y` and multi-key `on K1, K2`).
- Collapse is **direction-aware** by kind; a symmetric "pick either branch" rule would unsoundly greenlight, e.g., taking `Timestamp` from the enrichment side of a `leftouter`.
- Collapsibility applies uniformly to whichever of the three required columns is a key, not just the impacted entity.
- The collapse mark lives **on the provenance-tree node** (forced by the rename case, where the key fork sits nested under a `project`/`project-rename`), not in a flat sidecar. The check is a tree walk computing, per column, the set of achievable leaf-sets under allowed-direction branch choices; the triplet is consistent iff the three families share a common member.
- The mark refines the *faithful* tree — corrected branch positions plus an accurate label, no synthetic edges — and is **exposed in the API response and UI** (and documented in the README). It carries only `operator` (`"join"`/`"lookup"`) and `kind` (e.g. `"leftouter"`), both facts of the query. Collapse **direction** is *not* stored: it is a semantic conclusion the check derives from the kind, not a fact of the query, so it stays out of the tree.
- Synthesising a fork for a `join` left key (so an engineer may use the *other* side's key in the triplet) is deferred; if added, it lives only in the check, never in the returned provenance tree.
