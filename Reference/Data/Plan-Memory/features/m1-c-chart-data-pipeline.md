# M1-C: Chart-data pipeline

Milestone: M1-C (prerequisite M1-A, done)
Status: Back at the Step 2 gate — Step 3 paused 2026-11-01 because three
approved tests carry a wrong assumption about the YAML oracle (see "Step 3
progress and the test defect" below); plan, tests, C17 wording and CI
change were approved earlier the same day

Former section numbers from the single application plan (1, 3, 5, 8, 9) are
still referenced below where they map onto this plan, per the section map in
the master plan.

## Problem and scope

Charts must read real S-101 chart exchange sets and expose decoded chart
content — features, attributes, geometry, update lineage — to Display,
without Core implementing an alternative reader and without Charts drawing
anything itself (confirmed ownership, master plan). Today `CoreCharts` is an
empty stub: `ChartsModule` declares two capabilities
(`charts.display`, `charts.route-planning`) and nothing else. This plan covers
building the actual reader and chart store.

**In scope:** exchange-set/catalogue parsing, a bounded ISO/IEC 8211 record
reader, S-101 feature/attribute/geometry decoding, base-cell and update
lineage (valid sequence, invalid sequence, reissue, cancellation, corrupt
input), an immutable chart store with import provenance, and the typed
reader boundary (`IExchangeSetReader`, `IS101DatasetDecoder`,
`IS101UpdateApplier`) Display will later consume in M1-D.

**Out of scope (non-goals) for M1-C:**

- Drawing, portrayal, symbology, and scene-building from chart queries — M1-D
  (Display), per the existing ownership split.
- Full S-100 product-family support. Only S-101, as evidenced by the three
  mapped corpora (S-101, S-158, S-164); no claim is made about other S-100
  products (former section 5).
- A distributed or server-based spatial database (e.g. a dedicated GIS
  server), or spatial-index tuning beyond a catalogue-adjacency cache. An
  embedded, file-based database with a view-driven cache is in scope (Q3,
  confirmed below); scaling that further is deferred until measurements
  justify it.
- Certificate trust: chaining a producer certificate to a scheme
  administrator, revocation, permits and decryption (S-100 Part 15). The
  fixtures are signed by S-164 test authorities whose administrator
  certificate is not in the repo; M1-C makes no verified-trust or
  operational-use claim. That is the master plan's separate
  "Trust/licensing" decision. Whether checking each dataset's signature
  against the certificate in its own catalogue (integrity only, no trust)
  is in scope is open question Q7.
- CRS/projection generality beyond what the audited fixtures declare.
- Reconciling the stale `charts.route-planning` capability to Navigation —
  already tracked as its own open item in the master plan's architecture
  section; not duplicated here.
- Performance tuning beyond a first recorded baseline (former section 8).

## Design

### Layering (former section 5)

Two layers, matching the existing S-101 contract
([s-101-data-contract.md](../Feature-Mappings/s-101-data-contract.md)):

```text
Exchange set (CATALOG.xml)  ->  ExchangeSet (catalogue metadata + resolved resource refs)
S-101 dataset (.000/.001+)  ->  S101Dataset (features, information objects, spatial records)
                             ->  DatasetLineage (base + ordered updates, lifecycle)
                             ->  Chart store (immutable snapshots, provenance, spatial/scale queries)
```

Byte-level ISO 8211 parsing stays separate from catalogue-aware S-101
decoding and from lineage application, matching the three-seam reader
boundary already proposed in the feature-mapping document:

```text
IExchangeSetReader.Read(exchangeSetRoot) -> ExchangeSet
IS101DatasetDecoder.Decode(datasetStream, encodingVersion, featureCatalogue) -> S101Dataset
IS101UpdateApplier.Apply(baseDataset, orderedUpdates) -> UpdatedS101Dataset
```

All decoding, lineage and store types (ISO 8211 reader, exchange-set
reader, S-101 decoder, update applier, chart store, cache) live inside
`CoreCharts`, per the master plan's instruction to keep S-101 internals
inside Charts rather than create speculative shared libraries.

**Host-facing contract (amended 2026-11-01 during Step 2).** The module
runtime only accepts a feature that implements an interface from the
shared `Contracts` assembly ("Feature requires a host-shared interface"),
so C27 needs one. The plan adds the smallest one, in
`System/Shared/Contracts/Charts.cs`: `ChartPoints` (point `charts.library`,
feature `core.charts.library`, declared in Charts' `module.json`) and an
`IChartLibrary` interface with an asynchronous import of an exchange-set
root (returning an explicit outcome: loaded, rejected, unsupported version
or cancelled, with diagnostics) and a neighbourhood query by reference
cell (returning the snapshot generation and the cell identities and
provenance it covers). Its parameter and result types are small
host-shared records in the same file. Decoded feature content is not in
the shared contract: tests read it from the `CoreCharts` types directly,
and the Display-facing content contract is still decided when M1-D starts,
as before. This follows the master plan's existing rule that boundary
types are published through a small host-shared contract assembly.

### Specification and resource audit (2026-11-01)

The user added the official IHO document set at
`Reference/Data/S-100 Documentation/` and 13 further `iho-ohi` submodules.
All of it was audited against this plan, and every S-101 file in every
corpus (about 600) was parsed with a throwaway ISO 8211 walker to check the
documents against real bytes. Result: **the normative material needed to
build M1-C is now all in hand**; what is missing is fixtures for three
lifecycle cases at the current edition, plus the XML schemas.

| Need | Source now in hand | Status |
|---|---|---|
| ISO 8211 container, S-100 record/field model | S-100 Ed 5.2.1 (Dec 2025) Part 10a, in `S-100 Documentation/S-100 Ed 5.2.1_FINAL.pdf`; supersedes the 5.1.0 Part 10a saved in `Specifications/` (5.2.1 changed only the Delete-instruction notes for `COCC`/`SECC`/`CCOC`) | Complete |
| S-101 encoding profile (allowed records/fields, fixed values, update and cancellation file structures) | S-101 ENC Product Specification Ed 2.0.0 (Dec 2024), Annex B (normative): B-5 base, B-6 update (`COCC`/`SECC`/`CCOC` update-control fields), B-7 cancellation | Complete |
| Lifecycle rules (former gap b) | S-101 PS §11.3.1-11.3.3 (file naming `101CCCC….EEE`, edition/update/issue-date sequencing, re-issue keeps the last update number, cancellation is an update file with edition 0); S-100 Part 17 §17-4.4.1; expected outcomes in S-164 Ed 1.0.0 §2.2-2.3 | Complete |
| Feature Catalogue (former gap a) | S-101 FC 2.0.0 (190 feature types, 236 simple + 42 complex attributes), plus FC 1.1.0, 1.5.0, 1.9.0 and test-only 2.5-2.8 in `S-164-Sub-Group/data/Catalogues` and its exchange sets. No FC 1.2.0 exists, but datasets carry their own code tables (`ATCS`/`ITCS`/`FTCS`/`IACS`/`FACS`/`ARCS` map numeric codes to catalogue names), so decoding never needs the FC; it is needed only to type attribute values and resolve meanings | Complete |
| Exchange catalogue model | S-101 PS §12 and S-100 Part 17 §17-4.5 (dataset/support-file/catalogue discovery metadata) | Complete as text |
| CRS and coordinates | S-101 PS §5 (EPSG:4326 horizontal, metre depth vertical CRSs) and §10.1 (`CMFX`/`CMFY` = 10^7, `CMFZ` = 10) | Complete |
| Data integrity (signatures) | S-100 Part 15 §15-8; test certificates in `S-164-Sub-Group/data/Staging/keys` | Text complete; see Q7 |
| XML schemas (`S100_ExchangeCatalogue.xsd`, `S100FC.xsd`, `Part15.xsd`, schematron) | `Reference/Data/distribution_20240521/`, the S-100 schema server's 2024-05-21 archive for 5.0.0/5.1.0/5.2.0, added by the user (Q8). Covers all three exchange-catalogue namespaces the data uses and mirrors the ISO/OGC schemas they import, so validation runs offline. No sample catalogues. Provenance in [Specifications/](../Specifications/README.md) | Complete |
| Independent decode oracle at S-101 2.0.0 | None. The 2.0.0 cells ship only `.docx` notes, and the dataset README warns of "known errors" in their `PRED`/`PRSP` values and some associations | Gap; handled in Design below |

Other submodules checked and found not needed for M1-C: `S-101_Portrayal-Catalogue`
(Lua/SVG portrayal, for M1-D), `S-100-Validation-Checks` and
`S-101-Validation` (S-158 validation-check spreadsheets, a possible later
validation feature), `S-100UML` (Enterprise Architect models only, no
schemas), `S-100-xmlbindings` (S-125/S-201 only), `NIWCSoftwareTestbed`
(manuals for the ShoreECDIS/S100Viewer tools; binaries are release-only),
`S100Infrastructure`, `S100Resources`, `S-100WG`, `ENCWG`,
`S100-Security-Scheme-PT` (working-group material), `S-100-Standard-Development`
(Metanorma source of the same S-100 text).

### Findings from the real bytes that change the design

1. **Self-describing records, not hard-coded layouts.** The corpus spans S-101
   1.0 to 2.0.0 on Part 10a editions 1.1, 5.1 and 5.2. The binary layouts
   differ: 2.0.0 nests repeating groups in its format controls
   (`(b11,b14,2b12,b11,(3b12,b11,A))`) while 1.x writes them flat and relies
   on the `*` in the label list; 2.0.0 marks text fields UTF-8 (`%/G`) in
   the field controls while 1.x leaves them blank; `THAS` exists in 1.x and
   is gone in 2.0.0. The reader must therefore interpret each file's own
   Data Descriptive Record (field controls, label list, format controls),
   which ISO 8211 is designed for. C6 now says so.
2. **Edition and update numbers come from `DSID.DSED`, whose format varies.**
   2.0.0 base cells carry a bare edition (`"3"`); update files carry
   `edition.update` (`"3.2"`); the 1.1.0 corpus also writes base cells as
   `"2.0"` and its re-issue as `"1.3"`; cancellation is `"0"`. The product
   version strings vary too (`PRED` `"2.0"` vs `"2.0.0"`), which the dataset
   README itself lists as a known error.
3. **The exchange catalogue is not reliable enough to drive lineage.** Every
   2.0.0 update in the released S-164 sets is labelled `purpose =
   newDataset` (the spec says `update`); the 1.1.0 cancellation catalogue
   says `newDataset` with edition 0 and update 0; one 1.1.0 catalogue's
   `certificateRef` points at a certificate id that does not exist. S-100
   5.2.1 also changed cancellation so the catalogue's `editionNumber` holds
   the cancelled cell's edition (it is now a positive integer), while S-101
   Annex B-7 still puts `DSED = "0"` in the file. Decision: the dataset
   file's own `DSID` is authoritative for identity, edition, update and
   cancellation; disagreement with the catalogue is a warning diagnostic,
   not a rejection (otherwise the released valid update sequence would be
   rejected). New requirement C35. The schemas confirm it: validated
   offline, 80 of 118 catalogues fail strict XSD validation, including
   every catalogue in the plan's fixtures (the 5.2 sets put
   `encodingFormat` where the schema expects `producingAgency`; the 5.0
   sets omit the required `gml:id` on coverage polygons and put bare text
   in `cit:name`). The S-101 Feature Catalogue 2.0.0 validates cleanly. So
   the reader parses catalogues tolerantly, by namespace and element name,
   and never rejects one for schema invalidity alone.
4. **Catalogues use arbitrary namespace prefixes** (`ns15:`, `ns16:`) and
   three exchange-catalogue namespace versions (`/s100/xc/5.0`, `5.1`,
   `5.2`); the reader must match namespace URIs, never prefixes.
5. **Adjacency is not in the catalogue.** Each dataset entry has a
   bounding box, one or more coverage polygons (`posList` in latitude,
   longitude order per EPSG:4326) and a scale band, but nothing marks two
   cells as neighbours. C32 now computes adjacency from coverage geometry.
6. **`DSSI` record counts are unreliable.** About half the files declare
   counts that do not match their records (all 32 `S-101_1.2` cells
   included, several with the same bogus `0/1/0/1/0/0/2`); only the released
   S-164 2.0.0 sets are fully consistent. Counts are a warning, never a
   bound or an oracle. 81 files also use `CMFZ = 100` although S-101 2.0.0
   mandates `10`, confirming C13's rule of using each dataset's own factors.
7. **The "corrupt" fixtures the old plan cited are not corrupt.** In
   `S-101-Test-Datasets/S-164`, `2.2.1 Corrupt Update` holds files
   byte-identical to the valid updates; only the catalogue's signature
   values differ, and every one of them verifies (ECDSA signatures are
   randomised, so re-signing yields different valid bytes). `2.1.5 Loading
   Corrupt Data` is structurally valid and its signature verifies too. The
   only genuinely corrupt fixture anywhere is
   `S-164-Sub-Group/.../CorruptData/.../10100AA_X02SE.000`, whose last record
   claims 310 bytes with 295 left. C18 and C21 are re-pointed accordingly.
8. **Signatures are verifiable offline.** A released 2.0.0 update's
   `S100_SE_SignatureOnData` verifies with ECDSA/SHA-384 against the
   producer certificate embedded in its `CATALOG.XML` (the 1.1.0 corpus uses
   SHA-256). The producer certificate does not chain to any scheme
   administrator certificate in the repo, so trust-chain checks cannot be
   tested here; integrity checks can (Q7).

The binary-encoding findings recorded before this audit still hold and
are kept below for reference.

### Binary encoding (Part 10a), as first recorded 2026-11-01

The first pass fetched **S-100 Edition 5.1.0, Part 10a — ISO/IEC 8211
Encoding** (October 2023) on its own, saved at
[Reference/Data/Specifications/](../Specifications/README.md). Its findings
(still valid under 5.2.1):

- The full Logical Record byte layout: a fixed 24-byte Leader (record
  length, interchange level, leader id, field-control length, base address
  of the Field Area, and an entry map giving the actual byte widths of the
  tag/length/position fields used by the Directory that follows), a
  Directory of (tag, length, position) entries terminated by the field
  terminator, and a Field Area holding either Data Descriptive fields (in
  the one Data Descriptive Record per file) or the real data fields (in
  every Data Record).
- The actual S-100 common and object-record field tags an S-101 dataset
  uses: `DSID`/`DSSI` (+ `ATCS`/`ITCS`/`FTCS`/`IACS`/`FACS`/`ARCS` numeric
  code-lookup tables) for the Data Set General Information record;
  `CSID`/`CRSH`/`CSAX`/`PROJ`/`GDAT`/`VDAT` for the Data Set Coordinate
  Reference System record; `IRID` for Information Type records;
  `PRID`/`MRID`/`CRID`/`CCID`/`SRID` plus the coordinate fields (`C2IT`/
  `C3IT`/`C2FT`/`C3FT`/`C2IL`/`C3IL`/`C2FL`/`C3FL`) for Point/Multi Point/
  Curve/Composite Curve/Surface records; and `FRID`/`FOID`/`ATTR`/`INAS`/
  `SPAS`/`FASC`/`THAS`/`MASK` for Feature Type records.
- The exact mechanism behind this plan's C11 ("nested/repeating attribute
  groups keep their order and grouping"): the `ATTR` field's `NATC`
  (attribute code) / `ATIX` (index among siblings of the same code) / `PAIX`
  (1-based pointer to the parent's position within the same field, 0 for
  top-level) subfields, filled in pre-order tree traversal — this is a
  verified mechanism, not an assumption.
- The per-record `RUIN` (Insert/Delete/Modify) update instruction and the
  23-step record-order rule (§10a-4.5: General Information, then CRS, then
  Information/Point/Multi Point/Curve/Composite Curve/Surface/Feature
  records in Insert order, then the same in Modify order, then Delete in
  reverse order) that an update file's records are applied under.

What Part 10a alone did not define — Feature Catalogue code meanings and
exchange-set lifecycle rules — is now covered by the S-101 2.0.0 Feature
Catalogue and Product Specification (see the audit table above).

### Fixtures (revised 2026-11-01)

The old fixture set was all S-101 1.1.0 (Part 10a edition 1.1), a
pre-operational edition, and two of its "corrupt" cases are not corrupt
(finding 7). The current edition, S-101 2.0.0, has a coherent released
lineage in the S-164 sub-group's Part 2 exchange sets. Those ship as zips
(`S-164-Sub-Group/data/ExchangeSets/S-164/Part2/**.zip`); tests extract
them into a temporary directory and never modify the submodule. Revisions:
`S-101-Test-Datasets` `6fbc77360695ba75db9b460b604cb865caaaa8f4`,
`S-164-Sub-Group` `421bb6b0921c0c937df83c7455edd71ff24a9b80`.

| Fixture | Edition | Role | Notes |
|---|---|---|---|
| `S-164 Part2/2.2_DatasetLoading/PowerUp` | 2.0.0 | Base load | 6 cells (`X0000` ed 7, `X01NE` ed 5, `X01NW` ed 3, `X01SE` ed 3, `X01SW` ed 3, `X02SE` ed 4), coverage polygons for adjacency (C32) |
| `S-164 Part2/2.3_AutomaticUpdates/SequentialUpdate1-5` | 2.0.0 | Valid lineage (C16) | `X01SW` updates `3.1`-`3.5` on the edition 3 base |
| `.../InvalidSequence001-003` | 2.0.0 | Invalid sequence (C17) | `3.1`, `3.2`, then `3.4` (gap): S-164 §2.3.3 expects the gap rejected |
| `.../GoodBaseCells` + `.../NewUpdate` | 2.0.0 | New edition (C17, C19) | Edition 4 base, then update `4.1`; `NewUpdate` before `GoodBaseCells` must be refused (S-164 §2.3.4) |
| `.../OldUpdate` | 2.0.0 | Older edition (C17) | `3.1` after edition 4 is installed must be refused, naming the expected edition (S-164 §2.3.5) |
| `S-164-Sub-Group/.../S-100/CorruptData` | 1.1.0 | Corrupt base cell (C21) | `10100AA_X02SE.000`: last record truncated (310 claimed, 295 present). The only genuinely corrupt file in any corpus |
| `S-101-Test-Datasets/S-164/TestDataSets/2.2.6 Re-issue` | 1.1.0 | Re-issue (C19) | Base-profile file, `DSED "1.3"`, catalogue `purpose = reissue`, update 3. No 2.0.0 re-issue exists |
| `S-101-Test-Datasets/S-164/TestDataSets/2.2.7 Cancellation` | 1.1.0 | Cancellation (C20) | Annex B-7 shape: one `DSID` record, `DSED "0"`; its catalogue is non-conformant (finding 3). No 2.0.0 cancellation exists |
| `S-101-Test-Datasets/S-101_1.2/000/101AA00DS0001.000` + YAML sidecar | 1.2.0 | Field-level decode oracle (C15) | The only cell with a machine-readable oracle; its `DSSI` counts are wrong (finding 6) |
| `S-101-Test-Datasets/S-158/V1/TestDataSets/00AA_00010` | 1.1.0 | Older-edition decode (C9, C34) | Exercises the flat (1.x) format controls |
| `S-101/S-101/S-101_FR_June26/…/S101FR00369400/` (SHOM; local only, git-ignored) | 2.0 | Real lineage across a new edition (C16, C17, C19, C36) | Edition 1 + `1.1` + `1.2`, then new edition 2, then `2.1`. Applying an edition 1 update after edition 2 is installed reproduces the older-edition refusal with real data |
| `S-101/S-101/S-101_GB_Apr26/S100_ROOT` (UKHO; local only, git-ignored) | 2.0 | Real exchange set (C1, C2, C31-C33, C36), second baseline (C30) | 27 cells over real GB coverage for adjacency, one update, 127 support files, `S100_SE_DigitalSignature` element; also one S-128 file (not S-101) |
| `s100tds/upd/2.1.0/cases/<ID>` (submodule `Reference/Data/s100tds`, `4c2cade`) | 2.1.0 label, 2.0.0-valid content | Update mechanics (C16, C19) | About 80 base+update pairs, one per Annex B-6 update class; read through a test-only override that applies the 2.0.0 rules to the 2.1.0 label (Q9). Oracle: each case's description in `docs/update-permutation-catalogue.md`. The 2.10.0 cases use a test-only FC and are not used |
| Derived at test time from `SequentialUpdate1` and SHOM's `101FR00369400.001` | 2.0 | Corrupt update (C18, C36) | No corrupt update exists in any corpus; the test copies a `.001` and truncates it (structural) or alters one byte (signature) |

**Search for more S-101 2.0.0 data (2026-11-01).** Asked for by the user
after Q6/Q7. Findings:

- `S-164-Sub-Group/data/Staging/S-101/v2.0.0` holds the same `X01SW`
  lineage as the released sets but with all five invalid-sequence files
  (`.001`-`.005`), plus 46 more 2.0.0 base cells (Chart 1, polar, overlap,
  hazards, safety contour, settings and others) usable as decode-only
  fixtures. Marked "not authoritative" by its own README.
- The sub-group's GitHub release `v2.0.0` (2026-10-16, "for S-164 Edition
  2.0.0 type approval testing") contains the same 39 exchange sets as the
  repo's zips; its notes say more sets will follow. The in-repo manual
  source still describes the re-issue (`.001`, then a re-issue `.000`
  carrying updates 2-3, then `.004`), cancellation and corrupt-update tests
  in terms of the old S-57 cells: they have not been rebuilt at 2.0.0.
- The IHO GI Registry's S-101 entry lists no test dataset for 2.0.0; every
  file it offers is already here.
- UKHO publishes free S-101 trial data through the ADMIRALTY Marine Data
  Portal, behind its own licence terms; its edition is not stated
  publicly. Canadian S-101 data used in the 2026 sea trials is sold
  through PRIMAR.
- **`github.com/kusala9/s100tds`** (created 2026-10-26, unofficial, no
  licence): 444 dataset files, a base cell plus about 80 update classes
  covering every update mechanism in S-101 Annex B-6 and Part 10a —
  insert/modify/delete of every record type, positional `COCC`/`SECC`/`CCOC`
  edits, nested attribute edits via `PAIX`, association and mask edits,
  record ordering, and a new edition. Every file parses cleanly. Built
  against S-101 **2.1.0** (at IHO call for approval; its FC is a draft)
  and a test-only 2.10.0, but every code the 2.1.0 build uses exists in FC
  2.0.0; only the `DSID` version label differs. No catalogues, signatures
  or expected-result files: each case's oracle is its written description
  in `docs/update-permutation-catalogue.md`. Re-issue and cancellation are
  described but have no files. See Q9.

Still nowhere: an S-101 2.0.0 re-issue, cancellation or corrupt update. C19
and C20 stay on the 1.1.0 files and C18 on a derived file.

**Real hydrographic-office data (added by the user, 2026-11-01).**
`Reference/Data/S-101/S-101/` holds two zips, extracted in place:
`S-101_GB_Apr26` (UKHO, one signed exchange set dated 2026-04-16: 27 base
cells, one update `101GB005DEVRC.001`, 127 support files, and one S-128
file; corrected from 29 cells at the Step 3 gate) and
`S-101_FR_June26` (SHOM, 2026-06-19/22: four signed exchange sets, plus a
spreadsheet). Downloaded from UKHO; licensed data that must never be
committed. `Reference/Data/S-101`, `S-102`, `S-104`, `S-111` and `S-124`
(all UKHO downloads) are excluded in the root `.gitignore`.

**Local-only data rule.** Tests that read `Reference/Data/S-101` skip, with
a reason naming the missing folder, when it is absent (as it is in CI and
on any other machine). Every requirement they touch is also covered by
committed data (the S-164 sets, `s100tds`, the 1.1.0 files), so CI still
verifies all of C1-C37; the UKHO/SHOM tests add real-producer coverage on
machines that have the data. All 35 dataset files are S-101 2.0 on Part 10a 5.2, parse
cleanly, declare `DSSI` counts that match, and use the mandated
`CMFX`/`CMFY` 10^7 and `CMFZ` 10 — the cleanest data in the project. All
36 signatures verify (ECDSA/SHA-384) against certificates issued by the
IHO S-100 5.2.0 root scheme administrator. Differences from the S-164 data
that the requirements must allow for:

- Base cells write `DSID.DSED` as `"1.0"` / `"2.0"`, not the bare `"3"` of
  the S-164 sets.
- UKHO's catalogue uses `S100_SE_DigitalSignature` for the dataset
  signature element; everyone else uses `S100_SE_SignatureOnData`.
- SHOM puts each cell in its own folder (`DATASET_FILES/101FR00369400/`).
- SHOM's catalogues label purposes correctly (`newDataset`, `update`,
  `newEdition`), unlike the S-164 sets. UKHO's has the same
  `encodingFormat`-before-`producingAgency` order the S-164 sets use; with
  two producers agreeing, the 2024 schema build is more likely the outlier.
  SHOM's are schema-valid apart from .NET notices about an undeclared
  `xlink` prefix.

SHOM's `101FR00369400` is a real cross-edition lineage: edition 1 base,
updates `1.1` and `1.2` (one exchange set), then a new-edition 2 base, then
update `2.1` (separate exchange sets). `101FR00571300` is an edition 2 base
of 17,055 records with update `2.1`.

**Further exploration of the UKHO download (2026-11-01).**

- *Support files.* UKHO's S-101 set carries 127 support files (108 `TXT`,
  19 `TIF`), all `revisionStatus = new`, all signed, all present, none with
  `supportedResource` populated. All 27 of its cells use the `fileReference`
  attribute and 5 use `pictorialRepresentation` to point at them. No
  requirement yet covers importing support files or resolving those
  references (Q10, answered by C37). The committed released 2.0.0
  `InitialPowerUp` set also has 5 support files; its `X0000` cell refers to
  them from information types, so C37 is verifiable in CI. (Corrected at the
  Step 3 gate: one of the cell's 5 references, `10100AATESTPC.TI`, is
  truncated in the cell's bytes, and one UKHO reference, `101GB00A0193.TXT`,
  is a producer typo for `101GB001A0193.TXT`; neither resolves.)
- *SHOM's spreadsheet* (`S101_dataShom_AcrossTheChannel_June2026.xlsx`)
  lists, per cell, edition/update history, bounds, file size and a feature
  count, and per update the features each change touched (by FOID or
  position: buoys, lights, wind turbines and an offshore-production area for
  `101FR00369400`). The change notes are an independent oracle for C16/C19
  on the SHOM data. The feature counts are not exact: `101FR00571300`
  edition 2 holds 5,056 feature records against the sheet's 5,027, and no
  obvious subset (the meta features total 36) explains it. The sheet also
  lists three cells not in the download.
- *Mixed-product exchange sets.* UKHO's S-104 and S-111 exchange sets each
  carry an S-128 catalogue-of-products dataset (GML) beside the gridded
  data, so a real catalogue mixes products. C1 now says other products are
  listed and reported unsupported without failing the exchange set.
- *Placeholder signatures.* SHOM's S-104/S-111 sets have no `CATALOG.SIGN`
  and every signature value is the placeholder text
  `============A_MODIFIER_AVEC_SIGNATURE============` ("to be replaced with
  a signature"). Not S-101, but C36 now states the rule: a signature value
  that cannot be decoded as a signature counts as unsigned (warning), while
  one that decodes but fails verification is corrupt.
- *Signature element.* Every set produced by the UKHO toolchain uses
  `S100_SE_DigitalSignature`, including SHOM's S-102; SHOM's own S-101 sets
  use `S100_SE_SignatureOnData`. The element follows the toolchain, not the
  producer.

The non-S-101 products (S-102, S-104, S-111, S-124, S-128) are out of
M1-C's scope; their findings are recorded for the later overlay work in the
master plan.

S-164 Ed 1.0.0 (March 2023) is still marked `[TBD]` in places and its
edition numbers (1 and 2) predate the released 2.0.0 sets (3 and 4); its
expected outcomes are used, its numbers are not.

**Decode oracle at 2.0.0.** No machine-readable oracle exists, so C15 keeps
the 1.2.0 YAML sidecar for field-level comparison, and 2.0.0 decoding is
checked against what the specification and catalogue fix independently:
every record resolves, every code resolves through the dataset's own code
tables to a name present in FC 2.0.0, the catalogue's edition/update agree
with `DSID`, and the decoded Data Coverage features fall inside the
catalogue's bounding box (S-101 §4.5.3).

### ISO 8211 approach (confirmed 2026-11-01, Q1)

Implement a bounded reader in `CoreCharts` against the verified Part 10a
structure above (leader, directory, field area; explicit limits on record
size, field count, and nesting) rather than bind a third-party/native
library (e.g. GDAL's ISO8211 driver). This keeps the security invariants
(bound sizes, no native dependency, no implicit network access) directly
testable and avoids an immature or partial S-101 code path in a general GIS
library.

### Chart store (confirmed 2026-11-01)

An embedded, file-based store (originally assumed SQLite; replaced at the
Step 3 gate, 2026-11-01, by a managed store of content-addressed files and
an atomically replaced manifest, because the host refuses native libraries
in modules; no server process, works offline) holds
durable, immutable per-import snapshots. Charts has no concept of "what is
currently displayed" itself (that belongs to Display/M1-D); the caching
policy is driven by a reference cell (or cells) the caller's query names, not
by Charts reaching into Display. On top of the database sits an in-memory
cache that is kept warm for the reference cell(s) a query names plus every
cell the exchange-set catalogue metadata marks as geographically adjacent to
them; cells outside that neighborhood are eligible for eviction under a
configured limit, and an evicted cell is transparently reloaded from the
database on the next query that needs it. This is testable at the Charts
layer alone: tests supply the reference cell(s) directly, simulating what
Display will later pass.

### Logging and host integration

Charts uses `IModuleHost.GetLog("Core.Charts")` (M1-I's facility) for import
diagnostics, consistent with the rest of Core. Charts registers its
data/import services as contributions; without Charts, those features are
reported unavailable through the host, never silently supplied by a
built-in fallback reader (confirmed architecture decision, master plan).

## Requirements

### Slice 1 — Exchange set and ISO 8211 container

| ID | Requirement |
|---|---|
| C1 | Reading a valid S-100 exchange-set root returns an `ExchangeSet` with catalogue metadata (product, edition/update, purpose, bounding box, coverage polygons, display scales) and a resolved resource reference, tagged with its declared role, for every entry in `CATALOG.XML` (file name matched case-insensitively). Elements are matched by namespace URI, never prefix, for exchange-catalogue namespaces `/s100/xc/5.0`, `5.1` and `5.2`. Entries for products other than S-101 (for example the S-128 dataset in UKHO's S-104/S-111 sets) are listed with their product and reported unsupported; they never fail the exchange set |
| C2 | A resource path is resolved only within the exchange-set root, honoring the observed `file:/S100_ROOT/...` and `file:/S-101/...` conventions; a reference that would resolve outside the root is rejected with a diagnostic, not silently followed or truncated |
| C3 | A missing, malformed, or incomplete `CATALOG.xml` returns a structured rejection naming the file and the missing/invalid element, never an empty successful `ExchangeSet` |
| C4 | External XML entity resolution and implicit remote schema/catalogue retrieval are disabled; a catalogue referencing either fails closed |
| C5 | Importing a supported package succeeds with no network access available |
| C6 | A bounded ISO/IEC 8211 reader decodes a valid DDR+DR leader/directory/field-area stream into an ordered sequence of typed records (field tag, subfields, raw bytes), independent of any S-101 feature semantics. Subfield layout comes from the file's own DDR (field controls, label list, format controls), so both nested (2.0.0) and flat (1.x) repeating-group format controls, and both UTF-8-marked and unmarked text fields, decode correctly |
| C7 | The reader enforces explicit bounds on record size, field count, and subfield nesting; exceeding a bound is rejected with a diagnostic naming the limit and the position, never a stack overflow, unbounded allocation, or silent truncation |
| C8 | A malformed ISO 8211 stream (bad leader length, inconsistent directory, truncated field data) is rejected with a diagnostic including byte/record position |
| C9 | Decoding the same exchange set and dataset file twice produces structurally equal output |

### Slice 2 — S-101 dataset decoding

| ID | Requirement |
|---|---|
| C10 | A decoded `S101Dataset` exposes `DatasetIdentity`, encoding/feature-catalogue `ProductVersion`, declared coordinate reference systems, and the feature/information/spatial record collections, keeping those three kinds distinct rather than flattened |
| C11 | Each decoded `Feature` preserves its FOID, feature-type code, primitive kind, attributes (repeating/nested attribute groups keep their order and grouping, not flattened to single values), geometry references, and associations (target, association name, role) |
| C12 | Each decoded `SpatialRecord` preserves its identity and geometry (point, multipoint, curve, composite curve, surface including exterior/interior ring references), resolved only after every dataset record is read, so forward references and shared spatial records are supported |
| C13 | Coordinates are decoded using the dataset's own coordinate-reference and spatial-parameter metadata, not a fixed convention; depth/vertical values keep a distinct unit and datum from horizontal coordinates |
| C14 | An unresolved feature-type code or geometry reference is preserved on the decoded record and reported as a diagnostic, never silently dropped |
| C15 | Decoding the audited `S-101_1.2` base cell and comparing against its YAML sidecar (oracle, not authority) matches on feature IDs, attribute values, geometry primitive kind, and association roles |

### Slice 3 — Update lineage

| ID | Requirement |
|---|---|
| C16 | A base `.000` dataset and its ordered `.001`+ update files are modeled as a `DatasetLineage`; applying `SequentialUpdate1`-`5` (2.0.0) to the `X01SW` edition 3 base, one exchange set at a time, produces a current dataset at edition 3 update 5, and applying SHOM's `101FR00369400` `.001`-`.002` to its edition 1 base produces edition 1 update 2, with edition and update read from `DSID.DSED` (`N` or `N.0` for a base, `N.M` for an update) and each update's records applied in the Part 10a §10a-4.5 order |
| C17 | An update whose number is not exactly one more than the current one (`InvalidSequence003`, a gap after `3.2`), whose edition is newer than the installed base (`NewUpdate` before `GoodBaseCells`), or whose edition is older (`OldUpdate` after edition 4) is refused with a diagnostic stating the expected and actual edition/update, and the lineage stays at its last valid state; a gap or a newer-edition update also flags it not up to date until a complete sequence, re-issue or new edition clears it (S-164 §2.3.3-2.3.5). (Wording amended and approved at the Step 2 gate, 2026-11-01: an older-edition update no longer flags a complete chart not up to date.) |
| C18 | A corrupt update (derived at test time from `SequentialUpdate1`'s `.001`, since no corrupt update exists in any corpus) is rejected with a diagnostic naming the file; the previously valid current dataset is left undisturbed |
| C19 | A new edition (`GoodBaseCells`, edition 4; SHOM `101FR00369400` edition 2 after edition 1 update 2) replaces the lineage's content and resets the update number to 0; a re-issue (`2.2.6 Re-issue`, base-profile file at edition 1 update 3) replaces the content without changing edition or update; both preserve dataset identity and are distinguishable from each other and from an ordinary update |
| C20 | A cancellation (`2.2.7 Cancellation`: update-profile file whose `DSID.DSED` is `"0"`, S-101 Annex B-7) marks the lineage `Cancelled` and removes it from the active charts; a cancelled dataset is never presented as an active, currently loaded chart |
| C21 | A structurally corrupt base cell (`CorruptData/10100AA_X02SE.000`, truncated final record) is rejected with a diagnostic naming the file and byte position and never becomes an installed chart; other cells in the same exchange set still load |

### Slice 4 — Chart store, provenance, and import lifecycle

| ID | Requirement |
|---|---|
| C22 | A successfully decoded/updated dataset is published to the chart store as an immutable snapshot; starting a new import never mutates or becomes visible through a snapshot already published |
| C23 | The chart store persists, per snapshot, source file identity/hash, dataset ID, edition/update, product and encoding/catalogue versions, import timestamp, and diagnostic/provenance information, queryable once import completes |
| C24 | A spatial/scale query against the chart store answers from one snapshot generation only, never a mix of an old and a new snapshot |
| C25 | An import is cancellable mid-flight; cancelling leaves the previously installed chart (if any) intact and leaves no partial snapshot visible to queries |
| C26 | The import API is asynchronous and does not decode synchronously on the calling thread (verified at the contract boundary, since Charts has no UI thread of its own to block) |
| C27 | With the Charts module loaded, the `core.charts.library` feature (`IChartLibrary`) is available through the module host and imports an exchange set; without it, the host reports the feature unavailable and no other module or the host supplies a fallback reader |
| C31 | A published snapshot is persisted durably in the embedded database; chart data survives an application restart without requiring re-import |
| C32 | A query naming a reference cell (or cells) returns that cell plus every installed cell whose catalogue coverage polygon touches or overlaps the reference cell's (the catalogue gives coverage geometry, not explicit neighbours), served from the in-memory cache when already warm, and transparently decoded from the database and cached when not |
| C33 | A cell outside the current reference-cell-plus-adjacent neighborhood is eligible for eviction from the in-memory cache under a configured limit; evicting it never loses the durable data, and a later query for it succeeds by reloading from the database |

### Slice 5 — Reader boundary, diagnostics, and baseline

| ID | Requirement |
|---|---|
| C28 | `IExchangeSetReader`, `IS101DatasetDecoder`, and `IS101UpdateApplier` exist as independently testable seams — catalogue parsing is verifiable without dataset decoding, and dataset decoding without lineage application |
| C29 | Every decode/import outcome is an explicit result distinguishing rejected, unsupported-version, cancelled, and successfully-loaded; an unsupported or unknown version is never represented as an empty successful result |
| C30 | Load time, peak memory, and query latency for importing the 2.0.0 `PowerUp` exchange set (committed) are measured, and also for the UKHO `S-101_GB_Apr26` set (27 cells, about 28 MB) where that local-only data is present, and recorded against that fixture, as a first numeric baseline — not a pass/fail performance gate yet |
| C34 | A dataset is accepted for decoding when its `DSID` declares encoding `S-100 Part 10a` and product `INT.IHO.S-101` at an edition the decoder supports (version strings normalised, so `"2.0"` and `"2.0.0"` are the same); any other product or edition yields the unsupported-version result of C29, never a best-effort decode. The supported editions are set by Q6 |
| C35 | When the exchange catalogue and the dataset file disagree on edition, update number, purpose or certificate reference, the file's `DSID` is used and the disagreement is reported as a warning diagnostic; a mismatch alone never rejects a dataset. Declared `DSSI` record counts that differ from the records present are likewise a warning only |
| C36 | Each dataset's signature (`S100_SE_SignatureOnData`, or `S100_SE_DigitalSignature` as UKHO writes it) is verified against the certificate its own `CATALOG.XML` names (ECDSA; SHA-384 for 2.0.0 data, SHA-256 for 1.1.0), so the released 2.0.0 sets and all 36 UKHO/SHOM datasets verify and a byte-altered copy of `SequentialUpdate1`'s `.001` is rejected as corrupt; an unsigned dataset, a signature value that cannot be decoded as a signature (such as SHOM's placeholder text), or an unresolvable certificate reference is a warning, not a rejection; a decodable signature that fails verification is corrupt. No certificate chain or trust is evaluated (Q7) |
| C37 | Support files listed in the exchange catalogue are imported with their datasets (signature-checked per C36) and kept in the chart store with the snapshot that uses them; a feature's `fileReference` or `pictorialRepresentation` value resolves to the stored support file, and a value naming a file that is missing or not listed in the catalogue is preserved and reported as a diagnostic. Verified on the released 2.0.0 `InitialPowerUp` set (cell `10100AA_X0000` makes 5 references, 4 resolvable and one, `10100AATESTPC.TI`, truncated in the data and reported) and, where the local-only data is present, on UKHO's 127 support files (referenced by all 27 cells; the one reference naming an unlisted file, a producer typo, is reported). Showing the text or image is M1-D (Q10) |

## Decisions

- **Q1 — ISO 8211 approach (confirmed 2026-11-01):** build the bounded
  reader in-house inside `CoreCharts`, not a third-party/native library.
- **Q2 — Specification sourcing for Slice 2 (confirmed 2026-11-01):** only
  the test chart data is available; no normative IHO S-100/S-101
  product-specification or feature-catalogue text has been obtained. Slice 2
  proceeds against the test corpus's observed shape (the YAML sidecar and
  XML dumps), with that non-normative caveat recorded wherever Slice 2
  requirements are tested. Binding to the actual edition text is deferred
  indefinitely, not scheduled within M1-C; this plan does not claim
  S-101-specification conformance, only conformance to the audited fixture
  set. **Superseded, 2026-11-01 — see Q4 below.**
- **Q3 — Chart store technology (confirmed 2026-11-01):** an embedded
  database (assumed engine: SQLite) with durable immutable snapshots, plus
  an in-memory cache kept warm for the caller's reference cell(s) and their
  catalogue-adjacent neighbors, evicting outside that neighborhood. Not an
  in-memory-only store. The SQLite choice itself is still assumed, not
  separately confirmed.
- **Q5 — CRS scope (confirmed 2026-11-01):** decode only the coordinate
  reference systems the audited fixtures actually declare; broader
  CRS/projection generality stays a non-goal.
- **Q4 — Build against the real S-100 specification, not just the
  test-corpus shape (confirmed 2026-11-01, supersedes Q2).** Fetched and
  read **S-100 Edition 5.1.0, Part 10a — ISO/IEC 8211 Encoding** (October
  2023) directly from IHO's public download, saved at
  [Reference/Data/Specifications/](../Specifications/README.md) with
  provenance. It grounds the full binary container structure and the
  S-100/S-101 record field tags (see the Design section's "specification
  gate" above) — Slice 1 and most of Slice 2 are now built against verified
  normative text, not guesswork. The two narrower gaps first recorded here
  (the S-101 Feature Catalogue, and exchange-set lifecycle rules) were both
  closed on 2026-11-01 by the documents the user added: S-101 FC 2.0.0 and
  the S-101 2.0.0 Product Specification (§11.3, Annex B). See the audit in
  the Design section. The fixture table was revised in the same audit.
- **Q4 follow-up — the dataset file is authoritative over the catalogue
  (proposed 2026-11-01, design decision, not a question):** recorded as
  finding 3 and requirement C35, because the released test data's own
  catalogues contradict their files and the specification.

## Open questions

These block the Step 1 gate.

- **Q6 — Which S-101 editions does M1-C support? Answered 2026-11-01:
  recommendation accepted** (decode 2.0.0 fully; accept 1.x at the
  record/field level with a pre-operational-edition diagnostic). The corpus spans 1.0 to
  2.0.0; 2.0.0 (December 2024) is the first operational edition and the
  only one whose Product Specification, Feature Catalogue and released
  S-164 lineage are all in hand. The 1.x files differ only in ways the
  DDR-driven reader (C6) handles anyway, but 1.x semantics (for example
  `THAS`) are pre-operational. *Recommendation:* decode 2.0.0 fully, and
  also accept 1.x files at the record/field level so the 1.2.0 YAML oracle
  (C15) and the 1.1.0 re-issue and cancellation fixtures (C19, C20) stay
  usable, with a diagnostic noting the pre-operational edition. Products
  2.5-2.8 in the S-164 catalogue-loading sets are unsupported (C34).
- **Q7 — Is signature integrity checking in scope? Answered 2026-11-01:
  recommendation accepted** (integrity only, against the certificate in the
  same catalogue; trust chain stays out of scope). S-164 treats a
  corrupt update as one whose signature fails. The data signature verifies
  offline with .NET's built-in ECDSA (SHA-384 for 2.0.0, SHA-256 for 1.1.0)
  against the producer certificate embedded in the same `CATALOG.XML`; no
  trust chain is involved. *Recommendation:* yes, integrity only: a dataset
  whose signature does not verify against its catalogue's certificate is
  rejected as corrupt (C18 then tests a byte-altered copy as well as a
  truncated one); trust stays out of scope. If no, C18 tests only a
  truncated (structurally corrupt) copy and signatures are ignored.
- **Q10 — Are S-101 support files in M1-C's scope? Answered 2026-11-01:
  recommendation accepted, added as C37.** Real UKHO data
  carries 127 signed support files that most cells reference through
  `fileReference`/`pictorialRepresentation`; no requirement covers them.
  *Recommendation:* yes, minimally — a new C37: support files listed in the
  catalogue are imported with their datasets (signature-checked per C36)
  and kept in the chart store with the snapshot, and a feature's
  `fileReference`/`pictorialRepresentation` value resolves to the stored
  file, with a missing or unlisted file reported as a diagnostic. Showing
  the text or image stays in M1-D. If no, the attribute values are kept as
  plain strings and support files are ignored until M1-D.
- **Q9 — Use `s100tds` for update-application tests, and how? Answered
  2026-11-01: recommendation accepted.** Added as submodule
  `Reference/Data/s100tds` at `4c2cadeac5c74c8443e65fdaec446471cfe1810f`;
  the asks to its author and to the S-164 sub-group are the user's. It is the
  only data that exercises the update mechanisms the S-164 sets never
  touch (positional coordinate/segment/component edits, nested attribute
  edits, association edits). Its files say S-101 2.1.0, which C34 would
  reject as unsupported under Q6, even though their content is 2.0.0-valid.
  It is unofficial and unlicensed. *Recommendation:* add it as a submodule
  (a reference, not a copy, so nothing is redistributed) and run the
  update-applier tests against it through a test-only override that
  decodes its 2.1.0 label with the 2.0.0 rules; production behaviour stays
  as C34 says. Separately, ask the author for a licence and a 2.0.0 build,
  and ask the S-164 sub-group whether 2.0.0 re-issue, cancellation and
  corrupt-update sets are planned.
- **Q8 — Pin the S-100 XML schemas in the repo? Answered 2026-11-01:** the
  user downloaded the schema server's 2024-05-21 archive to
  `Reference/Data/distribution_20240521/`. Used for tests and reference
  only; the reader never validates against them at run time (finding 3
  shows the released test catalogues fail strict validation) and never
  fetches schemas (C4).

## Ledger

Step 2 run, 2026-11-01: `dotnet test Tests/Core/CoreCharts.Tests` — 174 tests,
165 failed, 9 passed. Every failure is `NotImplementedException` from a
production stub (checked per test from the TRX: no setup, fixture, zip, YAML
or byte-patching fault), so each fails because the behaviour is missing. The 9
passes are the 8 pre-existing module tests plus
`Runtime_WithoutCharts_ReportsTheLibraryUnavailable`, which passes already
because "feature unavailable without its module" is existing host behaviour;
it stays as a regression guard (same as M1-I's I9). The local-only UKHO/SHOM
tests ran on this machine; elsewhere they skip, naming the missing folder.
Test classes are in `Tests/Core/CoreCharts.Tests`; "(local)" marks a
local-only test, "×n" a theory's case count.

Full solution, same day: `dotnet build Poseidon.slnx` no warnings or errors;
`dotnet test Poseidon.slnx -maxCpuCount:1` every other project passes
(Core 141, Contracts 94, CoreTracks 74, CoreUI 55, CoreRadar 34, CoreAIS 30,
CoreDisplay 19, CoreNavigation 8, CoreSonar 8, all CMS suites and the
Military aggregate); CoreCharts.Tests fails only its 165 expected tests, and
the Civilian aggregate reports `MSB4181` because it runs each module suite
and passes on CoreCharts' failures, as designed. Nothing outside CoreCharts
regressed from the manifest or contract changes.

| Requirement | Test | Failing run (Step 2) | Passing run (Step 3) |
|---|---|---|---|
| C1 | `ExchangeSetReaderTests.Read_ReleasedPowerUp_ListsEveryEntryWithItsRoleMetadataAndResolvedFile`, `Read_EachCatalogueVersion_ReadsItsDatasets` ×3 (xc 5.0/5.1/5.2), `Read_OtherProducts_AreListedAsUnsupportedWithoutFailingTheSet` | NotImplementedException | Pass (2026-11-01) |
| C2 | `Read_ObservedFileNameConventions_ResolveInsideTheRoot` ×3, `Read_ShomPerCellFolders_ResolveInsideTheRoot` (local), `Read_FileNamesEscapingTheRoot_AreRejectedWithDiagnostics` | NotImplementedException | Pass (2026-11-01) |
| C3 | `Read_MissingCatalogue_IsRejectedNamingTheFile`, `Read_MalformedCatalogue_IsRejectedNamingTheFile`, `Read_DatasetEntryWithoutFileName_IsRejectedNamingTheElement`, `Read_CatalogueWithoutAnyEntries_IsRejected` | NotImplementedException | Pass (2026-11-01) |
| C4 | `Read_CatalogueWithExternalEntity_IsRefusedWithoutAnyNetworkRequest`, `Read_CatalogueWithRemoteSchemaHint_NeverFetchesIt` | NotImplementedException | Pass (2026-11-01) |
| C5 | `ExchangeSetReaderTests.Import_SupportedPackage_SucceedsWithoutNetworkAccess` | NotImplementedException | Pass (2026-11-01) |
| C6 | `Iso8211ReaderTests.Read_PrimaryBaseCell_ReturnsDdrDefinitionsAndEveryDataRecordInOrder`, `Read_RepeatingGroups_DecodeFromEitherFormatControlStyle` ×2, `Read_Utf8MarkedField_DecodesMultiByteText` | NotImplementedException | Pass (2026-11-01) |
| C7 | `Read_RecordLongerThanLimit_…`, `Read_TooManyFieldsInARecord_…`, `Read_FormatNestingDeeperThanLimit_…`, `Read_MoreRecordsThanLimit_…` | NotImplementedException | Pass (2026-11-01) |
| C8 | `Read_BadLeaderLength_IsRejectedAtPositionZero`, `Read_DirectoryPointingPastItsRecord_IsRejectedWithinThatRecord`, `Read_TruncatedFile_IsRejectedAtTheTruncatedRecord` | NotImplementedException | Pass (2026-11-01) |
| C9 | `Iso8211ReaderTests.Read_SameStreamTwice_…`, `ExchangeSetReaderTests.Read_SameExchangeSetTwice_…`, `S101DatasetDecoderTests.Decode_SameFileTwice_…` | NotImplementedException | Pass (2026-11-01) |
| C10 | `S101DatasetDecoderTests.Decode_Edition200BaseCell_ExposesIdentityVersionsCrsAndDistinctRecordCollections` | NotImplementedException | Pass (2026-11-01) |
| C11 | `Decode_Features_KeepFoidTypeAttributesTreesAndAssociations`, `Decode_PrimaryCell_MatchesItsYamlOracle` | NotImplementedException | Pass (2026-11-01) |
| C12 | `Decode_SpatialReferences_AllResolveAndSharedRecordsAreShared`, `Decode_FeaturesBeforeTheirGeometry_ResolveTheSameAsTheOriginalOrder` | NotImplementedException | Pass (2026-11-01) |
| C13 | `Decode_Coordinates_MatchTheOracleUsingTheDatasetsOwnFactors`, `Decode_DifferentDepthFactor_ScalesDepthsByThatFactor` | NotImplementedException | Pass (2026-11-01) |
| C14 | `Decode_UnknownFeatureTypeCode_IsKeptAndReported`, `Decode_UnknownGeometryReference_IsKeptAndReported` | NotImplementedException | Pass (2026-11-01) |
| C15 | `Decode_PrimaryCell_MatchesItsYamlOracle` | NotImplementedException | Pass (2026-11-01) |
| C16 | `S101UpdateApplierTests.Apply_SequentialUpdates_AdvanceTheLineageToEdition3Update5`, `Apply_ShomEdition1Updates_AdvanceToUpdate2` (local), `Apply_UkhoUpdate_AdvancesToUpdate1` (local), `Apply_UpdatePermutationCase_ChangesExactlyTheRecordsTheUpdateAddresses` ×70 (s100tds) | NotImplementedException | Pass (2026-11-01) |
| C17 | `Apply_UpdateAfterAGap_IsRefusedAndLeavesTheLastValidState`, `Apply_UpdateForANewerEdition_IsRefusedAndFlagsNotUpToDate`, `Apply_UpdateForAnOlderEdition_IsRefusedNamingTheExpectedEdition` (see the C17 note below), `Apply_ShomEdition1UpdateAfterEdition2_IsRefused` (local) | NotImplementedException | Pass (2026-11-01) |
| C18 | `ChartLibraryTests.Import_TruncatedUpdate_IsRejectedAndLeavesTheInstalledCellUntouched`, `Import_UpdateAlteredAfterSigning_IsRejectedAsCorrupt` | NotImplementedException | Pass (2026-11-01) |
| C19 | `Apply_NewEdition_ReplacesContentResetsUpdateAndClearsNotUpToDate`, `Apply_Reissue_ReplacesContentKeepingEditionAndTakesItsUpdateNumber`, `Apply_ShomEdition2_IsANewEdition` (local), `Decode_S100TdsNewEditionCase_IsTheNextEditionAsABase` | NotImplementedException | Pass (2026-11-01); the s100tds case test renamed `Decode_S100TdsNewEditionCase_IsTheNextEditionAsABase` (approved correction) |
| C20 | `Apply_Cancellation_MarksTheLineageCancelled` | NotImplementedException | Pass (2026-11-01) |
| C21 | `Import_CorruptBaseCell_IsRejectedWithPositionAndNeverInstalled`, `Import_SetWithOneCorruptCell_LoadsTheOthers` | NotImplementedException | Pass (2026-11-01) |
| C22 | `Import_NewerImport_NeverChangesAnAlreadyPublishedSnapshot` | NotImplementedException | Pass (2026-11-01) |
| C23 | `Import_RecordsProvenancePerCell` | NotImplementedException | Pass (2026-11-01) |
| C24 | `Query_DuringAnImport_AlwaysAnswersFromOneGeneration` | NotImplementedException | Pass (2026-11-01) |
| C25 | `Import_CancelledMidFlight_LeavesTheInstalledChartAndNoPartialSnapshot` | NotImplementedException | Pass (2026-11-01) |
| C26 | `Import_ReturnsBeforeDecodingAndDecodesOffTheCallingThread` | NotImplementedException | Pass (2026-11-01) |
| C27 | `ChartsRuntimeTests.Runtime_WithCharts_ProvidesTheLibraryWhichImportsAnExchangeSet`; `Runtime_WithoutCharts_ReportsTheLibraryUnavailable` | NotImplementedException; the second passes already (host behaviour, regression guard) | Pass (2026-11-01) |
| C28 | `ReaderBoundaryTests.Seams_AreUsableIndependently` | NotImplementedException | Pass (2026-11-01) |
| C29 | `Import_Outcomes_AreExplicit`, `Decode_UnsupportedProductOrEdition_IsReportedUnsupported` ×3 | NotImplementedException | Pass (2026-11-01) |
| C30 | `ChartBaselineTests.Baseline_ReleasedPowerUp_IsMeasured`, `Baseline_UkhoExchangeSet_IsMeasured` (local) | NotImplementedException | Pass; numbers below |
| C31 | `Restart_ReopensPublishedChartsFromTheDatabase` | NotImplementedException | Pass (2026-11-01) |
| C32 | `Query_ReferenceCell_ReturnsItAndEveryCellItsCoverageTouches` ×6, `Query_ColdNeighbourhood_LoadsFromTheDatabaseOnceThenServesFromCache` | NotImplementedException | Pass (2026-11-01) |
| C33 | `Query_MovingNeighbourhood_EvictsCellsOutsideItAndReloadsThemOnDemand` | NotImplementedException | Pass (2026-11-01) |
| C34 | `Decode_Edition20_IsSupported` ×2, `Decode_Edition1x_IsDecodedWithAPreOperationalDiagnostic` ×3, `Decode_UnsupportedProductOrEdition_IsReportedUnsupported` ×3, `Decode_S100TdsWithTestOverride_IsDecoded` | NotImplementedException | Pass (2026-11-01) |
| C35 | `Import_CatalogueDisagreeingWithTheFile_WarnsAndUsesTheFile`, `Decode_WrongDeclaredRecordCounts_IsAWarningNotARejection` | NotImplementedException | Pass (2026-11-01) |
| C36 | `Import_GenuineSignatures_Verify`, `Import_UpdateAlteredAfterSigning_IsRejectedAsCorrupt`, `Import_PlaceholderSignature_IsAWarning`, `Import_UnresolvableCertificateReference_IsAWarning`, `Import_UkhoExchangeSet_VerifiesEverySignature` (local), `Import_SupportFileAlteredAfterSigning_IsRejected` | NotImplementedException | Pass (2026-11-01, after the approved D6 correction) |
| C37 | `Import_SupportFiles_ResolveToTheirStoredContent`, `Import_ReferencedSupportFileMissing_IsReported`, `Import_ReferencedSupportFileNotInTheCatalogue_IsReported`, `Import_SupportFileAlteredAfterSigning_IsRejected`, `Import_UkhoSupportFiles_ResolveExceptTheProducersOneTypo` (local; renamed from `…AllReferencesResolve` by the approved D7 correction) | NotImplementedException | Pass (2026-11-01, after the approved D5 and D7 corrections) |

**Production code added in Step 2 (compile-only, every member throws):**
`System/Shared/Contracts/Charts.cs` (the approved host-shared contract);
`CoreCharts`: `Iso8211/Iso8211Reader.cs`, `ExchangeSets/ExchangeSetReader.cs`,
`S101/S101Dataset.cs` (records), `S101/S101DatasetDecoder.cs`,
`Lineage/S101UpdateApplier.cs`, `Store/ChartLibrary.cs`,
`ChartDiagnosticCodes.cs` (stable codes the tests assert on); `module.json`
now 0.1.0, providing `charts.library` and contributing `core.charts.library`;
`ChartsModule.Features` exposes the library (database path assumed:
`charts/charts.db` beside the host executable). Test tooling, independent of
production code: `Fixtures.cs` (paths, extraction of the released S-164 zips
with backslash entry names normalised so it also works on Linux, temp copies,
`[LocalDataFact]`, and `Iso8211Probe`, a minimal walker used only to locate
bytes to patch or reorder), `YamlOracle.cs`, `TestSupport.cs` (a canonical
structural comparer by runtime type, a loopback connection counter, a fixed
clock). The test project now also references `Core` for C27.

**Step 3 progress and the test defect (2026-11-01).** After the gate, the
approved C17 wording and the CI change (`git submodule update --init
--depth 1` of the three test-data submodules in the Windows and Linux jobs
of `build-test-package.yml`) were applied, and Step 3 began. Implemented so
far: the ISO 8211 reader (all 12 `Iso8211ReaderTests` pass), the exchange-set
reader (all 17 `ExchangeSetReaderTests` except the C5 import test, which
needs the library), and the S-101 decoder (17 of 20
`S101DatasetDecoderTests` pass). The update applier and chart library are
not started.

The 3 decoder failures are a test defect, not a decoder defect. The tests
assumed the YAML sidecar's record names are the encoded RCNM+RCID ("S1308"
= surface 8). Reading the raw bytes independently shows they are not: they
are the producer tool's own names. In `101AA00DS0011` the five-sounding
multipoint the YAML calls `P11511` is encoded as record 115/1, and
`P1151` is 115/7. In `101AA00DS0001` the YAML's `S1304` is surface 130/2
(consistently, for all three features using it) and `S1301` is 130/8. The
decoder returns exactly what the bytes encode. Affected:
`Decode_PrimaryCell_MatchesItsYamlOracle` (C15),
`Decode_Coordinates_MatchTheOracleUsingTheDatasetsOwnFactors` and
`Decode_DifferentDepthFactor_ScalesDepthsByThatFactor` (C13). Proposed
corrections, which keep both requirements' strength:
- C13 tests: find each YAML depth or point entry's decoded record by its
  first coordinate (latitude and longitude within 1e-7) instead of by name,
  then assert every position and depth as now.
- C15 test: drop the geometry-name comparison. C15 asks for feature IDs,
  attribute values, geometry *primitive kind* and association roles, all
  still asserted; the record names were never part of it. Replace it with
  "the feature has as many spatial references as the sidecar lists".

The user approved those corrections and Step 3 resumed. Running them
exposed two more instances of the same root cause, refined and verified
(all 20 `S101DatasetDecoderTests` now pass) and awaiting approval:
- C15: the sidecar's association targets use the producer's numbering too
  (`150/21` is encoded as information type 150/1), so the test compares the
  target's record kind, association name and role, not the target number.
- C13: two point records in `101AA00DS0011` share one position, so an
  entry is matched by record kind and its whole coordinate list (any record
  that matches will do), not by a unique first coordinate.

The user approved those refinements and Step 3 resumed with the update
applier, implemented after dumping the s100tds update encodings: an
update's `PAIX` indexes the *base* record's attributes in pre-order; an
`INAS`/`FASC` modify re-points the association with the same code; the
control fields (`COCC`, `CCOC`, `SECC`) pair with the data field after
them; no curve in any corpus (233,258 checked) has more than one segment,
so a segment control for any other segment is refused. Two data defects
were handled in production code: the s100tds updates use `COCC`, `C2IL` and
`SEGH` without defining them in their DDR (decoded with the S-101 Annex B
layout plus a `ddr-definition-missing` warning), and the 1.1.0 Power Up
cells write repeating groups in braces (`{3b12,b11,A}`). Result: 131 of the
132 non-library tests pass, including all 70 s100tds cases. The one failure
is a fourth test defect, awaiting approval: `Decode_S100TdsNewEditionCase_IsAnEdition2Base`
expects edition 2 from the case's title ("Edition bumped from 1 to 2"), but
its bytes say `DSED=3.0`, since that set's base cells are edition 2. Proposed:
expect application profile 1, update 0 and the s100tds base cell's edition
plus one (3), and rename the test `…IsTheNextEditionAsABase`.

**Design change found in Step 3, for the Step 3 gate.** The host refuses
native libraries in modules (`PluginLoadContext.LoadUnmanagedDll` throws:
"not reload-safe. Use a worker process"), so the plan's assumed SQLite
engine cannot run inside Charts. The store will instead be a managed
embedded store: content-addressed blobs (the dataset and support files as
received) plus a manifest replaced atomically on each publish, with cells
re-decoded from their stored lineage on load. No new dependency. C31-C33
are unchanged.

**Step 3 complete, for the gate (2026-11-01).** The user approved the
DS-NEWEDITION correction and asked that further test defects found in Step 3
be held, still failing, for this gate instead of stopping for each.

The chart library (`Store/`) is implemented:
- `ChartStore`: the dataset and support files exactly as received, stored
  by SHA-256 in `charts.db.blobs/`, beside a JSON manifest `charts.db`
  written to a temp file and moved into place. Opening removes files no
  manifest refers to (leftovers of rejected or cancelled imports and
  superseded editions). An unreadable manifest is set aside and logged,
  never overwritten silently.
- `ChartSnapshot`: immutable per generation; a cancelled cell is kept, so
  later updates to it are refused, but is never active (C20).
- `DatasetCache`: decoded cells keyed by their stored chain (so snapshots
  share them); the last queried neighbourhood is pinned, and up to
  `CacheCapacity` other cells stay warm, least recently used first out.
- `SignatureVerifier`: ECDSA over the file bytes against the catalogue's
  own certificate. SHA-384 or SHA-256 is accepted, because the sub-group's
  `CorruptData` cell is 2.0.0 data signed with SHA-256 (checked with
  openssl).
- `Coverage`: adjacency by polygon intersection (touching counts), with the
  bounding box as a fallback.
- The import: on a worker thread, one at a time. It processes each S-101
  dataset in name and sequence order (signature, then decode, then
  catalogue comparison, then lineage), then the support files, then checks
  every `fileReference`/`pictorialRepresentation` in changed cells. It
  publishes only by swapping the snapshot after the manifest is saved.

Three diagnostic codes were added: `dataset-missing`, `base-not-installed`
(an update whose cell is not installed) and `store-unreadable`.

Results: CoreCharts.Tests 171 of 174 pass. The 3 failures are test defects
held for this gate (below). Full solution `dotnet test Poseidon.slnx
-maxCpuCount:1`: every other project passes, with the same counts as
Step 2.

C30 baseline (this machine, Debug):
- PowerUp (6 cells): load 430 ms, allocated 90.0 MiB, peak working set
  127.6 MiB, query 0.073 ms mean over 200.
- UKHO `S-101_GB_Apr26` (27 cells): load 4,716 ms, allocated 1,327 MiB,
  peak working set 374 MiB, query 4.0 ms mean over 200. The first query
  computes adjacency.

Test defects held for the gate. In each case, reading the bytes shows the
data differs from what the test assumed, and the library behaves correctly:
- *D5 (C37, `Import_SupportFiles_ResolveToTheirStoredContent`).* The test
  collects references from feature attributes only, but `10100AA_X0000`
  carries them on information types (`NauticalInformation`, 7 references).
  The cell also refers to `10100AATESTPC.TI`, truncated in the file's own
  bytes (`…TESTPC.TI` then the unit terminator), while the catalogue lists
  `10100AATESTPC.TIF`. Proposed: collect references from features and
  information types, assert that the four listed ones resolve to their
  bytes, and assert that `10100AATESTPC.TI` does not resolve and that the
  import reported it as `support-file-not-listed`.
- *D6 (C36, `Import_UkhoExchangeSet_VerifiesEverySignature`).* The test
  expects 29 cells. The set holds 28 S-101 files: 27 cells, one of which
  (`101GB005DEVRC`) also has update `.001`. It also holds an S-128 GML file,
  which is not S-101. Proposed: expect 27, and relabel the C30 UKHO baseline
  "27 cells".
- *D7 (C37, `Import_UkhoSupportFiles_AllReferencesResolve`).* The test
  expects every reference to resolve. Including information types (D5),
  there are 550 references to 128 distinct names across all 27 cells. One
  does not resolve: `101GB005DEVQG` names `101GB00A0193.TXT`, which is not in
  the set; the listed file is `101GB001A0193.TXT`, a producer typo.
  Proposed: collect references from features and information types, and
  assert that the only unresolved reference is that one and that the import
  reported it as `support-file-not-listed`.

Requirement text corrections that follow from those:
- C30: "29 cells" becomes 27.
- C37: the InitialPowerUp cell has "5 references, 4 of them resolvable,
  one truncated in the data". UKHO's are "127 resolvable support files
  referenced by all 27 cells, one unresolvable reference".
The meaning of both requirements is unchanged.

**Gate decision (2026-11-01).** The user approved all of it: the D5-D7
corrections (applied as proposed; the UKHO references test is renamed
`Import_UkhoSupportFiles_ResolveExceptTheProducersOneTypo`), the C30/C37
count corrections (applied to the requirement, fixture and exploration
text), and the managed store in place of SQLite.

Final run after the corrections: CoreCharts.Tests 174 of 174 pass, with the
local-only UKHO/SHOM tests running on this machine. Full solution
`dotnet build Poseidon.slnx`: no warnings or errors. `dotnet test
Poseidon.slnx -maxCpuCount:1`: every project passes, the Civilian and
Military aggregates included (Core 141, CoreCharts 174, Contracts 94,
CoreTracks 74, CoreUI 55, CoreRadar 34, CoreAIS 30, CoreDisplay 19,
CoreNavigation 8, CoreSonar 8, the CMS suites). Awaiting the user's
acceptance to close out M1-C.

Follow-ups for later milestones (not done here):
- The module stores charts under `charts/` beside the host executable,
  which may be read-only in an installed deployment. It belongs in a
  per-user or per-machine data folder once the host offers one.
- The library's `ILog` is not wired to the host log, because the feature
  is built at registration, before `StartAsync` has the host.
- The decoder allocates about 47 times the input size, which is the first
  thing to tune if load time matters.

**Points for the Step 2 gate.**
- *C17, proposed wording change.* C17 says every refused update flags the
  cell not up to date. For an *older-edition* update (`OldUpdate` after
  edition 4 is installed) that would warn about a chart that is in fact
  complete; S-164 §2.3.5 asks only for refusal with the reason. The test
  asserts refusal, the expected/actual editions and unchanged content, and
  that the cell stays `Current`. Proposed C17 text: "…is refused with a
  diagnostic stating the expected and actual edition/update, and the
  lineage stays at its last valid state; a gap or a newer-edition update
  also flags it not up to date until a complete sequence, re-issue or new
  edition clears it (S-164 §2.3.3-2.3.5)". Needs approval.
- *C2.* The real catalogues use four file-name forms, not the two C2 names:
  `file:/S-101/…`, `file:/S100_ROOT/…` (S-158, catalogue beside
  `S100_ROOT`), bare `S-101\DATASET_FILES\…` with backslashes (exports),
  and SHOM's per-cell folders. All are tested, resolving against the
  catalogue's own folder; C2's "honoring the observed conventions" covers
  them, so its meaning is unchanged.
- *C4.* For a remote schema hint, "fails closed" is tested as "never
  fetched" (zero connections to a loopback listener), with reading
  continuing; an external entity is refused outright.
- *CI cannot run the fixture tests yet.* `.github/workflows/build-test-package.yml`
  checks out without submodules, so every fixture-backed test would fail
  there. It needs `S-101-Test-Datasets`, `S-164-Sub-Group` and `s100tds`
  checked out (not the other, larger reference submodules). Changing the CI
  workflow needs the user's approval.

## Status and follow-ups

Step 1 plan drafted 2026-11-01; Q1-Q5 answered. Revised the same day after
auditing the documentation and submodules the user added and parsing every
S-101 file in the corpora: the normative material is complete, the fixture
set moved to the released S-101 2.0.0 S-164 sets, C1, C6, C16-C21, C30 and
C32 were rewritten, C34 and C35 were added, and open questions Q6-Q8 were
raised. Q8 was answered the same day (the user added the S-100 schema
archive; validating every catalogue with it confirmed finding 3). Q6 and
Q7 were accepted as recommended the same day, adding C36 (signature
integrity). A search for more 2.0.0 data found no 2.0.0 re-issue,
cancellation or corrupt update anywhere, but found the unofficial
`s100tds` update-permutation set, raising Q9, answered the same day (added
as a submodule, used through a test-only edition override). Exploring the
user's UKHO download (S-101 support files, SHOM's change spreadsheet, and
the S-102/S-104/S-111/S-124 products) refined C1 and C36 and raised Q10
(support files), answered the same day by adding C37. All open questions
are answered; 37 requirements (C1-C37). The user approved the plan the
same day. At the start of Step 2, test design showed the runtime requires
every module feature to implement a host-shared interface, which the
Design's "all types inside CoreCharts" rule did not allow; per the
workflow the plan was amended (host-shared `IChartLibrary` in Contracts,
C27 reworded) and returned to the Step 1 gate before any test was written.

The milestone deliverable from the master plan is unchanged: audit
specifications and resources, pin fixture versions, Charts decodes a real
base cell, and metadata, attributes, topology and malformed-input
diagnostics are verified.

Known follow-ups, explicitly out of scope here and not blocking this plan:
a distributed/server-based spatial database beyond the embedded store (Q3),
full CRS/projection generality, portrayal/symbology (M1-D), digital-
signature/trust verification, reconciling the `charts.route-planning`
capability to Navigation, certificate trust and permits (S-100 Part 15),
S-158 validation checks (the `S-100-Validation-Checks`/`S-101-Validation`
material), and asking the S-101/S-164 groups for 2.0.0 re-issue,
cancellation and corrupt-update fixtures (none exist yet).