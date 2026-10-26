# S-164 Test Data Contract

## Purpose and evidence

S-164 is represented by ECDIS test scenarios built around S-101 exchange
sets. It is a test harness/data organization, not a separate chart feature
encoding. Under `S-164/TestDataSets`, scenario folders include names such as
`2.1.1 Power Up`, `2.2.2 Loading of Updates`, `2.2.3 Loading of Invalid
Sequence`, `2.2.6 Re-issue`, and `2.2.7 Cancellation`.

A typical scenario contains `S100_ROOT/CATALOG.xml` and paths under
`S100_ROOT/S-101/DATASET_FILES/`; some scenarios also include
`S-101/SUPPORT_FILES/`. For example, the Power Up catalogue references one
base `.000` cell plus several other `.000` cells and support files. The
Loading of Updates scenario references the same cell lineage as `.001` through
`.005`. The ReadMe also documents display/loading test topics and notes
historical known issues in test data.

## Scenario and package contract

Use the shared S-100 exchange-set and S-101 data contracts in
[s-101-data-contract.md](s-101-data-contract.md). S-164 adds scenario
organization and execution expectations:

```text
S164Scenario
  scenarioId: string
  scenarioPath: path
  exchangeSets: ExchangeSet[]
  s101Lineages: DatasetLineage[]
  supportFiles: ResourceReference[]
  evidenceFiles: ArtifactReference[]
  expectedBehavior: TestExpectation?  # sourced from the matching S-164 spec

DatasetLineage
  baseCell: DatasetResource?
  updates: DatasetResource[]           # ordered by catalogue/update metadata
  lifecycle: Active | Reissued | Cancelled | Invalid
```

The folder name is a human-readable scenario label, not a unique dataset
identifier. Parse `CATALOG.xml` for product, resource path, purpose, edition,
and update information. Preserve additional resource metadata and unknown
catalogue elements for forward compatibility.

## Reader and test-runner behavior

- Load every exchange set through the common catalogue reader and route each
  chart resource through the S-101 decoder selected by the declared
  specification/encoding version.
- Treat `.000` as a base-cell candidate and numbered `.001`-and-later files as
  update candidates only when catalogue metadata and S-101 lineage confirm
  that relationship.
- Preserve the order of update resources and validate gaps, duplicates,
  out-of-order updates, wrong-edition updates, corrupt files, reissues, and
  cancellations. Do not silently skip invalid files or apply updates by
  lexical filename sorting alone.
- Load support resources as catalogue-addressed resources with their own
  formats; do not parse all support files as chart cells. The observed
  examples include `.TXT` and `.TIF` resources.
- Keep scenario execution/portrayal expectations outside the core decoder.
  The scenario runner reports observed behavior against expectations in the
  corresponding S-164 test specification.

Represent results with distinct states such as `Loaded`, `Rejected`,
`InvalidSequence`, `Reissued`, and `Cancelled`, plus diagnostics and artifact
references. A rejected/corrupt/invalid-sequence case must not appear as a
successfully loaded chart.

## Versioning and scope

The inspected S-164 catalogues declare S-101 product specification 1.1.0.
The repository also contains S-101 1.2.0 standalone fixture sidecars; retain
version metadata and dispatch explicitly rather than mixing those contracts.
Scenario folder names and the repository ReadMe are useful discovery aids,
not a replacement for the matching normative S-164 specification. The exact
expected display, loading, and update assertions must be transcribed from
those specifications before they are used as automated test oracles.
