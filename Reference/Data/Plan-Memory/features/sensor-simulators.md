# Sensor simulators and ComputerGeneratedForce

Milestone: Supporting infrastructure (not tied to an M1-x letter; used by M1-E
now, M1-F and M1-G later, and eventually the CMS backlog)
Status: **Plan approved by the user on 2026-10-27.** The user created the
project skeletons in `Simulators/` (13 console-app stubs, all
`Console.WriteLine("Hello, World!")`, not yet added to `Poseidon.slnx`) before
this plan was written. Sequencing agreed with the user on 2026-10-27: Step 2
(tests) may proceed incrementally alongside the remaining M1-E slices; Step 3
(code) waits until M1-E is done. Step 2 has not started.

## Problem and scope

Poseidon's sensor adapters (`CoreAIS`, `CoreRadar`, and later `CoreSonar` and
Navigation's GPS/own-ship input) currently have no way to receive data except
a direct in-process feature call (`IAisInput.Submit`, `IRadarInput.Submit`),
which is what hand-rolled test fixtures use today (see
`Tests/Core/CoreTracks.Tests/TrackPipelineIntegrationTests.cs` and the "real
sensor packages" copied into `CoreTracks.Tests.csproj`). There is no faithful,
reusable way to exercise the adapters the way a real sensor connection would,
and no way to run the application live against a realistic, scriptable moving
scenario for manual testing. The master plan deferred "device-specific
protocols and hardware integrations" as a separate decision; this feature picks
that decision up for the *simulated* connection kind specifically (not for any
particular real hardware's dialect).

This feature covers:

- `Simulators/ComputerGeneratedForce` (CGF): a standalone server process that
  generates a simulated world (own ship and other vessels/contacts moving over
  time per a scenario) and serves it to simulator clients over a simulated
  connection.
- The civilian simulator processes: `AisSim`, `NavigationRadarSim`,
  `EchoSounderSim`, `NdsSonarSim`, `GpsSim`. Each connects to CGF as a client,
  derives its own sensor-specific view of the world, and emits wire-protocol
  sentences over its own simulated connection, exactly as a real sensor's
  connection would. `NdsSonarSim` is civilian (corrected 2026-10-27: it is a
  fish-finding and seabed-mapping sonar, not a military one) and feeds
  `CoreSonar` alongside `EchoSounderSim`.
- The receiving side in Poseidon: a listener added to `CoreAIS`, `CoreRadar`
  and (lower priority) `CoreSonar` that accepts a simulated connection, decodes
  the wire sentences into the adapter's existing normalized contract
  (`AisPositionReport`, `RadarTargetReport`, ...), and submits them through the
  existing `Submit` feature call. Decided with the user on 2026-10-27: this
  receiving side is in scope here, since the simulators are inert without it
  and it is the direct counterpart of the same loop.

Non-goals:

- **Not shipped.** Simulators and CGF are developer/test tooling, never part
  of Core's packaged/published output, the same rule the reference chart
  datasets already follow.
- **No change to Tracks or the fusion contracts.** Simulators feed the
  *existing* `AisPositionReport`/`RadarTargetReport` contracts through a new
  decoder; `TrackObservation` and the M1-E pipeline are untouched.
- **The 7 CMS/military simulator stubs stay empty.** `FireControlRadarSim`,
  `SurveillanceRadarSim`, `EwSim`, `DataLinkSim`, `HullMountedSonarSim`,
  `TowedArraySonarSim` and `VariableDepthSonarSim` map to CMS's own future
  track sources (fire-control radar, surveillance radar, EW, military sonar,
  Link 11/16/22; see `cms-backlog.md` T2 and D4), reserved under their own
  names but not built out until the CMS backlog starts (D8). The project
  skeletons stay as placeholders. (`NdsSonarSim` is civilian, see above, not
  one of these seven.)
- **Real hardware protocols remain separate.** A simulated connection speaking
  a realistic wire protocol is not a claim that any particular real device
  uses exactly that dialect; adapting to one is future work when a device is
  selected.
- **GPS has no consumer yet.** `GpsSim`'s natural receiver is Navigation's
  own-ship provider (21.10 in `m1-e-track-fusion.md`), which does not exist
  until M1-F starts. `GpsSim` can still be planned and built; its receiver
  side waits for M1-F.

## Design

Two independent simulated connections, not one:

```text
ComputerGeneratedForce (server)
  -> simulated connection -> AisSim / NavigationRadarSim / EchoSounderSim / NdsSonarSim / GpsSim (clients)
       -> simulated connection (wire protocol) -> CoreAIS / CoreRadar / CoreSonar / Navigation (listeners)
```

- **CGF <-> Sim.** Our own protocol; it never has to resemble anything real,
  since both ends are our own tooling. Assumed (recommend TCP, loopback by
  default, host/port configurable): CGF runs as a long-lived server, ticking
  its simulated world on a clock and streaming each entity's true state
  (position, course, speed, class) to every connected client; each Sim
  subscribes to the subset of entities relevant to its sensor type (for
  example `NavigationRadarSim` needs every nearby contact's true position to
  compute a simulated return; `AisSim` needs each vessel's own transponder
  state).
- **Sim <-> Poseidon adapter.** Must behave like the real sensor's connection
  would, carrying the real wire protocol:
  - `AisSim` emits AIVDM sentences per visible vessel at the AIS descriptor's
    expected interval (10 s, 21.4 of `m1-e-track-fusion.md`).
  - `NavigationRadarSim` computes range/bearing from own ship to nearby
    contacts (with configurable sensor noise) and emits ARPA-style sentences
    (for example `TTM`; see the civilian non-geodetic source research in
    21.11) at the radar's scan-rate interval.
  - `GpsSim` emits own ship's simulated position and heading as GPS sentences
    (for example `GGA`/`RMC`/`HDT`).
  - `EchoSounderSim` emits synthetic depth along own ship's track as depth
    sentences (for example `DBT`/`DPT`).
  - `NdsSonarSim` emits synthetic fish-finding and seabed-mapping sonar data
    (bottom depth/composition and fish-arch-style contacts) over its own
    simulated connection; exact sentence shape is a Step 2 detail since it is
    a more capable sensor than a basic echo sounder.
  - Assumed (recommend TCP, loopback by default, host/port configurable,
    matching the CGF link): a real NMEA 0183-over-TCP multiplexer is a
    realistic precedent, not just a convenient stand-in, and keeps every
    target platform (Windows, Arch Linux, RHEL) on equal footing without
    serial-port platform quirks.
- **Poseidon-side listener.** A small component per adapter (`CoreAIS`,
  `CoreRadar`, later `CoreSonar`) that accepts the simulated connection, reads
  line-delimited, checksummed sentences, decodes them into the adapter's
  existing report record, and calls the existing `Submit`. No change to the
  normalizer, `Tracks`, or the fusion contracts; a disconnected simulated
  connection is treated exactly like today's `SourceHealth.Disconnected`
  path, not a special case.
- **Shared code, to avoid 13x duplication.** Recommended, to settle at Step 2:
  a new non-deployed library (for example `Simulators/Shared`) for the
  NMEA-style sentence builder, checksum, and a small line-oriented TCP
  client/server helper, referenced by every `*Sim` project and by CGF; and,
  on the receiving side, a small shared decoding/listener helper (for example
  `System/Shared/SensorTransport`, parallel to `System/Shared/Contracts`)
  referenced by `CoreAIS`/`CoreRadar`/`CoreSonar`, so the three listeners are
  not copy-pasted. Contracts itself stays as-is (types only).
- **Scenario authoring.** Assumed for a first version: scenarios are
  code-built inside `ComputerGeneratedForce` (a fluent builder: an entity
  starting here, turning there, at a given speed, at given times), simplest to
  build and to keep in step with the tests that use them. A data-file format
  (for load-test profiles or non-recompiled manual scenarios) is a later
  addition if the code-built form proves limiting.

## Requirements

| ID | Requirement |
|---|---|
| R1 | `ComputerGeneratedForce` runs as a standalone server, ticks a simulated world (own ship and other vessels) forward per a scenario (position, course, speed, manoeuvres over time), and serves connected clients continuously |
| R2 | Each civilian Sim process (`AisSim`, `NavigationRadarSim`, `EchoSounderSim`, `NdsSonarSim`, `GpsSim`) connects to a running CGF over a simulated connection and receives the subset of world entities relevant to its sensor type |
| R3 | `AisSim` emits each visible vessel's own AIS transponder report as wire-format AIVDM sentences at the AIS descriptor's expected interval over its own simulated connection |
| R4 | `NavigationRadarSim` computes range/bearing (with configurable sensor noise) from own ship to nearby contacts and emits ARPA-style wire sentences at the radar's scan-rate interval |
| R5 | `GpsSim` emits own ship's simulated position and heading as wire-format GPS sentences; its Poseidon-side receiver is out of scope until Navigation's own-ship provider exists (M1-F) |
| R6 | `EchoSounderSim` emits synthetic depth soundings along own ship's track as wire-format depth sentences |
| R6a | `NdsSonarSim` emits synthetic fish-finding and seabed-mapping sonar data (civilian, corrected 2026-10-27) over its own simulated connection |
| R7 | `CoreAIS` gains a listener that accepts a simulated connection, decodes line-delimited AIVDM sentences into `AisPositionReport`, and submits them through the existing `IAisInput.Submit`, with no change to the normalizer or Tracks contracts |
| R8 | `CoreRadar` gains an equivalent listener decoding ARPA-style sentences into `RadarTargetReport` and submitting through `IRadarInput.Submit` |
| R9 | `CoreSonar` gains an equivalent listener for `EchoSounderSim`'s and `NdsSonarSim`'s sentences once Sonar's own contract shape is defined (lower priority; Sonar is not a track source) |
| R10 | A simulated connection becoming unavailable is treated by the receiving adapter exactly as today's disconnected-source path (`SourceHealth.Disconnected`), not a special case |
| R11 | A load scenario can drive many simultaneous contacts through `AisSim`/`NavigationRadarSim` concurrently, toward the M1-E throughput baseline (R31, 5,000 reports/s at 1,000 tracks) and the per-source queueing tests (R26, R29) |
| R12 | The 7 CMS/military Sim projects stay empty, reserved scaffolding; no behavior is added until the CMS backlog starts (D8) |
| R13 | No `Simulators/` or `ComputerGeneratedForce` project is referenced by, or packaged into, Core's published output |

## Decisions

Confirmed with the user on 2026-10-27:

- Simulators are standalone applications, not in-process modules; each one
  interacts with its target the same way a real sensor's connection would,
  over a simulated connection.
- Data is generated at the wire protocol level, not the normalized-contract
  level.
- `ComputerGeneratedForce` already exists (project skeleton) to generate a
  simulated world and seed the other simulators with entities.
- `ComputerGeneratedForce` runs as a live server; Sim processes connect to it
  at runtime (not a static scenario file consumed independently by each Sim).
- The Poseidon-side listener/decoder (the receiving half of the Sim
  connection) is in scope for this same feature, not a separate one.
- Sequencing: this plan now; this feature's tests may be written
  incrementally while the remaining M1-E slices (3 to 6) are worked on; this
  feature's own Step 3 (code) waits until M1-E is done.
- **`NdsSonarSim` is civilian**, corrected on 2026-10-27: it is a
  fish-finding and seabed-mapping sonar, not a military one. It moves from the
  CMS/reserved group into the civilian group alongside `EchoSounderSim`,
  feeding `CoreSonar`. The CMS/reserved group is 7 projects, not 8 (an earlier
  miscount in this file's first draft also said 9; both are corrected).

## Open questions

None block approving the overall direction; these are implementation details
to settle at Step 2 (tests), in order of how much they'd reshape the design if
answered differently:

1. **Transport choice.** TCP (loopback by default, host/port configurable) is
   assumed for both the CGF<->Sim link and the Sim<->Poseidon link. Confirm,
   or name a different simulated-connection kind (named pipe, UDP, a virtual
   serial port) if one better matches what a real device would use.
2. **Scenario authoring.** Code-built scenarios in CGF are assumed for a first
   version. Confirm, or ask for a data-file format (JSON or similar) from the
   start if scenarios need to be authored without recompiling.
3. **Shared-code layout.** A new `Simulators/Shared` library and a new
   `System/Shared/SensorTransport` library are proposed so the listener and
   sentence-building code is not duplicated across 13 projects and 3 adapters.
   Confirm the names/locations, or fold the receiving-side helper into
   `Poseidon.Contracts` instead.
4. **Exact wire dialects per sensor beyond AIS/ARPA/GPS basics** (for example
   whether `NavigationRadarSim` should also emit `TLL` for a GPS-equipped
   radar, per the 21.11 research) are a Step 2 test-design detail.
5. **CMS Sim ownership mapping** (for example `DataLinkSim` likely becomes a
   `CmsComms` concern for Link 11/16/22, D4) is irrelevant until the CMS
   backlog starts; parked here for when that planning begins.

## Ledger

Empty; Step 2 (tests) has not started.

## Status and follow-ups

Plan approved 2026-10-27. Step 2 (tests) may begin alongside the M1-E slice
work already underway, per the agreed sequencing, on the user's order; Step 3
(code) waits until M1-E (slices 3 to 6) is done.
