# S-158 Test Data Contract

## Purpose and evidence

S-158 is represented in this repository as validation test data for S-101, not
as a separate chart feature encoding. The observed `S-158/V1` tree contains
`TestDataSets/00AA_00001`-style case directories and matching
`LogsDumps/00AA_00001` directories. A representative test case contains:

- `CATALOG.xml`;
- an S-101 dataset such as
  `S100_ROOT/S-101/DATASET_FILES/10100AA_00001.000`; and
- a case report such as `101AA00400001_Report.docx`.

The example catalogue declares product identifier `S-101`, product-spec
version `1.1.0`, dataset purpose `newDataset`, edition 2, update 0, and the
dataset file reference. The XML dump artifacts under `LogsDumps` have root
`Dataset` and include sections such as `DataSetGeneralInformation`,
`DataSetCoordinateReferenceSystems`, `Informations`, `Points`,
`MultiPoints`, `Curves`, `CompositeCurves`, `Surfaces`, and `Features`.
They are inspection/validation artifacts, not the binary chart files.

## Contract boundary

Use the S-101 chart contract in [s-101-data-contract.md](s-101-data-contract.md)
to parse catalogue-referenced chart files. S-158 adds test-case metadata and
expected validation evidence around those files; it does not redefine the
S-101 feature, attribute, or geometry model.

```text
S158TestCase
  caseId: string
  packageRoot: path
  exchangeSet: ExchangeSet          # parsed by the shared S-100 reader
  s101Resources: ResourceReference[]
  reportFiles: ArtifactReference[]
  validationDumps: ArtifactReference[]
  expectedOutcome: TestOutcome?      # sourced from approved test specification
```

Each artifact reference should retain its relative path and role (chart data,
report, dump, or other test evidence). Do not identify a test case by a
hard-coded directory depth or assume every case has the same set of
attachments.

## Loading and validation behavior

1. Discover a case directory and parse its exchange catalogue with the shared
   S-100 catalogue reader.
2. Resolve the S-101 data reference from the catalogue; do not synthesize it
   from the folder name.
3. Pass the referenced chart dataset to the version-appropriate S-101 decoder.
4. Associate reports and XML dumps with the test case as evidence. Do not
   mistake an XML dump in `LogsDumps` for the authoritative chart payload.
5. Validate the decoder against expected results defined by the corresponding
   S-158 test specification/report. A dump is useful for comparison but is not
   automatically a complete expected-result oracle.

The reader should surface separate outcomes for package/catalogue errors,
S-101 decoding errors, and test assertions. Preserve diagnostic context and
the source artifact path. A validation failure is not a reason to discard the
case or mark it as successfully loaded.

## Versioning and scope

The observed V1 catalogue declares S-101 1.1.0. Carry this version through to
the S-101 decoder and avoid assuming the S-101 1.2 fixture layout applies.
The repository files inspected here establish the package shape; they do not
by themselves define all normative S-158 test assertions or pass/fail rules.
Bind expected outcomes to the matching, approved S-158 specification.
