# M1-C: Chart-data pipeline

Milestone: M1-C (prerequisite M1-A)
Status: Not started. No planning for this feature has been approved yet.

This file collects the existing chart-related material moved verbatim from the
former application plan (sections 3, 5 and parts of 8 and 9) so that M1-C
planning starts from it. Under the workflow in
[development-workflow.md](../development-workflow.md), the first step is to turn
this material into a requirements-based plan and get it approved before any
tests or code. Former section numbers are kept in headings so older references
remain traceable (see the section map in the master plan).

## Existing reference contracts (former section 1)

- [S-101 chart contract](../Feature-Mappings/s-101-data-contract.md)
- [S-158 validation-test contract](../Feature-Mappings/s-158-data-contract.md)
- [S-164 scenario contract](../Feature-Mappings/s-164-data-contract.md)
- [Dataset dependency declaration](../../../../.gitmodules)

Dataset revision inspected during mapping:
`6fbc77360695ba75db9b460b604cb865caaaa8f4` of
`https://github.com/iho-ohi/S-101-Test-Datasets.git`.

These mappings are preliminary contracts, not complete normative schemas.
S-158/S-164 in this corpus wrap or exercise S-101 payloads; they are not two
additional chart decoders. Do not claim support for all S-100 products from
support for these three directories.

## Chart presentation requirements (former section 3)

- Land and coastline form a non-toggleable base layer.
- Other thematic content is stacked in toggleable overlays.
- Bathymetric depth contours have a dedicated layer and coarse-to-fine interval
  slider.
- The interval control filters existing encoded contours; it does not invent
  new contours or imply a continuous seabed model. Keep soundings and depth
  areas separately addressable.
- Safety-contour selection is independent of visual contour thinning. The
  slider must not suppress a required safety contour; normative behavior needs
  the matching portrayal/navigation specifications.

## 5. Chart ingestion and storage contracts

The ingestion and data-storage pipeline in this section belongs to Charts. Core
provides generic hosting and UI contribution points, not catalogue parsers,
ISO 8211 decoding, or S-101 feature logic. Charts registers data/import services;
CoreUI binds their commands and Display draws their data. Without Charts, those
features are unavailable rather than supplied by a built-in fallback reader.

Implement the boundaries proposed in the S-101 mapping as typed C# APIs:

1. Exchange discovery reads catalogue namespaces, product versions, dataset and
   support references, and trust/protection metadata.
2. A bounded ISO 8211 parser decodes records using the applicable encoding
   specification, preserving field/record location for diagnostics.
3. S-101 decoding resolves feature/information types, nested and repeated
   attributes, topology, geometry, coordinate scaling, CRS, and vertical datum.
4. A lineage service validates editions and update sequence, applies operations
   transactionally, and publishes a new immutable dataset snapshot only after
   successful validation.
5. A chart store exposes spatial/scale queries and durable import provenance.
   Begin with the simplest store supporting measured fixture sizes; choose a
   persistent spatial database only when measurements justify it.
6. Display, not Charts, builds scenes from chart queries and view state, not raw
   parser records. Full S-101 portrayal requires the matching portrayal resources and
   rules; a basic feature viewer must be labelled as such.

### Cross-cutting invariants

- Persist source file identity/hash, dataset ID, edition/update, product,
  encoding/catalogue versions, import time, and diagnostic provenance.
- Distinguish rejected, unsupported, cancelled, and successfully loaded data.
  Unknown or unsupported versions are never treated as an empty successful cell.
- Preserve unknown coded attributes with diagnostics; reject unsafe-to-interpret
  geometry or unsupported encodings explicitly.
- Resolve catalogue paths inside the selected exchange root. Account for the
  observed `file:/S100_ROOT/...` and `file:/S-101/...` reference conventions
  without treating them as arbitrary host filesystem paths.
- Disable external XML entity resolution and implicit schema/network downloads.
  Bound record sizes, resource counts, nesting, and memory consumption.
- Do not trust a file because it has a signature. Separate signature parsing,
  cryptographic verification, certificate trust, expiry, and navigation policy.
  Test-only acceptance of self-signed artifacts must be explicit and visibly
  non-operational.
- Imports are cancellable and asynchronous; long parsing work never blocks the
  Avalonia UI thread. Failed imports leave previously installed charts intact.
- Chart caches are derived data, invalidated by source/version changes.
- Keep offline operation possible; remote schema/catalogue retrieval is not an
  implicit requirement for loading a supported package.

### Specification gate

Before low-level parser implementation, obtain and pin the exact S-100/S-101
specifications, feature catalogues, encoding rules, and required portrayal
resources for each supported edition. Distinguish a verified supported version
from a candidate fixture version. Audit the existing mappings against binary
headers and resource metadata; folder names and autogenerated YAML do not prove
wire-format conformance. Select an initial fixture only after that audit.

## Chart-related verification (from former section 8)

- Chart tests: positive fixture assertions for exact IDs, metadata, geometry,
  nested values and reference resolution; negative tests for malformed records,
  missing resources, unsupported versions and interrupted imports.
- Updates: verify installed snapshot identity and feature changes, not only
  whether a method returned success. Exercise S-164 sequence/reissue/cancellation
  scenarios against approved expected results.
- S-158: map validation cases and report expectations individually; filenames
  containing "errors" are not sufficient pass/fail definitions.
- Separate tiny deterministic fixtures from optional full-corpus runs. Missing
  submodule data must be an explicit setup error in data-integration runs, not
  a passing test with zero assertions.
- Record fixture revision and selected specification versions with results.
- Measure load time, memory, query latency and pan/zoom frame time on named
  fixture/hardware combinations; establish numeric budgets from the first
  working slice instead of inventing unsupported performance claims.

## Related decisions (from former section 9)

| Decision | Provisional position | Resolve before |
|---|---|---|
| Product/spec versions | Select from audited available fixtures; no blanket S-100 support | M1-C |
| Trust/licensing | Verify dataset/resource usage rights and trust requirements | Redistribution or operational use |
| Portrayal/certification | Development viewer, not certified ECDIS or operational navigation | Any operational claim |

## Ledger

None yet.

## Status and follow-ups

Planning has not started. The milestone deliverable from the master plan: audit
specifications and resources, pin fixture versions, Charts decodes a real base
cell, and metadata, attributes, topology and malformed-input diagnostics are
verified.
