# Kustodian

A KQL linting and inspection web service for Defender XDR detection engineers.

## What it does

Kustodian validates KQL detection queries before they're deployed. Given a query, it checks:

- **Syntax** — the query is valid KQL and all column/table references resolve in the target environment.
- **Required output columns** — `Timestamp`, `ReportId`, and a caller-specified impacted entity field (e.g. `DeviceName`, `AccountUpn`) must all appear in the output.
- **Impacted entity consistency** — the required columns must all trace back to the same source tables. A query that mixes `Timestamp` from `DeviceEvents` with `AccountUpn` from `IdentityInfo` produces misleading alert evidence, and Kustodian flags it.
- **Naming conventions** — newly introduced columns (not raw table pass-throughs) can be validated against a regex pattern.
- **Column provenance** — every output column is traced back through the query pipeline to the leaf table columns it was derived from, giving a full picture of where the data came from.

The intended audience is **detection engineers** writing custom detection queries in Microsoft Defender XDR Advanced Hunting, and the **CI pipelines or review tooling** that validate those queries.

## Running with Docker

```bash
docker run -p 8080:8080 jsbarr/kustodian # From DockerHub
```

or:

```bash
# Build from source
docker build -t kustodian .
docker run -p 8080:8080 kustodian
```

## Web UI

Kustodian ships a browser-based UI served from `/` on the same port as the API. Open `http://localhost:8080` after starting the service.

The UI provides an interactive form for the fields described in the [`/analyse`](#analyse) section. After submission it shows:

- **Highlighted query** — the original query text with colour-coded spans for syntax errors, naming violations, and provenance groups. Hovering a column in the results panel highlights its corresponding source spans in the query, and vice versa.
- **Output columns tab** — a table listing each output column's name, type, and the leaf table columns it was derived from.
- **Diagnostics tab** — a list of all errors and warnings with severity icons and the affected column names.

Light and dark themes are supported and persist across sessions.

## API

The service listens on port 8080 and exposes the following endpoints:

```
GET  /health
GET  /environments
POST /analyse
```

### `/health`

`GET /health` returns `200 OK` when the service is running.

### `/environments`

`GET /environments` returns the list of available environment names as a sorted JSON array.

```json
["defender-xdr", "sentinel"]
```

Use this to populate environment selectors in tooling or UIs without hard-coding environment names.

### `/analyse`

POST a JSON payload with key/value pairs as indicated below.  Optional keys should be omitted entirely if not used.
The `environment` value must match the filename (without `.json`) of a manifest in the `environments/` subdirectory. Built-in environments are `defender-xdr` and `sentinel`.  See [Environments](#environments) for more.

| Field | Required | Type | Default | Description |
|---|---|---|---|---|
| `query` | Yes | `string` | N/A | The query to be analysed. |
| `impactedEntityField` | Yes | `string` | N/A | The output column that identifies the affected entity (e.g. `DeviceId`, `AccountUpn`). Must be present in the query output and share source tables with `Timestamp` and `ReportId`. |
| `environment` | Yes | `string` | N/A | The environment in which the query should be analysed.  This defines the available tables and functions.  See [Environments](#environments). |
| `namingConvention` | No | `string` (regex) | `null` | Regex that all newly introduced output columns must match. |
| `provenance` | No | `boolean` | `true` | Set to `false` to omit the provenance tree from the response. |
| `debug` | No | `boolean` | `false` | Set to `true` to include the raw KQL syntax tree in the response. |

### Examples

The examples below use `"provenance": false` to keep responses concise.

#### Syntax error

Syntax errors short-circuit all further checks — `outputColumns` is `null`.

```json
{
  "query": "DeviceEvents | where (Timestamp > ago(1d)",
  "impactedEntityField": "DeviceId",
  "environment": "defender-xdr",
  "provenance": false
}
```

```json
{
  "messages": [
    {
      "level": "ERROR",
      "type": "Syntax",
      "message": "Expected: )",
      "affectedColumns": [],
      "start": 41,
      "end": 41
    }
  ],
  "outputColumns": null,
  "syntaxTree": null
}
```

#### Naming convention violation

The query is structurally valid, so `outputColumns` is still populated. Naming violations are warnings — the query will still run in Defender XDR.

```json
{
  "query": "DeviceEvents | extend badCol = strcat(DeviceId, '_x') | project Timestamp, ReportId, DeviceId, badCol",
  "impactedEntityField": "DeviceId",
  "environment": "defender-xdr",
  "namingConvention": "^[A-Z]",
  "provenance": false
}
```

```json
{
  "messages": [
    {
      "level": "WARN",
      "type": "ColumnNamingConvention",
      "message": "One or more columns do not match naming convention '^[A-Z]': badCol",
      "affectedColumns": ["badCol"]
    }
  ],
  "outputColumns": [
    { "name": "Timestamp", "type": "datetime", "provenance": null },
    { "name": "ReportId",  "type": "long",     "provenance": null },
    { "name": "DeviceId",  "type": "string",   "provenance": null },
    { "name": "badCol",    "type": "string",   "provenance": null }
  ],
  "syntaxTree": null
}
```

#### Missing required output column

`Timestamp`, `ReportId`, and the impacted entity field must all appear in the output.

```json
{
  "query": "DeviceEvents | project Timestamp, DeviceId",
  "impactedEntityField": "DeviceId",
  "environment": "defender-xdr",
  "provenance": false
}
```

```json
{
  "messages": [
    {
      "level": "ERROR",
      "type": "ImpactedEntityExistence",
      "message": "Missing required output columns: ReportId",
      "affectedColumns": ["ReportId"]
    }
  ],
  "outputColumns": [
    { "name": "Timestamp", "type": "datetime", "provenance": null },
    { "name": "DeviceId",  "type": "string",   "provenance": null }
  ],
  "syntaxTree": null
}
```

#### Inconsistent impacted entity provenance

All three required columns must trace back to the same source tables. Here, `Timestamp` and `DeviceId` originate from `DeviceEvents`, but `ReportId` was projected from `AlertInfo` after the join — the evidence would span two independent rows.

```json
{
  "query": "DeviceEvents | join kind=inner AlertInfo on DeviceId | project Timestamp, ReportId=ReportId1, DeviceId",
  "impactedEntityField": "DeviceId",
  "environment": "defender-xdr",
  "provenance": false
}
```

```json
{
  "messages": [
    {
      "level": "ERROR",
      "type": "ImpactedEntityConsistency",
      "message": "Impacted entity columns have inconsistent provenance: ReportId",
      "affectedColumns": ["ReportId"]
    }
  ],
  "outputColumns": [
    { "name": "Timestamp", "type": "datetime", "provenance": null },
    { "name": "ReportId",  "type": "string",   "provenance": null },
    { "name": "DeviceId",  "type": "string",   "provenance": null }
  ],
  "syntaxTree": null
}
```

### Environments

The environment dictates what tables and functions are available and are used when analysing the query. The built-in environments are loaded from a data directory in [`src/data`](./src/data) with three subdirectories:

```
data/
  environments/   # one manifest file per environment, with references to tables and functions
  tables/         # one schema file per table
  functions/      # one definition file per function
```

You can specify your own environments, tables and/or functions.  Your environment can use any combination of either the built-ins or your custom ones, and in the event of a name clash, your custom definition takes priority.  Using this, you can override a single table schema, add a new function, or define an entirely new environment without touching the built-ins

To load custom environments, set `KUSTODIAN_DATA_DIR` to a directory with this layout when starting the service, for example when using the container image:

```bash
docker run -p 8080:8080 \
  -e KUSTODIAN_DATA_DIR=/custom \
  -v /path/to/my/data:/custom \
  jsbarr/kustodian
```

**Environment manifest** (`environments/my-env.json`)

Lists the tables and functions that make up the environment by name. Each name must correspond to a file in `tables/` or `functions/`.

```json
{
  "tables": ["MyCustomTable", "DeviceEvents"],
  "functions": ["Enrich"]
}
```

**Table schema** (`tables/MyCustomTable.json`)

A flat object mapping column names to KQL scalar types.

```json
{
  "Timestamp": "datetime",
  "DeviceId": "string",
  "AccountUpn": "string",
  "Score": "real"
}
```

Supported column types: `string`, `datetime`, `int`, `long`, `real`, `bool`, `dynamic`, `guid`, `decimal`, `timespan`, `object` (dynamic bag), `array` (dynamic array).

**Function definition** (`functions/Enrich.json`)

`paramSignature` is the parameter list without outer parentheses; `body` is the KQL expression or tabular pipeline the function evaluates to. Tabular functions (used with `invoke`) take a leading `T:(*)` parameter. Scalar functions omit it. `paramSignature` may be omitted for zero-argument functions.

```json
{
  "paramSignature": "T:(*), label:string",
  "body": "T | extend Tag = label"
}
```

The built-in environment files live under `src/data/` in the repository and are generated from the source definitions in `src/environments/` using `scripts/collect-environments.py`.

## How query facts are computed

When a request arrives, `QueryFacts.Build` does the following:

**1. Parse and semantic analysis**

The query is passed to the Kusto SDK's `KustoCode.ParseAndAnalyze` together with a `GlobalState` that describes the target environment's tables and functions. This resolves every column and table reference to a typed symbol, and populates the output schema (`ResultType`).

**2. Single-pass AST index**

`BuildRefMaps` walks the syntax tree once and builds three lookup tables:
- The earliest text position of each column symbol (for error location reporting).
- The earliest text position of each table symbol.
- The `NameDeclaration` node where each column was introduced — this is the AST node containing the expression that defines the column (e.g. the right-hand side of `extend foo = bar + baz`).

**3. Provenance tracing**

For each column in the result schema, `BuildProvenanceNode` traces it back to its origins recursively:

- **Leaf case** — the column belongs to a real table in the environment. It is recorded as a leaf with the table name and query position.
- **Derived case** — the column was introduced by the query (via `extend`, `project`, `summarize`, etc.). `GetProvenanceSources` finds the upstream columns it was derived from:
  - For simple renames (`project-rename`), the Kusto SDK populates `OriginalColumns` directly.
  - For computed expressions (`extend foo = bar + baz`), the defining expression node from the AST index is walked to extract all column name references.
- **Cycle guard** — a `path` set tracks the current recursion stack as a safeguard against infinite recursion. In practice this appears unreachable through valid KQL, because the SDK's sequential extend binding always resolves name references to pre-existing symbols rather than ones being defined in the same statement.

The result for each output column is a `ProvenanceNode` tree showing the full derivation chain, and a flat `sourceMap` listing all the leaf table columns it ultimately depends on. The `sourceMap` is what the impacted entity consistency check uses to compare source table sets across `Timestamp`, `ReportId`, and the impacted entity field.

# Known Issues

**Invoke provenance limitation** — for columns introduced by `invoke`, provenance is traced into the function body by parsing it and walking `SimpleNamedExpression` nodes (explicit name assignments like `extend foo = expr`). Columns produced by operators that use implicit naming — e.g. `summarize count() by Category` producing `count_` — are not resolved further, because implicit column naming is determined by the SDK only during semantic analysis, not during parsing alone. The fix is to call `ParseAndAnalyze` on the function body using a `GlobalState` extended with the function's input table (whose schema is known from `ctx.ResultTable` at the call site). This would give full symbol binding inside the body, making the provenance trace complete for all invoke-introduced columns.