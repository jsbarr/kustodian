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

### Build and run

```bash
docker build -t kustodian .
docker run -p 8080:8080 kustodian
```

The service listens on port 8080 and exposes a single endpoint:

```
POST /analyse
```

### Request format

POST a JSON payload with key/value pairs as indicated below.  Optional keys should be omitted entirely if not used.
The `environment` value must match the filename (without `.json`) of a schema file in the environments directory. The built-in environment is `defender-xdr`.

| Field | Required | Type | Default | Description |
|---|---|---|---|---|
| `query` | Yes | `string` | N/A | The query to be analysed. |
| `environment` | Yes | `string` | N/A | The environment in which the query should be analysed.  This defines the available tables and functions.  See [Using a custom environment](#using-a-custom-environment). |
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

### Using a custom environment

Environment schemas are loaded from `/app/environments/` inside the container. Mount your own directory to override or extend the built-in schemas:

```bash
docker run -p 8080:8080 \
  -v /path/to/my/environments:/app/environments \
  kustodian
```

Each file in the directory is named `<environment-name>.json` and describes the tables and functions available in that environment. Example:

```json
{
  "tables": {
    "MyCustomTable": {
      "Timestamp": "datetime",
      "DeviceId": "string",
      "AccountUpn": "string",
      "Score": "real"
    }
  },
  "functions": {
    "Enrich": {
      "paramSignature": "T:(*), label:string",
      "body": "T | extend Tag = label"
    }
  }
}
```

Functions are declared as a dictionary keyed by function name. `paramSignature` is the parameter list (without outer parentheses); `body` is the KQL expression or tabular pipeline that the function evaluates to. Tabular functions (used with `invoke`) take a leading `T:(*)` parameter. Scalar functions omit it. `paramSignature` may be omitted entirely for zero-argument functions.

Supported KQL column types include `string`, `datetime`, `int`, `long`, `real`, `bool`, `boolean`, `dynamic`, `guid`, and others accepted by the Kusto SDK's `ScalarTypes`.

To use both the built-in `defender-xdr` schema and your own tables, copy `src/environments/defender-xdr.json` into your mounted directory alongside your custom files.

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
