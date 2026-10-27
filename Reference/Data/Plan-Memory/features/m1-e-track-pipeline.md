# M1-E: Track pipeline (as built)

Milestone: M1-E (prerequisite M1-A; UI integration needs M1-B)
Status: Implemented and verified on Windows (2026-10-26): single-source tracks,
sensor-side relay, pull feed, Display overlay; the full solution run passed 677
tests. **Acceptance is on hold**: the requirements confirmed on 2026-10-26
(cross-sensor fusion, per-source timings, queue ingress) supersede parts of this
design. The rework is planned in [m1-e-track-fusion.md](m1-e-track-fusion.md).
Cross-platform CI has not run on this change.

This file holds the sensor-to-track proposal (from the former architecture
section), the M1-E plan as built (former section 19) and its ledger rows (former
section 17). Former section numbers are kept in headings so older references
remain traceable (see the section map in the master plan). References to
"section 20" and "section 21" are now in
[m1-e-track-fusion.md](m1-e-track-fusion.md); references to section 9 are in the
master plan.

## Sensor-to-track contract proposal (from former section 4)

- Each adapter submits a normalized `TrackObservation` to an ingestion contract
  owned by Tracks and receives an explicit accepted/rejected result and managed
  track ID when association succeeds. A source track ID is not the global ID.
- Observations carry source/module ID, source-local target ID when available,
  observation ID/sequence, measured UTC time, receipt time, coordinate frame,
  position and/or source measurement, velocity when known, quality/uncertainty,
  and source health. Use explicit units and reference frames.
- Tracks alone creates managed track IDs, associates observations, applies
  updates and publishes immutable track snapshots. Sensors do not construct
  authoritative tracks or call Display directly.
- Initially associate repeated observations by source plus source-local ID.
  Do not merge AIS and radar reports solely because positions are nearby.
  Cross-source correlation/fusion is separate policy requiring evidence/tests.
  (Superseded by the 2026-10-26 fusion requirements.)
- Preserve bearing-only/range-only detections as measurements when applicable.
  Do not invent a geographic position without sensor pose, reference frame and
  sufficient data; such measurements need a supported estimator before they
  become chart-positioned tracks.
- Define duplicate/out-of-order handling, source-ID reuse, stale/lost/removed
  lifecycle, bounded queues and overload diagnostics. Distinguish source
  disconnect from confirmed target disappearance.
- Expose a revisioned snapshot plus ordered changes (upsert/remove/source state).
  Display and Navigation subscribe independently; a late/reloaded consumer
  resynchronizes from a snapshot and then applies changes without gaps.
- Feed cancellation/unsubscription and a bounded resync path are mandatory for
  hot reload. Keep authoritative track state in a versioned checkpoint during
  Tracks replacement; sensor reconnection must not create duplicate tracks.
- Navigation calculates CPA/TCPA from own-ship and target kinematics at a common
  time/reference frame. Return unavailable/qualified results for missing or
  stale motion, handle near-zero relative velocity, and test units, negative
  TCPA and motion assumptions. Constant-velocity CPA is an explicit model,
  not a guarantee of future separation.

## 19. M1-E: Track pipeline

Started 2026-10-26 (M1-B merged; CI green on Windows, Arch Linux and RHEL).
Requirements are written as acceptance tests before production code.

### Design decisions (assumed; confirm or revise)

- **Two Tracks-owned points.** `tracks.ingestion` (`ITrackIngestion`) and
  `tracks.feed` (`ITrackFeed`), both `single`, contributed by Tracks as
  `core.tracks.ingestion` and `core.tracks.feed`. The shared vocabulary
  (`TrackObservation`, `Track`, snapshots, changes) lives in `Poseidon.Contracts`
  so type identity survives Tracks reload.
- **Pull-based feed, no subscriptions.** `InvokeAsync` is synchronous, gated and
  must not retain plugin instances, so a callback subscription would pin a
  retired load context. Consumers keep only a cursor (`epoch`, `revision`) and
  poll `GetChanges`. Unsubscription is therefore trivially complete, and
  hot reload resynchronizes through `ResyncRequired` (unknown epoch, future
  revision, or a cursor older than the bounded change log) followed by a fresh
  snapshot.
- **Association** is by (source ID, source-local target ID) only. Observations
  without a target ID are rejected: associating unidentified detections needs a
  tracker, which is separate scope. Nearby reports from different sources are
  never merged.
- **Latest observation replaces** position, kinematics and measurement; the name
  is kept when an observation omits it. A bearing/range-only observation yields a
  managed track with no position and the retained polar measurement.
- **Ordering.** An observation older than, or equal in measured time to, the last
  accepted one is `OutOfOrder`, except an exact repeat (same sequence and time),
  which is `Duplicate`. Sequence is provenance, not the ordering key, so a
  restarted source whose sequence resets is not mis-rejected.
- **Lifecycle** uses the Tracks clock (receipt time): Active, then Stale, Lost,
  and removed (default 60 s, 180 s, 600 s). An update revives Stale/Lost tracks.
  A target ID reused after removal creates a new track.
- **Source disconnect is not target disappearance.** While a source is reported
  `Disconnected` its tracks are held at Stale and never progress to Lost or
  removed; ageing restarts from the moment the source resumes (an accepted
  observation implies the source is back).
- **Bounds.** At most 10,000 tracks (new tracks beyond that are rejected,
  updates still accepted) and a 10,000-entry change log.
- **Checkpoint.** Tracks captures epoch, revision, ID counter, tracks and source
  state (schema 1) so reload preserves track IDs and consumer cursors; an empty
  Tracks captures nothing.
- **Sensor adapters** (AIS and Radar) each declare an input point
  (`ais.input`, `radar.input`), contribute their own input feature
  and depend only on `Core.Tracks`. **Core.Sonar is not a track source**
  (user-confirmed): civilian sonar is for depth and bathymetry, so it has no
  Tracks dependency, input point or normalizer. A future transport (serial,
  network, replay)
  pushes native reports through `IModuleHost.InvokeAsync`; the adapter
  normalizes them (units, sentinels, polar to geodetic when a pose is known) and
  forwards them through a shared bounded `ObservationRelay` that calls Tracks
  from a background pump. Overload is reported (`QueueFull`), not hidden.
  The pump starts with execution-context flow suppressed, otherwise it would
  inherit the lifecycle "feature calls blocked" flag.
- **Simulated and recorded input** is supplied by pushing reports through the
  same inputs (tests do this); the adapters ship no built-in fake traffic and
  claim no production device protocol.
- **Polar to geodetic** uses a spherical-earth approximation (mean radius
  6,371,008.8 m); adequate for civilian radar ranges, not geodetic survey.

### Requirements

| ID | Requirement |
|---|---|
| E1 | Invalid observations are rejected with a reason: blank source, missing target ID, non-finite or out-of-range latitude/longitude, negative speed, course or heading outside [0, 360), default measured time |
| E2 | The first observation creates a managed track with a Tracks-generated ID; repeats update the same track; equal target IDs from different sources, or nearby positions, stay separate tracks |
| E3 | Exact repeats are `Duplicate` and older observations `OutOfOrder`; neither changes the track |
| E4 | A bearing/range-only observation creates a position-less track that retains its measurement |
| E5 | Tracks age Active, Stale, Lost and are removed per policy; updates revive them; removal is a feed change; a reused target ID after removal gets a new track ID |
| E6 | A disconnected source holds its tracks at Stale without losing or removing them; ageing restarts when the source resumes or reports again |
| E7 | New tracks beyond capacity are rejected with `CapacityExceeded`; existing tracks still update |
| E8 | The feed offers a snapshot and gapless ordered changes; unknown epoch, future revision or a trimmed log requires resync; snapshot plus changes converges with the manager |
| E9 | Checkpoint and restore preserve epoch, revision, track IDs and source state; re-observation updates the restored track; unsupported state is rejected |
| E10 | Tracks declares and contributes both points, has no dependencies, starts and stops idempotently and ages tracks on its own timer |
| E11 | The relay is bounded (`QueueFull`), assigns increasing sequences and receipt times, delivers in order including source health, stops promptly and restarts, and reports counters |
| E12 | AIS reports normalize to observations: knots to m/s, wire sentinels become unknown, invalid MMSI or position rejected |
| E13 | Radar reports project to a position only with a known pose (relative bearing needs a heading); otherwise they stay measurements; invalid range or bearing is rejected |
| E14 | Core.Sonar is not a track source: no Tracks dependency, no input point, no feature contribution |
| E15 | AIS and Radar each declare their input point and feature, depend only on Core.Tracks, checkpoint their sequence and reject input when not running |
| E16 | With the real runtime, AIS and Radar input becomes managed tracks in the feed; a repeat report updates the same track ID |
| E17 | Reloading keeps track IDs and the sensors keep updating the same tracks; without Tracks the sensors are Blocked and recover when it returns (Sonar is unaffected) |
| E18 | Display consumes the feed without depending on Tracks: it resyncs on epoch change or gap, reports an explicit no-provider state, and exposes a toggleable tracks overlay |

### Status

Implemented and verified on Windows (ledger rows E1-E18; full solution run 677
passed, 0 failed). The requirements confirmed on 2026-10-26
supersede parts of this design: association by source plus target ID (E2),
single-clock lifecycle timings (E5) and the "never merge nearby sources"
decision give way to correlation and fusion, and sensor feeds move to queues
that Tracks consumes. Acceptance is on hold until the fusion rework in
[m1-e-track-fusion.md](m1-e-track-fusion.md) is planned, approved and built under
the workflow. M1-E is complete once that is done and the pull-request CI run
passes on Windows, Arch Linux and RHEL-compatible containers.

What exists: the shared track vocabulary, `ITrackIngestion`/`ITrackFeed`, the
Tracks manager with lifecycle, source health, bounded log, resync and
checkpoint; AIS and Radar adapters with normalizers over a shared
`ObservationRelay`; `TrackReplica`/`TrackReplicaPump` for consumers; and a
Display tracks overlay (toggleable layer plus a status line in the view).

Known gaps and follow-ups:

- Display does not draw track symbols yet: that needs the chart projection
  (M1-D). The overlay currently reports the track count or an explicit
  unavailable state.
- No real device protocol or recorded-file replay transport exists; adapters
  accept native reports pushed through their input feature. AIS NMEA/radar
  protocol selection is a separate decision (section 9 of the master plan).
- Navigation (M1-F) can reuse `TrackReplica` for CPA/TCPA targets.
- The relay discards observations in flight when its module stops for a
  reload; it does not replay them.
- Core.Sonar remains an empty module until depth/bathymetry is scoped.

## 17. Requirement-to-test ledger (M1-E rows)

| Requirement | Acceptance test or suite | Verification |
|---|---|---|
| M1-E E1: Invalid observations are rejected with a reason | `InvalidObservations_AreRejectedWithAReason` (17 cases) in [TrackObservationRulesTests](../../../../Tests/Shared/Contracts.Tests/TrackObservationRulesTests.cs); `InvalidObservations_AreRejectedWithoutCreatingTracks` in [TrackManagerTests](../../../../Tests/Core/CoreTracks.Tests/TrackManagerTests.cs) | Failed before implementation (rules returned no problem), then passed on Windows, 2026-10-26 |
| M1-E E2-E4, E7: Association, ordering, measurement-only tracks, capacity | `FirstObservation_CreatesTrackWithGeneratedId`, `RepeatedObservation_UpdatesTheSameTrackAndKeepsTheName`, `SameTargetIdFromDifferentSources_OrNearbyPositions_StaySeparateTracks`, `ExactRepeatIsDuplicate_AndOlderObservationIsOutOfOrder_WithoutChangingTheTrack`, `SequenceReset_WithNewerMeasuredTime_IsAccepted`, `BearingOnlyObservation_CreatesPositionlessTrackRetainingTheMeasurement`, `NewTracksBeyondCapacityAreRejected_ButExistingTracksStillUpdate` in [TrackManagerTests](../../../../Tests/Core/CoreTracks.Tests/TrackManagerTests.cs) | Failed before implementation (`NotImplementedException`), then passed on Windows, 2026-10-26 |
| M1-E E5, E6: Lifecycle ageing, removal as a change, source disconnect | `Tracks_AgeToStaleAndLostThenAreRemoved_AndUpdatesReviveThem`, `RemovalIsAFeedChange_AndReusedTargetIdAfterRemovalGetsANewTrackId`, `DisconnectedSource_HoldsItsTracksAtStaleUntilItResumes`, `ObservationFromADisconnectedSource_ReconnectsItAndRevivesTheTrack` in [TrackManagerTests](../../../../Tests/Core/CoreTracks.Tests/TrackManagerTests.cs) | Failed before implementation (`NotImplementedException`), then passed on Windows, 2026-10-26 |
| M1-E E8, E9: Gapless feed, resync rules, checkpoint and restore | `Feed_OffersGaplessOrderedChanges_ThatConvergeWithTheSnapshot`, `Feed_RequiresResyncForUnknownEpochFutureRevisionOrTrimmedLog`, `Feed_LimitsBatchSize_AndTheCursorContinuesWithoutGaps`, `Checkpoint_PreservesEpochRevisionAndTrackIds_AndReobservationUpdatesTheRestoredTrack`, `Restore_RejectsUnsupportedOrCorruptState` in [TrackManagerTests](../../../../Tests/Core/CoreTracks.Tests/TrackManagerTests.cs) | Failed before implementation (`NotImplementedException`), then passed on Windows, 2026-10-26 |
| M1-E E10: Tracks declares and contributes both points, ages on its own timer, checkpoints | [TracksModuleTests](../../../../Tests/Core/CoreTracks.Tests/TracksModuleTests.cs); shared `StopBeforeStart_IsSafeAndIdempotent` | Timer and checkpoint tests failed before implementation; the manifest tests were written with the module and passed on first run. Passed on Windows, 2026-10-26 |
| M1-E E11: Bounded relay (QueueFull), ordering with health, prompt stop and restart, pump not blocked by the lifecycle flag; polar projection | [ObservationRelayTests](../../../../Tests/Shared/Contracts.Tests/ObservationRelayTests.cs), [GeodesyTests](../../../../Tests/Shared/Contracts.Tests/GeodesyTests.cs) | Written against throwing stubs; the failing run was not captured separately. Passed on Windows, 2026-10-26 |
| M1-E E12, E13, E15: AIS and Radar normalization and adapter lifecycle | [AisAdapterTests](../../../../Tests/Core/CoreAIS.Tests/AisAdapterTests.cs), [RadarAdapterTests](../../../../Tests/Core/CoreRadar.Tests/RadarAdapterTests.cs) | Written against throwing stubs; the failing run was not captured separately. Passed on Windows, 2026-10-26 |
| M1-E E14: Core.Sonar is not a track source | `CoreSonar_IsNotATrackSource` in [CoreSonarModuleTests](../../../../Tests/Core/CoreSonar.Tests/CoreSonarModuleTests.cs); shared `SensorDependency_IsTracksOnly` | Requirement changed by the user mid-slice (sonar was briefly implemented as a source, then removed); passed on Windows, 2026-10-26 |
| M1-E E16, E17: Real runtime AIS/Radar to feed, source health, reload keeps IDs and cursors, Blocked without Tracks and recovery | [TrackPipelineIntegrationTests](../../../../Tests/Core/CoreTracks.Tests/TrackPipelineIntegrationTests.cs) | Passed on first run on Windows, 2026-10-26 |
| M1-E E18: Replica resync/unavailable states and the Display tracks overlay | [TrackReplicaTests](../../../../Tests/Shared/Contracts.Tests/TrackReplicaTests.cs) (failed against stubs, then passed), [TrackLayerTests](../../../../Tests/Core/CoreDisplay.Tests/TrackLayerTests.cs), `AisInput_ReachesTheShellViewportThroughTracksAndDisplay` in [ShellViewportIntegrationTests](../../../../Tests/Core/CoreUI.Tests/ShellViewportIntegrationTests.cs) | Display and end-to-end tests passed on first run. Passed on Windows, 2026-10-26 |
