# Feature Mappings

This directory stores the data and reader contracts derived from the S-100 test
datasets. The contracts distinguish product data from the test harnesses that
package and exercise it.

## Contracts

- [S-101 chart data and reader contract](s-101-data-contract.md) — chart
  exchange sets, encoded cells, feature/spatial records, and update handling.
- [S-158 test data contract](s-158-data-contract.md) — S-101 exchange sets
  plus validation-test cases, reports, and dumps.
- [S-164 test data contract](s-164-data-contract.md) — S-101 exchange sets
  plus ECDIS loading/display scenarios, updates, and support files.

These contracts are derived from test-corpus evidence, not normative
specification text. The actual IHO specification documents, fetched
directly and with provenance, live in
[../Specifications/](../Specifications/README.md).

## Maintenance

Record the dataset repository revision and product-specification/encoding
versions when updating a contract. Keep observed repository layouts separate
from normative requirements; confirm normative field definitions against the
matching IHO product specification and encoding schema before implementing
them.
