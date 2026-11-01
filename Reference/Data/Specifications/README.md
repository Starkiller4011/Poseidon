# Specifications

Normative IHO specification documents, fetched directly from IHO's public
download (not test-corpus evidence, not a secondhand summary). Each entry
records where it came from and when, so it can be re-verified or updated.

## S-100 XML schema distribution (20240521)

- Location: `../distribution_20240521/` (added by the user, 2026-11-01).
- Source: the "archives" section of
  `https://schemas.s100dev.net/schemas/S100/5.2.0/index.html`, the IHO
  S-100 schema server. Its `README.pdf` is dated 2024-05-20 and covers
  S-100 Editions 5.0.0, 5.1.0 and 5.2.0.
- Content: 370 XSD, 44 schematron, 109 XML files. For each S-100 edition:
  the exchange catalogue (`S100Catalog/*/S100_ExchangeCatalogue.xsd`,
  namespaces `http://www.iho.int/s100/xc/5.0`, `5.1`, `5.2`, the three the
  test data uses), feature catalogue (`S100FC`), portrayal (`S100PC`), data
  protection (`S100SE/*/Part15.xsd`) and code lists, plus mirrors of the
  ISO TC 211 and OGC schemas they import (`schemas.isotc211.org/`,
  `schemas.opengis.net/`). No sample catalogues are included. 5.1.0 holds
  only the schemas revised for 5.1; the others are imported from 5.0.0.
- Offline use: most imports are relative, but some name
  `https://schemas.isotc211.org/…`, `http://www.w3.org/1999/xlink.xsd` and
  `http://www.w3.org/2001/xml.xsd`. A resolver must map the three schema
  hosts onto this folder and the W3C URLs onto `S100/5.0.0/w3c/XML/`, and
  canonicalise both routes to one file URI (otherwise .NET's
  `XmlSchemaSet` loads `gco.xsd` twice and fails). With that mapping,
  validation runs with no network access.
- Results against the real data (2026-11-01, throwaway .NET validator in
  the session scratchpad): S-101 Feature Catalogue 2.0.0 is valid against
  `S100FC` 5.2. Of 118 exchange catalogues, 38 are valid and 80 are not —
  including every catalogue in `S-101-Test-Datasets/S-164` (0 of 28) and
  in the released S-164 Part 2 dataset-loading and update sets (0 of 15).
  The 5.2 failures put `encodingFormat` where the schema expects
  `producingAgency`; the 5.0 failures omit the required `gml:id` on
  coverage polygons and put bare text in `cit:name`.
- Caveat: these builds predate S-100 Edition 5.2.1 (December 2025), whose
  Part 17 changes (for example `editionNumber` as a positive integer) may
  have a newer schema build on the server.
- Used by: [M1-C](../Plan-Memory/features/m1-c-chart-data-pipeline.md),
  for tests and reference only; the reader never validates against them
  at run time and never fetches schemas.

## S-100 Part 10a — ISO/IEC 8211 Encoding

Superseded for reference purposes by the complete S-100 Edition 5.2.1
(December 2025) in `../S-100 Documentation/S-100 Ed 5.2.1_FINAL.pdf`, whose
Part 10a (encoding edition 5.2, the one S-101 2.0.0 data declares) changes
only the Delete-instruction notes for `COCC`/`SECC`/`CCOC`. Kept as the
first copy obtained.

- Files: `S-100_ed5.1.0_Part10a_ISO8211-Encoding.pdf` (original),
  `S-100_ed5.1.0_Part10a_ISO8211-Encoding.txt` (extracted text, `pdftotext
  -layout`; legacy/Windows-1252 bytes in a few tables, not UTF-8 clean —
  re-extract from the PDF if a tool needs clean text).
- Source: `https://iho.int/uploads/user/Services%20and%20Standards/S-100WG/MISC/US_S100-Part10a.pdf`,
  fetched 2026-11-01. IHO's public upload; no paywall or login encountered.
- Edition: S-100 Edition 5.1.0, October 2023.
- Content: the full generic ISO/IEC 8211 Logical Record structure (Leader,
  Directory, Field Area — byte-level layout) and the S-100 common/object
  record field tags built on it: `DSID`/`DSSI` (+ `ATCS`/`ITCS`/`FTCS`/
  `IACS`/`FACS`/`ARCS` code tables) for the Data Set General Information
  record; `CSID`/`CRSH`/`CSAX`/`PROJ`/`GDAT`/`VDAT` for the Data Set
  Coordinate Reference System record; `IRID` for Information Type records;
  `PRID`/`MRID`/`CRID`/`CCID`/`SRID` plus the coordinate fields (`C2IT`/
  `C3IT`/`C2FT`/`C3FT`/`C2IL`/`C3IL`/`C2FL`/`C3FL`) for Point/Multi Point/
  Curve/Composite Curve/Surface records; `FRID`/`FOID`/`ATTR`/`INAS`/`SPAS`/
  `FASC`/`THAS`/`MASK` for Feature Type records; the `NATC`/`ATIX`/`PAIX`/
  `ATIN`/`ATVL` pre-order-traversal encoding for simple/complex/repeating
  attributes; and the per-record `RUIN` (Insert/Delete/Modify) update
  mechanism plus the 23-step record-order rule (§10a-4.5) for applying an
  update file's records.
- **What it does NOT define** (confirmed by reading it, not inferred):
  feature-type codes, attribute codes, and code-list/enumeration domains —
  those live in the product-specific Feature Catalogue (for S-101, the IHO
  GI Registry's `S-101_FC_*.xml`; public/login-free access to that registry
  is not yet confirmed — see the M1-C feature file's Decisions). It also
  does not define exchange-set-level dataset lifecycle (base cell vs.
  numbered update vs. reissue vs. cancellation identity/edition rules) —
  that is product-specification territory, not yet fetched.
- Used by: [M1-C: Chart-data pipeline](../Plan-Memory/features/m1-c-chart-data-pipeline.md).
