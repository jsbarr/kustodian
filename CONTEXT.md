# Kustodian

Kustodian is a KQL linting and inspection web service that validates Microsoft Defender XDR detection queries before they are deployed. This glossary fixes the vocabulary the service, its API, and its documentation use to talk about queries, their analysis, and the environments they run against.

## Language

### Detection domain

**Detection query**:
A KQL query, written for Defender XDR Advanced Hunting, that Kustodian analyses. In the API this is simply the `query` field.
_Avoid_: hunting query, rule, detection rule

**Detection engineer**:
The person who authors detection queries; Kustodian's primary user, alongside the CI and review tooling acting on their behalf.
_Avoid_: analyst, hunter, author

**Impacted entity**:
The thing a detection identifies as affected — a device, account, and so on. The detection must surface it so an alert points at a single subject.
_Avoid_: affected entity, target, asset

**Impacted entity field**:
The one output column that names the impacted entity (e.g. `DeviceId`, `AccountUpn`), supplied per request. Must appear in the output alongside `Timestamp` and `ReportId`.
_Avoid_: entity column, key column

**Required output columns**:
The three columns every detection query must output: `Timestamp`, `ReportId`, and the impacted entity field. Their absence is an error.
_Avoid_: mandatory columns, key fields

**Alert evidence**:
The data a detection contributes to an alert. Evidence is misleading when its required columns are drawn from unrelated source rows.
_Avoid_: alert row, result row

### Analysis

**Provenance**:
The tree of sources feeding an output column — each source tracing back through the ones that feed it, down to leaves.
_Avoid_: lineage, derivation, history

**Source**:
A node in a column's provenance tree, identified by a `(symbol, position)` pair — the symbol being the unit of KQL that defines it (a table, an operator, ...). A source fans out into the upstream sources that feed it.
_Avoid_: node, input, dependency

**Leaf**:
A source with no upstream sources, where a provenance trace bottoms out. Either a table column (e.g. from `DeviceFileEvents`) or a value calculated in the query (e.g. `extend Col = 123`).
_Avoid_: leaf source, root column, origin

**Derived column**:
An output column the query introduces (via `extend`, `project`, `summarize`, and the like) rather than carrying through from a table. Naming conventions apply only to these.
_Avoid_: computed column, new column

**Pass-through column**:
An output column carried unchanged from a source table, or a plain rename of one. Its name is not the author's choice, so naming conventions do not apply.
_Avoid_: raw column, passthrough

**Consistency**:
The property that the required output columns all trace to the same set of table-column leaves (calculated leaves are not compared). When they don't — e.g. `Timestamp` from one table and `ReportId` from another after a join — their provenance is *inconsistent* and Kustodian errors.
_Avoid_: agreement, alignment

**Diagnostic**:
A single error or warning Kustodian emits about a query, carrying a level (`ERROR`/`WARN`) and a type (`Syntax`, `ImpactedEntityExistence`, `ImpactedEntityConsistency`, `ColumnNamingConvention`).
_Avoid_: message, finding, issue, lint

### Environment

**Environment**:
A named set of tables and functions that a detection query may reference, defined by a manifest. Built-ins are `defender-xdr` and `sentinel`; every analysis runs against exactly one.
_Avoid_: schema, workspace, database, target

**Manifest**:
The top-level document that defines an environment, declaring by name which tables and functions the environment exposes.
_Avoid_: config, index

**Table schema**:
The definition of one table as a flat map of column name to KQL scalar type.
_Avoid_: table definition, columns

**Function definition**:
The definition of one reusable function: a parameter signature plus the body expression or tabular pipeline it evaluates to.
_Avoid_: UDF, stored function

**Naming convention**:
An optional per-request regex that every derived column name must match; a mismatch is a warning.
_Avoid_: name pattern, naming rule
