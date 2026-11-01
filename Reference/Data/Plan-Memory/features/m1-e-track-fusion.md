# M1-E: Track fusion and queue ingress (rework)

Milestone: M1-E rework (prerequisite: the M1-E track pipeline as built)
Status: **Plan approved by the user on 2026-10-26, including the amendment
moving bearing tracks into CoreTracks (21.12, R16a and R33 to R37), and
amended again on 2026-10-28 for multi-sensor-channel modules (21.13, D10).
Slices 1 to 5 are accepted (slice 5 on 2026-10-29) and committed
(`ee0e486`). Slice 6 (close-out) is done — full solution run, the
consolidated requirement-to-test ledger and known limits, and the slice 5
PR's cross-platform CI all green — and presented to the user for review and
acceptance.** An earlier
implementation attempt of the whole rework was reverted at the user's
request. `CoreAIS` and `CoreRadar` are rewired onto `TrackSourceConnector`/
`ITrackPublisher` (real registration, retried in the background until
Tracks is available); `ObservationRelay` and `ITrackIngestion` (as described
in [m1-e-track-pipeline.md](m1-e-track-pipeline.md)) are removed entirely,
with no compatibility shim. Everything else reflects slices 1 to 4.

This file holds the track system requirements confirmed on 2026-10-26 (former
section 20) and the implementation plan for the fusion rework (former section
21). It follows [development-workflow.md](../development-workflow.md): slice 2
is at its Step 2 gate, waiting for the user to accept the tests before Step 3
(code) starts. Former section numbers are kept in headings so older references
remain traceable (see the section map in the master plan).

The reworked contract draft written before the revert is kept in this
repository in
[m1-e-track-fusion-contracts-draft.md](m1-e-track-fusion-contracts-draft.md).
Differences between that draft and the plan below: the draft drops the
per-report sequence number (duplicates are detected by measured time and
position) and the contribution's last sequence, and it adds per-source statistics
and system health as described in 21.2.

CMS-only parts of these requirements are also summarized in
[cms-backlog.md](cms-backlog.md); CMS implementation is not planned until the
civilian side is complete.

## 20. Track system requirements: Core and CMS (confirmed 2026-10-26)

Raised while reviewing M1-E; these supersede conflicting statements in the
M1-E track pipeline file and the sensor-to-track proposal.

### Confirmed requirements

| ID | Requirement |
|---|---|
| T1 | `CoreTracks` is the single track management system for both the ECDIS (civilian) and CMS products and behaves the same for both. `CmsTracks` expands it through extension points rather than replacing or duplicating it |
| T2 | CMS adds track sources through its own adapters: fire-control radar, surveillance radar, EW, military sonar, and EO/IR tracking with a laser range finder. Civilian `Core.Sonar` stays out of tracking; `CMS.Sonar` is a track source |
| T3 | Cross-sensor correlation and fusion live in `CoreTracks`. A report that does not correlate with an existing track creates a new track; otherwise it is fused into the existing track. Civilian use fuses AIS and radar |
| T4 | A track number never changes while the track lives. It is discarded (and its number retired) only after it has lost all contributing sensors and stayed stale long enough |
| T5 | `CoreTracks` also owns motion prediction between updates, the shared track picture across consoles, and non-geodetic sources with an estimator. Civilian sources of non-geodetic data still need investigation |
| T6 | CMS-only data in `CmsTracks`: identity (Hostile, Suspect, Neutral, Friend, Assumed Friend, Unknown), classification by domain (subsurface, surface, air; for example Possub 1-4), and link contribution/ownership. Higher update rates are not CMS-only: they benefit civilian use too and are a `CoreTracks` goal as long as hardware cost stays reasonable |
| T7 | Core and CMS tracks share `CoreDisplay` for now; a `CmsDisplay` module may extend it later if CMS-specific features need it |
| T8 | Stale, lost and removal timings are per source (radar needs far shorter than AIS), not one global policy. Each source module registers its expected update interval and timings when it registers with Tracks |
| T9 | The 10,000 track cap stays until performance tuning shows a need |
| T10 | Sensor feeds go directly into track management: each sensor publishes its reports to a queue that Tracks consumes, so several sensors can publish at the same time without blocking each other or the track manager. Most radars run their own tracker and report their own target numbers, which become source-local track IDs |
| T11 | A module may own several physical sensors of the same kind (for example a ship with two navigation radars, or two echo sounders). Each physical sensor is its own source to `CoreTracks`, with its own descriptor, timings and track correlation identity; a module discovers its own sensor inventory from a ship-specific hardware configuration it reads at startup, not from `module.json` (module identity, version and contributions stay static and build-time, per the existing rule) |

### Effect on the M1-E implementation as built

- **Association** (E2) is keyed by source plus source-local ID and the earlier
  plan said never to merge nearby sources. T3 reverses that: a managed track
  needs a list of contributing source tracks, and a correlator decides whether a
  new report joins one. AIS reports carrying an MMSI are a strong identity key;
  radar reports have only radar-local numbers and need gating.
- **Track numbers** (T4) are already generated by Tracks and are never reused,
  but today each (source, target) pair owns a track. Under fusion a track
  outlives any one contribution, and ageing must work per contribution: the
  track is Lost only when every contribution is stale.
- **Lifecycle timings** (E5, T8) become source-specific.
- **Observations** need what fusion requires: position uncertainty (already
  optional) made effectively mandatory for weighting, a sensor kind, and the
  expected update interval, so timings and weights can be derived.
- **Contracts** gain extension room for CMS data (identity, classification,
  ownership) without CoreTracks knowing CMS types. Candidate: a typed extension
  bag per track keyed by namespaced ID, contributed through a CmsTracks point.
- The `Tracks` manifest and the feed contract stay the base that CmsTracks
  builds on.
- **Multiple sensors of one kind** (T11): association already keyed by source
  plus source-local ID must use a source ID per physical sensor, not per
  module, or two physical radars that both hand out "target 7" would be
  treated as updates to the same source-local track. A module that owns
  several channels (CoreRadar's navigation radars today) registers one
  descriptor per channel and routes each incoming report to the right one by
  a channel identifier carried on the report. See 21.13.

### Questions asked (answered 2026-10-26; see the decisions below)

1. **Correlation method.** Gating on position and kinematics (distance,
   speed and course agreement) is the usual start. Is a simple nearest-neighbour
   gate acceptable for the first version, or is probabilistic (Mahalanobis or
   JPDA-style) association required? What happens when a report fits two tracks?
2. **Merge and split.** If two tracks later prove to be one target, which number
   survives (proposal: the older)? Can an operator force a merge or split?
3. **Fusion estimate.** Proposal: a constant-velocity Kalman filter weighted by
   each source's reported uncertainty, which also provides motion prediction
   between updates (T5). Is that the intended scope?
4. **Link contribution/ownership (T6).** Does this mean tracks received over a
   tactical data link from other platforms (for example Link 11/16), including
   which unit owns or reports a track? How do link tracks correlate with local
   ones?
5. **Feed (T10).** Higher update rates (CMS) and a shared picture across
   consoles argue for push streaming, but hot reload forbids retaining plugin
   instances. Option: a host-owned subscription broker with bounded
   per-subscriber queues, so consumers hold host handles, not Tracks objects.
   Do you want that explored, or a faster-polling hybrid first?
6. **Per-source timings (T8).** Proposal: each source declares an expected update
   interval and Tracks derives stale/lost/remove as multiples with sensible
   caps (AIS anchored targets report every 3 minutes; a radar scan is seconds).
   Or should the timings be a configurable table by sensor kind?
7. **Non-geodetic sources (T5).** Which civilian sources do you have in mind
   (for example AIS-less ARPA targets without own-ship pose)? The estimator needs
   own-ship state from Navigation.
8. **Sequence.** Should the M1-E rework (correlation, per-source timings, feed)
   precede CMS work, with CmsTracks starting only after CoreTracks fusion
   passes its tests?

### Decisions (answers of 2026-10-26)

| ID | Decision |
|---|---|
| D1 | **Correlation.** Nearest-neighbour gate on position and velocity (Mahalanobis, 99% for 2 degrees of freedom), with a probabilistic fallback when several tracks fit a report: the report joins a track only when its normalized likelihood is dominant (starting threshold 0.75), otherwise it starts a new track. This is a starting point and deserves a closer look |
| D2 | **Merge and split.** The older track number survives a merge. Operators can force a merge or a split of a track's contributions |
| D3 | **Fusion estimate.** Constant-velocity Kalman filter per track, weighted by each source's uncertainty, also used for prediction between updates. Documented as a starting point to revisit if testing shows another method is needed |
| D4 | **Link contribution (CMS, documented only).** Tracks from Link 11, Link 16 and Link 22 are one more source. When a reported track correlates with a local one, the link data is added like any other source, but the identity and classification the reporting unit sends override local values and the operator cannot change them until the link contribution is lost |
| D5 | **Feeds.** Sensors publish their reports to queues owned by Tracks, and Tracks consumes them (T10, refined 2026-10-26). Because most radars track internally, a source report is a source track with its own ID, and Tracks correlates source tracks to system tracks |
| D6 | **Per-source timings.** Sources register their expected update interval and timings when they register. Example: a radar derives its interval from the antenna rotation rate (30 rpm, 60 rpm, and so on) and uses 5 s, 10 s and 15 s |
| D7 | **Non-geodetic sources.** Unknown whether any civilian ones exist. This must be searched and verified before deciding whether the estimator belongs in Core or CMS only. Searched on 2026-10-26; the findings and the proposed answer are in 21.11 |
| D8 | **Sequence.** Civilian side first. CmsTracks requirements are documented but not implemented until work on the CMS side starts, after the civilian side is complete |
| D9 | **Bearing tracks in Core, estimator in CMS** (2026-10-26, after the search in 21.11). Bearing tracks move back into CoreTracks, because civilian search-and-rescue direction finders produce bearings. The estimator that ranges a target from bearings over time stays in CmsTracks. The amendment is in 21.12 |
| D10 | **Multiple sensors per module** (2026-10-28). A module's hardware inventory (which physical sensors it owns, and each one's settings) comes from a ship-specific configuration the module reads at startup, delivered by the host without the host interpreting it, not from `module.json`. Each configured channel registers as its own source with Tracks. CoreRadar's navigation-radar channels are the concrete case for M1-E; CoreSonar gets the same mechanism for its own civilian channels but wiring them is not in scope here, since civilian `Core.Sonar` is not a track source (T2). The amendment is in 21.13 |

### Design (starting point)

- **Contracts.** `tracks.publisher` (single, `ITrackPublisher`) and
  `tracks.operations` (single, `ITrackOperations`: merge, split) are
  Tracks-owned points beside `tracks.feed`. A source registers a
  `TrackSourceDescriptor` (source ID, kind, expected update interval, stale,
  lost and removal timings, default position and velocity uncertainty) with the
  publisher and receives an `ITrackSink` to publish batches, source health and
  descriptor updates into queues owned by Tracks.
- **Publishing.** Each source obtains its sink from a background task (feature
  calls are blocked inside lifecycle methods) once Tracks is available, and
  drops it when it stops. A generation reload stops every module before any
  restarts, so a sink cannot outlive the Tracks that issued it. See 21.9 for
  the queue design.
- **Correlation.** A report for a known source track updates its contribution
  without re-gating. A report for a new source track is gated against tracks
  that have no active contribution from the same source (two simultaneous
  reports from one source are by definition different targets). Correlated: the
  contribution joins the track. Ambiguous or outside the gate: a new track.
- **Track numbers.** Tracks keep their number for life. A track ends only when
  all its contributions have been removed by their sources' timings.
- **Estimate.** Per track, a local east/north frame, state of position and
  velocity, acceleration noise as process noise, sequential scalar updates for
  position and velocity. Velocity is reported once its uncertainty is small.
  `TrackMotion.Predict` extrapolates a track to a later time.
- **Operators.** Merge keeps the older number and moves the other track's
  contributions to it. Split moves one contribution to a new track; both tracks
  are re-estimated from their remaining contributions' latest reports.
- **Consumer feed.** Display and Navigation keep the replica of the pull feed
  (snapshot plus ordered changes) for now; the polling interval is
  configurable. Revisit if display latency needs it.

### Still open

1. Identity/classification extension shape in `CmsTracks` (documented only).
2. Whether civilian non-geodetic sources exist (D7): searched on 2026-10-26.
   They do exist, and the findings in 21.11 are waiting for the user to confirm
   what follows from them.
3. Tuning of every starting value: gates, the 0.75 dominance threshold, process
   noise, per-source timings (AIS starts at 4, 6 and 10 minutes because
   anchored vessels report every 3 minutes), the 200 m join and split distance
   and the declared peak report rates. The user confirmed on 2026-10-26 that
   all of these are starting points, tuned later against real data (21.10).

## 21. Implementation plan: M1-E rework for fusion

Status: approved by the user on 2026-10-26; Step 2 (tests) has not started.
Scope is the civilian side (CoreTracks, AIS, Radar, consumers); CmsTracks is
documented in section 20 and in [cms-backlog.md](cms-backlog.md) and is not
touched.

### 21.1 Requirements (each becomes a named acceptance test before code)

| ID | Requirement |
|---|---|
| R1 | Invalid reports are rejected with a reason (carried over from E1); a report without a source-local target ID is rejected |
| R2 | A report for an unknown source track that correlates with no track creates a track with a Tracks-generated number; a report for a known source track updates that contribution and its track |
| R3 | A new source track whose position and velocity fall inside the gate of exactly one candidate track joins it (the track now lists both contributions and keeps its number) |
| R4 | A new source track outside every gate creates a new track |
| R5 | When several candidates fit, the report joins the best one only if its normalized likelihood is at least the dominance threshold; otherwise it creates a new track |
| R6 | A track never gains a second active or stale contribution from the same source through correlation; two simultaneous reports from one source stay separate tracks even when close |
| R7 | A track with several contributions splits automatically when its contributions' positions disagree by more than the split distance (the same value as the maximum distance of the nearest-neighbour join gate) for the confirmation count of consecutive updates. The group holding the first contributor keeps the track number and every other group becomes a new track. If the first contributor drops out, the next contributor to have joined becomes the first. This applies equally to operator-merged tracks: a merge never prevents a later split. A transient disagreement does not split |
| R8 | The fused position, speed, course and uncertainty come from a constant-velocity Kalman filter: a more certain source pulls the estimate harder, the uncertainty is lower than any single source's, and velocity is reported only once its uncertainty is small |
| R9 | A late report (measured earlier than the current estimate) from another source is applied at the current estimate time without breaking the filter |
| R10 | Exact repeats are `Duplicate` and older reports `OutOfOrder` per source track, without changing the track (carried over from E3) |
| R11 | Each source registers a descriptor (kind, expected interval, stale/lost/remove timings, default uncertainties); a source can update its descriptor later and the new timings apply immediately. Unregistered sources use the policy defaults |
| R12 | Ageing is per contribution using its source's timings. A track is Active while any contribution is Active, Stale while none is Active but one is Stale, Lost when all are Lost |
| R13 | A contribution is removed when its source's removal time passes. A track is removed (and its number retired, never reused) only when its last contribution is removed. A track that outlives one source keeps its number |
| R14 | A disconnected source holds its contributions at Stale and they are not lost or removed; ageing restarts when the source resumes or reports again (carried over from E6, per contribution) |
| R15 | A new source track from a source whose earlier contribution to a track is Lost may join that track and replaces the Lost contribution |
| R16a | (Replaces R16 by the amendment in 21.12, D9.) A report gives either a position, a range and bearing, or a bearing only. A range and bearing is converted to a position from the own-ship position in the report (and heading, for a relative bearing). A bearing only is applied as a bearing (R33 to R37). A relative bearing without own-ship heading, any polar report without own-ship position, and a report with none of the three are rejected with a reason |
| R17 | Operators can merge two tracks: the older number survives, contributions move to it, the other track is removed and the merged estimate combines both, and later reports from either source track update the survivor |
| R18 | Operators can split one contribution off a track: it becomes a new track with a new number, both tracks are re-estimated from their remaining contributions' latest reports, and splitting the last contribution fails. Merge and split report failure with a reason for unknown tracks or contributions |
| R19 | New tracks beyond the capacity are rejected with `CapacityExceeded`; contributions to existing tracks still update (carried over from E7) |
| R20 | The consumer feed offers a snapshot and gapless ordered changes with resync rules, now including contributions (carried over from E8) |
| R21 | Checkpoint and restore preserve epoch, revision, track numbers, estimator state, contributions, descriptors and source health; reports after restore update the restored tracks and correlate against them (E9 extended, schema 2) |
| R22 | `TrackMotion.Predict` extrapolates a track's position to a later time from its fused velocity and returns nothing when velocity is unknown |
| R23 | A source registers with the Tracks publisher from a background task once Tracks is available, publishes without waiting for Tracks, and drops its sink when it stops; before registration its input is refused as `Unavailable` and counted |
| R24 | A source without Tracks, or Tracks without sources, runs correctly; a source added or removed by a reload is picked up or dropped without restarting Core |
| R25 | AIS registers its descriptor and converts reports as before; Radar derives its interval from the antenna rotation rate and registers 5, 10 and 15 s (scaled up only when the antenna is slow); the rotation rate can be changed at run time |
| R26 | Overload is bounded and reported: each source has a bounded queue in Tracks sized from the peak report rate the source declares in its descriptor (at least 4,096 reports, otherwise ten seconds of peak), which drops its oldest report when full and counts it. While any queue is dropping, the track management system itself (not the sensor) reports Degraded, and returns to Healthy after 10 seconds without drops. `CapacityExceeded` bounds tracks. Neither grows memory without limit |
| R27 | Display and other consumers keep working against the reshaped `Track` (contributions list), including the replica and the tracks overlay |
| R28 | Real-runtime end to end: AIS and radar reports of one vessel fuse into one track, the number survives reload, a source disappearing keeps the track alive on the other source, and removing Tracks blocks the sources and recovers on return |
| R29 | Several sources publishing concurrently are all processed: per-source order is preserved, no report is lost below the queue limit, and no source can starve the others |
| R30 | Processing outcomes are visible per source after the fact: received, created, updated, rejected by reason, dropped, queue depth and the last rejection detail appear in the feed's source status |
| R31 | Higher update rates and congested waters are supported: a measured throughput baseline (proposal: 5,000 reports per second at 1,000 tracks) is recorded by a test, as a measurement and not a pass/fail gate, together with the headroom of the default queue sizes |
| R32 | A radar report is converted to a position from the own-ship position and heading carried in the report when the radar provides them; otherwise it is rejected with a reason. When Navigation exists it becomes the source of own-ship data, and Navigation raises a fault when own-ship position or heading is unavailable (a requirement recorded for M1-F) |
| R33 | A bearing-only report for a new source track that does not correlate with exactly one track (or a dominant one, R5) creates a Bearing track: no position, a true bearing, its uncertainty and the report's own-ship position. It has a Tracks-generated number and the same per-source ageing, removal and number retirement as other tracks (R12 to R14) |
| R34 | A bearing correlates with a Point or Hybrid track when the track's predicted position, seen from the report's own-ship position, lies ahead along the bearing inside the bearing gate. One candidate, or a dominant one: the bearing joins that track as a contribution. Otherwise it stays a Bearing track, which is gated again on every update so it joins automatically once the ambiguity clears; the operator can also merge it. The gate is angular, which suits camera bearings; a source's descriptor may also declare a maximum association range. Bearing association sits behind an internal seam so CmsTracks can supply a different method for EW bearings later. Bearing tracks never correlate with each other in Core: a cross-fix needs bearings taken far enough apart, which one ship cannot do, so it is CMS work |
| R35 | A bearing contribution is supplemental. It refines the fused point only when every Active ranged contribution of the track is AIS, because AIS positions can be less accurate than a bearing. When radar or any other ranged source is Active, the bearing only updates the track's bearing, keeps its contribution alive and takes part in the split check. A bearing that falls outside the bearing gate for the split confirmation count splits off as a Bearing track (R7). Operator merge and split work with bearing contributions like any other |
| R36 | Kind changes keep the track number. A Bearing track whose source track reports a range becomes a Point track. A track whose ranged contributions all stop (Lost, or its source track now reports a bearing only) while a bearing contribution is still Active becomes Hybrid: its point is the last ranged estimate extrapolated by `TrackMotion.Predict`, its uncertainty grows with time, and it is never refined by bearings. A Hybrid track becomes Point again when a ranged report arrives. `TrackMotion.Predict` returns nothing for a Bearing track |
| R37 | Display draws a bearing line from the own-ship position on display along the track's true bearing to the edge of the viewport, for Bearing and Hybrid tracks, in the track's state style. The own-ship position comes from Navigation when Navigation reports one, otherwise from the report. Consumers that need positions, such as Navigation CPA (M1-F), skip Bearing tracks and treat Hybrid points as degraded |
| R38 | A module that owns several physical sensors of the same kind reads its own hardware configuration directly, from a file that stays with the module (not supplied by the host), naming each physical channel it manages. A module given no such file keeps using one implicit default channel, so single-sensor installs and tests are unaffected |
| R39 | Each configured channel registers its own `TrackSourceDescriptor` under its own source ID; a report carries the channel it came from, and the module routes it to that channel's sink. A report naming a channel the module has not configured is rejected as `Invalid` and counted, never applied to the wrong channel |
| R40 | CoreRadar's navigation-radar channels (for example two independently stabilized units) are the concrete case of R38/R39 for M1-E: each gets its own descriptor, antenna rotation rate and source ID. `CoreSonar` is given the same configuration-delivery mechanism for its own civilian channels (echo sounder, NDS sonar), but wiring them to Tracks is out of scope here, since civilian `Core.Sonar` is not a track source (T2) |

### 21.2 Contract changes (`Poseidon.Contracts`, namespace `Poseidon.Core.Modules`)

- **Remove** `ITrackIngestion` and the `tracks.ingestion` point (unreleased, so no
  compatibility shim). `ObservationResult`, `ObservationOutcome` and
  `RejectionReason` stay.
- **Add** `TrackSourceKinds` (string constants `ais`, `radar`; strings so CMS can
  add kinds), `TrackSourceDescriptor` (source ID, kind, expected interval,
  stale/lost/remove, default position uncertainty metres, default velocity
  uncertainty m/s, expected peak reports per second for queue sizing, with
  validation: positive and increasing timings),
  `ITrackPublisher` (`Register(descriptor)` returning an `ITrackSink`, or a
  refusal) and `ITrackSink` (`Publish(batch)` returning an accepted/dropped
  count, `ReportHealth`, `UpdateDescriptor`, `Dispose`).
- **Add points** `tracks.publisher` (single, `ITrackPublisher`) and
  `tracks.operations` (single, `ITrackOperations`); `tracks.feed` stays.
- **Reshape position-less support into bearings** (amended by 21.12):
  `TrackObservation.Measurement` becomes a polar measurement with an optional
  range, the bearing, its reference, the bearing uncertainty and the own-ship
  pose (`PolarMeasurement`, `BearingReference` and `SensorPose` are reshaped,
  not removed). `Track.LatestMeasurement` is replaced by a track kind (`Point`,
  `Bearing`, `Hybrid`) and a nullable bearing (true bearing, uncertainty, the
  report's own-ship position, measured time); position is null exactly when the
  kind is `Bearing`. A contribution records whether it is ranged or
  bearing-only. The descriptor gains a default bearing uncertainty and an
  optional maximum association range. `TrackSourceKinds` gains `camera` and
  `direction-finder`.
- **Extend `SourceStatus`** with per-source statistics (R30), and add a track
  system health (`Healthy` or `Degraded` with a detail) to the snapshot and as a
  change kind, for overload (R26).
- **Reshape `Track`**: drop `SourceId`, `SourceTargetId`, `LastSequence`; add
  `Contributions` (`TrackContribution`: source ID, source-local ID, state,
  joined, measured and updated times, last sequence, update count).
  `Track` gets explicit value equality so contribution lists compare by
  content.
- **Add** `ITrackOperations` and `TrackOperationResult`, and
  `TrackMotion.Predict(Track, DateTimeOffset)`.
- **Remove `ObservationRelay`** from Contracts: the queue now lives in Tracks.
  `TrackReplica` is unchanged apart from the new `Track` shape.
- `SensorPoints` drops nothing (the `*.input` points stay) and `IRadarInput`
  gains `SetAntennaRotationRate`.
- **No host change.** Hardware configuration is read by each module itself,
  from a file deployed alongside it, not delivered or parsed by the host; the
  host stays exactly as unaware of sensor channels as it already is of every
  other module-owned concept (R38).
- **Add** `ChannelId` (string) to `RadarTargetReport`, naming the physical
  radar the report came from; a module with no configuration file treats
  every report as the one implicit default channel (R38 to R40).

### 21.3 Module changes

- **CoreTracks.** `TrackManager` is rewritten (correlator, estimator, per
  contribution lifecycle, automatic split, operations, checkpoint schema 2) and
  split into small internal files: `TrackManager`, `TrackEstimator` (frame and
  filter), `TrackCorrelator`, `TrackIngress` (publisher, per-source queues and
  the consumer), `TrackPolicy`. `TracksModule` exposes `tracks.publisher`,
  `tracks.feed` and `tracks.operations`, runs the consumer thread and the ageing
  timer, and completes the queues on stop. Manifest: points `tracks.publisher`,
  `tracks.feed`, `tracks.operations`.
- **CoreAIS / CoreRadar.** Each keeps its `*.input` point, runs a connector task
  that registers with the Tracks publisher (retrying until Tracks is
  available), normalizes reports and publishes them, and drops the sink on stop.
  They have no queue of their own. Radar adds the rotation-rate setting and now
  rejects reports without a sensor pose. CoreRadar also reads its own hardware
  configuration file, found next to its own assembly (the module locates it
  itself; no host API is involved), listing the navigation-radar channels it
  manages (R38). It runs one connector/registration per configured channel,
  each with its own descriptor, rotation-rate setting and source ID (R39,
  R40); with no configuration file present it keeps today's single implicit
  channel. CoreAIS stays single-channel (one receiver assumed) and reads no
  such file.
- **CoreDisplay.** Only the new `Track` shape (the status line reads the track
  count and position presence).
- **CoreSonar.** Gains the ability to read its own hardware configuration file
  the same way (R38, for its own civilian channels: echo sounder, NDS sonar),
  but wiring those channels to anything is a later `CoreSonar` feature, not
  touched here; civilian `Core.Sonar` is not a track source (T2).
- **CoreUI, Core.** No change.

### 21.4 Starting parameters (internal `TrackPolicy`, tunable)

| Parameter | Starting value | Note |
|---|---|---|
| Process noise (acceleration) | 0.3 m/s² | Ships; revisit with real data |
| Position gate | 9.21 (chi-square, 2 dof, 99%) | On innovation covariance |
| Velocity gate | 9.21 | Applied only when both sides have velocity |
| Dominance threshold | 0.75 | Normalized Gaussian likelihood among gated candidates |
| Velocity reported when | one-sigma speed uncertainty at most 3 m/s | Avoids reporting unconverged velocity |
| Initial velocity uncertainty | 10 m/s without a velocity report | |
| AIS descriptor | interval 10 s; stale 4 min, lost 6 min, remove 10 min; position 10 m, velocity 0.5 m/s | Anchored vessels report every 3 min |
| Radar descriptor | interval 60 / rpm; stale max(5 s, 2 x interval), lost 2 x stale, remove 3 x stale (5, 10, 15 s at 24 to 60 rpm); position 30 m, velocity 2 m/s | Radar default 24 rpm |
| Unregistered source | stale 60 s, lost 180 s, remove 600 s; position 50 m, velocity 5 m/s | Previous global defaults |
| Maximum join distance | 200 m | Absolute cap on the nearest-neighbour join gate, in addition to the 9.21 uncertainty gate; also the split distance (R7). Confirmed as the starting value on 2026-10-26; not yet checked against real data, tuned later |
| Split confirmations | 3 consecutive updates of the diverging contribution | Avoids splitting on a transient |
| Capacity, change log | 10,000 tracks, 10,000 changes | Unchanged |
| Source queue | max(4,096, 10 x declared peak rate) reports per source, drop oldest, 128 per turn | AIS declares 1,000 reports/s and radar 500 reports/s for congested waters. Confirmed as starting values on 2026-10-26; no real data to check them against yet, tuned later with measured rates |
| Degraded recovery | 10 s without drops | Track system health |
| Bearing gate | 6.63 (chi-square, 1 dof, 99%) | On the bearing innovation: bearing variance plus the track's cross-bearing position variance divided by range squared (21.12) |
| Bearing association range | None by default | Angular gate only, which suits cameras; a descriptor may declare a maximum. EW bearings need a different method (CMS) |
| Bearings refine the point | Only when every Active ranged contribution is AIS | Bearings are supplemental (R35) |
| Direction finder bearing uncertainty | 5 degrees (one sigma) | The order of the RT-500-M's quoted accuracy |
| Hybrid point uncertainty growth | The filter's process noise, applied in prediction | The point grows less certain without a range |
| Source connector retry | 200 ms | Sensors registering with the publisher |
| Default radar channel (no configuration file) | Source ID `Core.Radar`, channel ID `default` | Preserves today's single-radar behavior unchanged |
| Configured radar channel source ID | `Core.Radar.<ChannelId>` | For example `Core.Radar.Nav1`; the channel ID is deployer-chosen, not an enum (21.13) |

### 21.5 Sequencing (each slice ends green on Windows; CI after the last)

Each slice is a unit of work that, under the workflow, is planned here, tested
(Step 2) and then coded (Step 3), with a gate after each step.

1. **Contracts.** Descriptor and source kinds with validation, the polar
   measurement extended with own-ship pose and bearing uncertainty and its
   validation (R16a), and `TrackMotion.Predict`. Tests: descriptor rules, polar
   measurement and bearing validation, `Predict` (including nothing for a
   track without a position). Moved out while writing the slice 1 tests
   (2026-10-26), because each slice must end green: the `Track` reshape (kind,
   bearing, contributions) goes to slice 3 with the manager rewrite, and the
   removal of `ObservationRelay` and `ITrackIngestion` goes to slice 4 with the
   publisher that replaces them. Their consumers change in those slices anyway.
2. **Estimator.** Frame conversion and the Kalman filter alone, with numeric
   tests (convergence, weighting, late updates, rebasing far from the origin),
   plus the scalar bearing update and hybrid extrapolation.
3. **Manager.** Correlator (including bearing association), contributions,
   lifecycle, kind changes, automatic split, operations, checkpoint. The test
   file is rewritten around R1 to R21 and R33 to R36; superseded E-tests are
   removed, not patched.
4. **Tracks ingress, module and sources.** Publisher, per-source queues and the
   consumer thread, operations feature, manifests, adapters as publishers (now
   per configured radar channel, R38 to R40), radar rotation rate per channel,
   statistics, the throughput baseline. Tests: R22 to R26, R29 to R31, R38 to
   R40.
5. **Consumers and integration.** Display (including the bearing line, R37),
   replica and the three integration files updated; the real-runtime fusion
   scenario (R27, R28).
6. **Close-out.** Full solution run, ledger rows, documentation of tuning
   values and known limits, then PR and cross-platform CI.

### 21.6 Superseded work

- Superseded and removed: tests E2 ("nearby positions stay separate"), E5, E6 as
  written, `RecordingIngestion`, `ObservationRelay` and its tests, `ITrackIngestion`
  and `tracks.ingestion`, and the ingestion-based integration tests.
- Reshaped (amended by 21.12): the position-less track path becomes the bearing
  path. E4 and the radar and AIS adapter tests that relied on it are rewritten
  against R16a and R33 to R36.
- Kept as is: record shapes for snapshots and changes, `TrackReplica` and its
  pump, geodesy, the validation rules, AIS and radar normalizers, the `*.input`
  points, Display overlay and its tests (reshaped `Track` only).

### 21.7 Risks and edge cases handled by design

- **Wrong fusion is corrected two ways.** Contributions that stop agreeing split
  automatically (R7); the operator can also split or merge (R5, R17, R18).
  Duplicate tracks from ambiguity are fixed by the operator merge.
- **Same-source targets** can never fuse by correlation (R6), which also keeps
  two AIS MMSIs apart.
- **Cross-source late reports** are applied at the current estimate time (R9).
- **Filter far from its frame origin**: the frame re-anchors when the estimate
  drifts about 20 km from it.
- **Threading.** Sources publish from their own threads into thread-safe queues;
  one consumer thread applies reports under the manager lock, and change events
  fire outside it.
- **Reload.** A generation reload stops every module first, so a sink
  reference cannot outlive Tracks; Tracks completes its queues on stop (queued
  reports are discarded and counted) and each source drops its sink when it
  stops.
- **Performance.** Candidate search is a linear scan, only for new source
  tracks (updates are a dictionary lookup). A throughput test records the
  achieved rate at 1,000 tracks so the tuning session has a baseline; a spatial
  index is added only if that test shows a need.

### 21.8 Confirmed by the user (2026-10-26)

1. Ambiguous reports start a new track (R5).
2. Operator merge and split live in Tracks and are exposed as a feature other
   modules call; no UI command in this phase (R17, R18).
3. The starting values in 21.4 are acceptable as a starting point (AIS 4, 6 and
   10 minutes; radar 5, 10 and 15 seconds).
4. The throughput baseline is acceptable as a starting point (R31).
5. `ITrackIngestion` is removed because it is no longer used.
6. Tracks that stop agreeing split automatically; the first contributor keeps the
   track number (R7). An operator merge does not prevent a later automatic split,
   so a wrongly forced merge corrects itself.
7. The split distance is the same as the maximum distance of the nearest-neighbour
   join gate (R7, 21.4).
8. If the first contributor drops out, the next contributor to have joined takes
   its place (R7).
9. Bearing-only reports create bearing tracks (a line from own ship along the
   bearing to the edge of the viewport); only military sensors are expected to
   supply them, so they move to CMS. (Superseded by D9 and 21.12: bearing tracks
   are in CoreTracks.) A sensor that reports range and bearing has
   its position calculated from the own-ship position (R16).
10. Sensors publish to a queue that Tracks consumes (T10, 21.9).

### 21.9 Ingress design: sensors publish, Tracks consumes

Options considered:

| Option | Summary | Verdict |
|---|---|---|
| A | Per-source queue inside each sensor module; Tracks subscribes and drains | Rejected: needs a subscription mechanism and two queues in series |
| B | Queues owned by Tracks; sensors publish into them; one consumer applies reports | Chosen: matches the requirement, producers never block each other or the manager |
| C | A host-owned message bus for all modules | Deferred: more machinery than one consumer needs; revisit when Display, Navigation or CMS need push |

Chosen design (B):

- Tracks exposes `ITrackPublisher.Register(descriptor)`; the call returns an
  `ITrackSink` bound to that source. Registering the same source ID again (a
  restart) replaces the previous sink; two different modules claiming one source
  ID is refused.
- Each source has its own bounded queue inside Tracks. Producers enqueue without
  waiting on the manager. Reports and control items (health, descriptor
  changes) share the queue so their order is kept. When the queue is full the
  oldest report is dropped and counted, because the latest report matters most
  for a track.
- One consumer thread drains the queues round robin in small batches, so no
  source can starve the others, and applies them to the manager. Ageing runs on
  a timer through the same manager lock.
- Reports are stamped with their receipt time when published; track ageing
  uses that time, not the processing time.
- Because processing is asynchronous, the per-report outcome is no longer
  returned to the sensor. Tracks keeps counters per source (received, created,
  updated, rejected by reason, dropped, queue depth, last rejection detail) and
  publishes them in the feed's source status (R30).
- Sensors have no queue of their own. Until a sensor has registered, its input
  is refused as `Unavailable` and counted; the connector retries every 200 ms
  from a background task because feature calls are blocked inside lifecycle
  methods.

Automatic split (R7), starting design:

- Only contributions that are Active or Stale take part. Operator-merged
  contributions take part too: a merge forces contributions together but never
  keeps them together once they disagree, so a mistaken merge corrects itself.
- Each time a contribution of a multi-contribution track updates, its latest
  position, extrapolated to a common time with its own velocity, is compared
  with the latest position of every other contribution. Disagreement means a
  distance above the split distance, which is the same maximum distance used by
  the join gate, so a pair that could not have joined can no longer stay
  together.
- A contribution that disagrees with the others for the confirmation count of
  consecutive updates is split off. The groups are formed from contributions that
  still agree with each other; the group containing the first contributor keeps
  the track number and the others become new tracks with new numbers and
  estimates rebuilt from their own latest reports.
- The first contributor is the contribution that joined earliest. If it is
  removed, the next earliest takes its place.
- Because the split distance equals the join cap, a split contribution can never
  rejoin automatically: it is an existing source track and is only gated again
  if its source drops it and reports it as a new source track.

Bearing tracks (superseded by D9 and 21.12: bearing tracks now belong in
CoreTracks; only the bearings-over-time estimator stays in CMS). As first
written: a bearing track has no position. It
stores the own-ship position and the bearing when the report arrived, and the
display draws a line from own ship along the bearing to the viewport edge.
CmsTracks adds this track kind and its source adapters (EW, EO/IR without a
laser range finder), and CmsDisplay or CoreDisplay draws it. CoreTracks stays
positioned-only.

### 21.10 Answers and remaining questions

Answered 2026-10-26:

- **Own-ship data for range and bearing.** Keep it in the radar report if radars
  can provide it; otherwise it moves to the Navigation module, which handles the
  GPS and is expected to end up as the source. A report that cannot be given an
  own-ship position is rejected, and Navigation must raise a fault when the data
  is unavailable (R32). Many marine radars send only range and bearing and take
  position and heading from separate GPS and compass feeds, so this is likely to
  end up in Navigation. The Radar adapter keeps the pose handling behind one
  internal seam so the switch does not touch correlation.
- **Degraded.** It is the track management system that becomes Degraded when a
  queue drops reports, not the sensor (R26). Queues are sized from each source's
  declared peak rate so normal operation in congested waters does not drop
  anything.

Answered 2026-10-26 (second round):

- **Maximum join distance.** 200 m is kept as the starting value. It is a guess
  from typical radar-to-AIS offsets and has not been checked against real data;
  it is tuned later, when recordings are available (21.4).
- **Peak rates.** AIS 1,000 reports/s and radar 500 reports/s are kept as the
  starting values. No real data has been available to check them; they are tuned
  later with real recordings (21.4).

Remaining:

1. **Navigation requirements** (own-ship provider, fault on missing GPS or
   heading) are recorded here and belong to M1-F.
2. **Non-geodetic sources** (D7): answered by D9. Bearing tracks go into
   CoreTracks, and the estimator stays in CmsTracks. The amendment in 21.12
   is fully answered and folded into the plan.
3. **Navigation own-ship point for Display** (R37): belongs to M1-F.

### 21.11 Civilian non-geodetic sources (D7, searched 2026-10-26)

The question was whether any civilian sensor reports targets other than as a
latitude and longitude, and so whether an estimator for non-geodetic data
belongs in CoreTracks or only in CmsTracks.

Findings:

| Source | What it reports | Civilian | Verified |
|---|---|---|---|
| ARPA radar, NMEA 0183 `TTM` | Target number, distance and bearing from own ship, bearing true (T) or relative (R), target speed and course | Yes, standard on merchant ships | Field layout from the gpsd NMEA reference and an open parser fixture; the normative NMEA standard was not read (paid) |
| ARPA radar, `TLL` and `TTD` | `TLL` gives the target's latitude and longitude (geodetic). `TTD` is tracked target data; its field layout was not found | Yes | `TLL` from the gpsd reference; `TTD` listed by Garmin, fields not verified |
| Camera-based detection (for example SEA.AI, Orca AI) | Detected objects with estimated range, bearing and speed from day, low-light and thermal cameras; complements radar and AIS | Yes, yachts and merchant ships | Product literature only; no output protocol found |
| Forward-looking sonar (for example FarSounder) | Range, horizontal bearing and depth of obstacles ahead, about one update every 1.6 s | Yes | Vendor material; no NMEA target output found. Obstacles, not vessel tracks |
| VHF and emergency-band direction finders (for example Rhotheta RT-500-M) | Bearing only to a transmitter on 121.5, 243 and 406 MHz and marine VHF, sent over NMEA to ECDIS | Yes, sold to civilian search-and-rescue organisations and coastal services | Vendor brochure; sentence layout not found |

What this means:

1. **Range and bearing from own ship is common in civilian use** (ARPA `TTM`,
   cameras). It needs no estimator, only conversion with the own-ship position
   and heading, which R16 and R32 already require. The radar can also send
   geodetic `TLL` when it has GPS input.
2. **Bearing-only civilian sources exist**, but only in search and rescue:
   direction finders on SAR vessels and coastal stations. That corrects decision
   21.8 item 9, which assumed only military sensors produce bearings. The
   civilian practice is to draw the bearing line and to cross-fix bearings
   from several stations. No civilian source was found that needs bearings-only
   target motion analysis (an estimator that ranges a target from bearings over
   time).
3. **Forward-looking sonar** reports range and bearing too, but its targets are
   underwater obstacles reported by civilian Sonar, which is not a track source
   (T2). It stays out of tracking.

Proposed answer (the user decided differently on bearing tracks; see D9 and
21.12):

- The estimator for non-geodetic data (bearings-only motion analysis) stays in
  CmsTracks. CoreTracks stays positioned-only for M1, as planned in R16;
  range-and-bearing reports are converted to positions before correlation.
- Bearing tracks are recorded as not strictly military: a later civilian SAR
  feature with a direction finder would need them. They stay out of M1. When
  they are built, they go in a place both products can use (a CoreTracks
  extension rather than CmsTracks only). That choice is made when the work is
  planned.
- Slice 1 is unchanged: position-less support is removed from the Core
  contracts as planned.

Sources: the [gpsd NMEA reference](https://gpsd.gitlab.io/gpsd/NMEA.html)
(`TTM`, `TLL`, `OSD`), Garmin's NMEA 0183 sentence lists, the
[Rhotheta RT-500-M product page](https://alphatronmarine.com/en/product/rt-500-m-500/),
[FarSounder](https://www.farsounder.com/blog/what-to-expect-from-your-3d-forward-looking-sonar),
[Orca AI](https://orca-ai.io/wp-content/uploads/2024/06/FAQs.pdf) and
[SEA.AI coverage in Maritime Executive](https://maritime-executive.com/features/sea-ai-employs-machine-vision-and-ai-improving-maritime-safety-and-security).

### 21.12 Amendment: bearing tracks in CoreTracks (D9, proposed 2026-10-26)

Status: all questions answered on 2026-10-26 and folded into the plan: R16a
and R33 to R37 are in 21.1, the starting values in 21.4, the contract change in
21.2 and the slice changes in 21.5. This section keeps the reasoning and the
answers. The tables below are the draft as first proposed; where they differ
from 21.1 and 21.4, those win.

Decision (user, 2026-10-26): bearing tracks belong in CoreTracks, so both
products can use them. The estimator that ranges a target from bearings over
time (bearings-only target motion analysis) stays in CmsTracks.

Answers (user, 2026-10-26):

1. **Scope.** M1 adds the bearing track contracts, the manager path and the
   Display line, tested with a test source. No direction-finder module.
2. **Fusion.** A bearing track can fuse with other sensors into a point track.
   When no single track can be chosen, the bearing track stays on its own. It
   is then merged only by the operator, or automatically once the tracks
   causing the ambiguity move out of the way.
3. **Kind change.** A bearing track whose source starts reporting range becomes
   a point track. If the range then stops, it becomes a hybrid track that shows
   the point and a bearing line, because the point can no longer be assumed to
   be accurate.
4. **Line origin.** The bearing line always starts at the own-ship position on
   display. When Navigation is not reporting the own-ship position, the
   position carried in the report is used.

Track kinds:

| Kind | Has | Shown as |
|---|---|---|
| Point | A fused position (and velocity when converged) | Point symbol, as today |
| Bearing | A true bearing, its uncertainty and the report's own-ship position; no position | Line from own ship along the bearing |
| Hybrid | A point that is no longer refreshed by a range, and a current bearing | Point symbol and bearing line |

Proposed requirements:

| ID | Requirement |
|---|---|
| R16a | A report gives either a position, a range and bearing, or a bearing only. A range and bearing is converted to a position from the own-ship position in the report (and heading, for a relative bearing). A bearing only is applied as a bearing. A relative bearing without own-ship heading, any polar report without own-ship position, and a report with none of the three are rejected with a reason |
| R33 | A bearing-only report for a new source track that does not correlate with exactly one track creates a Bearing track with a Tracks-generated number, with the same per-source ageing, removal and number retirement as other tracks (R12 to R14) |
| R34 | A bearing correlates with a point or hybrid track when the track's predicted position, seen from the report's own-ship position, lies ahead along the bearing inside the bearing gate. Exactly one candidate (or a dominant one, R5): the bearing joins that track as a contribution. Several without a dominant one: it stays a Bearing track. A standalone Bearing track is gated again on every update, so it joins automatically once the ambiguity clears. Bearing tracks never correlate with each other in Core |
| R35 | A bearing contribution refines the fused estimate only while the track has an Active ranged contribution (a position, or a range and bearing). Otherwise it updates the bearing and keeps the track alive but never moves the point, so Core never ranges a target from bearings over time. A bearing contribution that falls outside the bearing gate for the split confirmation count splits off as a Bearing track (R7). Operator merge and split work with bearing contributions like any other |
| R36 | Kind changes keep the track number. A Bearing track whose source track reports a range becomes a Point track. A track whose ranged contributions all stop (Lost, or its source track now reports bearing only) while a bearing contribution is still Active becomes Hybrid: its point is the last ranged estimate extrapolated by `TrackMotion.Predict`, its uncertainty grows with time, and it is not refined by bearings. A Hybrid track becomes Point again when a ranged report arrives. `TrackMotion.Predict` returns nothing for a Bearing track |
| R37 | Display draws a bearing line from the own-ship position on display along the track's true bearing to the edge of the viewport, for Bearing and Hybrid tracks, in the track's state style. The own-ship position comes from Navigation when Navigation reports one, otherwise from the report. Consumers that need positions, such as Navigation CPA (M1-F), skip Bearing tracks and treat Hybrid points as degraded |

Starting values (added to 21.4 once approved):

| Parameter | Starting value | Note |
|---|---|---|
| Bearing gate | 6.63 (chi-square, 1 dof, 99%) | On the bearing innovation: bearing variance plus the track's cross-bearing position variance divided by range squared |
| Bearing join distance cap | None | The 200 m cap would stop a 5 degree bearing joining beyond about 2.3 km; ambiguity is handled by the dominance threshold instead |
| Direction finder bearing uncertainty | 5 degrees (one sigma) | The order of the RT-500-M's quoted accuracy |
| Hybrid point uncertainty growth | Process noise of the filter, applied in prediction | The point grows less certain the longer it goes without a range |

Contract changes (replace the "remove position-less support" item in 21.2):

- `TrackObservation` keeps `Position` and replaces `Measurement` with a polar
  measurement that carries the range (optional), the bearing, its reference
  (true or relative), the bearing uncertainty, and the own-ship pose at the time
  of measurement. `PolarMeasurement`, `BearingReference` and `SensorPose` exist
  today and are reshaped rather than removed.
- `Track` gets a kind (`Point`, `Bearing` or `Hybrid`) and a nullable bearing
  (true bearing, uncertainty, the report's own-ship position, measured time).
  Position is null exactly when the kind is `Bearing`. A contribution records
  whether it is ranged or bearing-only.
- `TrackSourceDescriptor` gets a default bearing uncertainty in degrees.
- `TrackSourceKinds` gains `direction-finder`, so a later civilian source has a
  kind to declare.
- Display needs an own-ship position source. Until Navigation exists (M1-F), it
  uses the position in the bearing. M1-F adds the Navigation own-ship point
  that Display prefers.

Effect on the slices and superseded work:

- Slice 1 adds the bearing contract shapes and their validation tests, and no
  longer removes position-less support outright.
- Slice 2 adds the scalar bearing update to the estimator, and the rule that it
  applies only with an Active ranged contribution.
- Slice 3 adds the bearing path in the manager (R16a, R33 to R36).
- Slice 5 adds the Display bearing line (R37).
- 21.6: the position-less path is reshaped into the bearing path rather than
  removed. Tests E4 and the adapter tests that relied on it are rewritten
  against R16a and R33.
- No civilian bearing source module is added in M1 (AIS and Radar do not report
  bearings only). The bearing path is tested with a test source.

Answers to the remaining questions (user, 2026-10-26):

1. **Bearing with bearing.** A cross-fix is not possible from a single ship,
   because the two bearings must originate far enough apart for a proper fix.
   It is a CMS feature. Bearing tracks never correlate with each other in Core.
2. **Bearings refining the point.** Bearings are supplemental data, because
   radar and other sources are more accurate. The exception is AIS, which can
   be less accurate than a bearing. A bearing therefore refines the point only
   when every Active ranged contribution is AIS (R35). A Hybrid point is never
   refined by bearings.
3. **No distance cap.** An angular gate alone works for bearings from cameras,
   but not for bearings from an EW racket. Core uses the angular gate, a source
   may declare a maximum association range, and bearing association sits behind
   an internal seam so CmsTracks can supply an EW method later (R34).

Assumption (not asked): direction-finder bearings use the camera rule, the
angular gate with an optional range limit in the descriptor. To be revisited
if real direction-finder data shows they behave more like EW bearings.

The questions as asked:

1. **Bearing with bearing.** Two bearings from different sources (for example
   two direction finders) crossing on one target is a cross-fix. It is a
   single-moment calculation, not an estimate over time. Proposal: not in Core
   for M1; Bearing tracks stay separate and the operator sees both lines.
   Should Core do the cross-fix instead?
2. **Bearings refining the point (R35).** Proposal: a bearing improves the
   fused position only while another contribution supplies a range, and a
   Hybrid point is only extrapolated, never moved by bearings. Anything more
   would be ranging from bearings over time, which stays in CMS. Is that the
   intended line?
3. **No distance cap for bearings.** Proposal: bearings use the angular gate
   alone, without the 200 m cap, because the cap would block direction-finder
   fusion beyond about 2 km. Is that acceptable?

### 21.13 Amendment: multiple physical sensors per module (D10, approved 2026-10-28)

Status: raised by the user while scoping slice 4, before its tests are
written, because it changes how sources register with the Tracks publisher.
Resolved the same day. R38 to R40 are folded into 21.1, the contract change
into 21.2, the module changes into 21.3, the source-ID convention into 21.4
and the slice 4 description into 21.5. This section keeps the reasoning.

**The problem.** A real ship rarely has exactly one sensor of a kind. Navies
commonly carry two navigation radars, plus a surface-search and an
air-search (or a combined surface/air surveillance) radar; sonar suites mix
an echo sounder with hull-mounted, variable-depth and towed-array units. The
user confirmed the civilian/military split for M1: `CoreRadar` manages only
navigation radars (surveillance and fire-control radars are `CmsRadar`);
`CoreSonar` manages only the echo sounder and NDS sonar (hull-mounted, VDS
and TAS are `CmsSonar`). So even within M1's civilian scope, `CoreRadar` can
own more than one physical unit, and radar target numbers are only unique
within one radar's own tracker: if two physical radars both hand out
"target 7", and both are folded into a single implicit Tracks source
(today's assumption, one descriptor per module), the correlator would treat
them as updates to the same source-local track and could fuse two unrelated
contacts. Civilian `Core.Sonar` is not a track source (T2), so its multiple
channels are a module-internal concern only, not a Tracks correctness issue,
but the same mechanism should serve it too rather than inventing a second one
later.

**Options considered for the sensor's identity:**

| Option | Summary | Verdict |
|---|---|---|
| A | One `module.json` package per physical sensor (for example `CoreRadar-Nav1`, `CoreRadar-Nav2`), same entry assembly, distinct module ID and contribution | Rejected: module identity/version/contributions are meant to stay static and build-time; this makes the manifest set deployment-specific (templated per hull) |
| B | One module, point `radar.input` moved to `Multiple` cardinality, one declared contribution per channel in `module.json` | Rejected for the same reason: the manifest would have to encode a ship's loadout |
| C | One module, one point, reports carry a channel identifier; the module reads its own channel inventory from a file and registers one `TrackSourceDescriptor` per channel | Chosen |

**Options considered for where the channel inventory lives:**

| Option | Summary | Verdict |
|---|---|---|
| 1 | Each module reads its own configuration file directly, kept alongside it, with no host involvement | Chosen (user, 2026-10-28) |
| 2 | One ship-wide configuration file, read by the host and handed to each module through a new `IModuleHost` passthrough, host-opaque | Rejected: the user wants each module's configuration to stay with that module, not centralized in the host |

**Decision.** Each module that can own several physical sensors of one kind
reads its own hardware configuration file directly (found next to its own
assembly; no host API is added or changed). The file lists the physical
channels that module is responsible for. `CoreRadar` is the concrete case for
M1-E: each configured navigation-radar channel gets its own
`TrackSourceDescriptor`, antenna rotation rate and Tracks source ID
(`Core.Radar.<ChannelId>`, 21.4); `RadarTargetReport` carries a `ChannelId` so
the module can route an incoming report to the right channel's sink. A
module given no configuration file keeps one implicit default channel
(source ID `Core.Radar`), so single-radar installs and the existing slice 1
to 3 tests are unaffected. `CoreSonar` gains the same file-reading mechanism
for its own civilian channels, but wiring them to anything is left to a later
`CoreSonar` feature, since civilian `Core.Sonar` is not a track source (T2)
and nothing in M1-E touches it. `CoreAIS` is unaffected: a ship normally
carries one AIS transponder/receiver, and MMSI already identifies a target
independently of which receiver heard it, so AIS keeps today's single
implicit source.

**Authoring tool (noted, not scoped).** Option 2's underlying concern — a
technician configuring a hull's sensor suite wants one place to work, not a
file buried in each module's own deployed folder — still has merit even
though runtime stays module-owned (user, 2026-10-28). The proposed answer is
a separate authoring tool, deployed alongside the application, that edits
every module's configuration file from one screen or command; it writes to
the same per-module files `CoreRadar`/`CoreSonar` read at startup and adds no
new runtime path, so it never has to agree with Tracks or the host about
anything. Not planned or scoped yet; a backlog candidate, tracked here and in
the master plan's follow-on work.

**Settled while writing the slice 4 tests (2026-10-28):** the file is named
`sensors.json`, found beside the module's own assembly; its shape is
`{ "channels": [ { "channelId": "...", "antennaRotationRateRpm": ... } ] }`.
A malformed file (duplicate channel IDs, a non-positive rotation rate) is
reported as `InvalidDataException`, the same way other module start failures
are (for example `RadarModule`'s unsupported checkpoint). A missing file is
not an error: it yields one implicit channel, ID `default`.

**Still open**:

1. Whether a channel's role (for example distinguishing two navigation radars
   by mounting position) needs to be more than a free-form string the
   deployer chooses, the same way `TrackSourceKinds` stays open for CMS to
   extend.
2. The authoring tool itself: scope, where it lives in the repository, and
   whether it is a slice of this feature or its own later feature file.

## Ledger

The ledger for the as-built pipeline is in
[m1-e-track-pipeline.md](m1-e-track-pipeline.md). Rows are added per slice in
Step 2 (failing runs) and Step 3 (passing runs).

### Slice 1 (contracts): Step 2, tests written 2026-10-26

| Requirement | Tests | Failing run (2026-10-26) |
|---|---|---|
| R1 (carried over) | `TrackObservationRulesTests.InvalidObservations_AreRejectedWithAReason` (original cases), `PositionedObservations_AreAccepted_IncludingBoundaryValues` | Pass: existing behavior |
| R11 (descriptor rules) | `TrackSourceDescriptorTests`: `ValidDescriptors_AreAccepted_WithAndWithoutTheOptionalBearingValues`, `Kinds_AreOpenStrings_SoOtherModulesCanDeclareTheirOwn`, `InvalidDescriptors_AreRefusedWithAReason` (20 cases) | 22 fail with `NotImplementedException` (stub `Validate`) |
| R16a (contract rules) | `TrackObservationRulesTests`: 11 new `InvalidObservations_AreRejectedWithAReason` cases (no bearing, no own-ship position, relative bearing without heading, invalid own-ship position or heading, invalid bearing uncertainty); `RangeAndBearing_IsAcceptedWithTheOwnShipPosition_AndRelativeBearingsWithItsHeading`; `BearingOnly_IsAcceptedWithTheOwnShipPosition_AndRelativeBearingsWithItsHeading`; `APositionedObservation_MayCarryTheMeasurementItCameFrom` | The 11 invalid cases fail: today's rules return no reason. The accepting tests pass already; they guard against rejecting too much |
| R16a, R32 (radar) | `RadarNormalizerTests.WithoutAPoseOrWithARelativeBearingAndNoHeading_TheReportIsRejectedWithAProblem` (rewritten from the superseded "only the measurement is kept") | Fails: the normalizer still accepts the report |
| R22 | `TrackMotionTests`: `Predict_MovesAlongTheCourseBySpeedTimesElapsedTime` (3 cases), `Predict_AtTheMeasuredTimeOrWithoutSpeed_ReturnsTheTrackPosition`, `Predict_ReturnsNothing_WhenTheVelocityIsUnknown` | 5 fail with `NotImplementedException` (stub `Predict`) |
| R36 (Predict part) | `TrackMotionTests.Predict_ReturnsNothing_ForABearingTrack` | Fails with `NotImplementedException` |

Totals: Contracts.Tests 39 failed of 103, CoreRadar.Tests 1 failed of 23; every
other test project passes. `dotnet build Core.Application.proj`: no warnings or
errors.

Compile-only production changes: `PolarMeasurement` gains optional `Pose` and
`BearingUncertaintyDegrees`; `TrackSourceKinds` and `TrackSourceDescriptor`
(with a stub `Validate`) are added to `Tracks.cs`; `TrackMotion.cs` holds a stub
`Predict`.

Existing test adjusted so it stays valid: `TrackManagerTests.BearingOnlyObservation_CreatesPositionlessTrackRetainingTheMeasurement`
now gives its bearing an own-ship position (R16a). It is superseded and is
rewritten in slice 3.

### Slice 1 (contracts): Step 3, code written 2026-10-26

The user approved the slice 1 tests, the assumptions below and the sequencing
change on 2026-10-26 by ordering the implementation.

- `TrackSourceDescriptor.Validate` (in `Tracks.cs`) implements R11's rules.
- `TrackObservationRules.Validate` implements R16a: the bearing uncertainty and
  own-ship pose are checked when present. A report without a position must
  have a bearing, the own-ship position, and the heading for a relative
  bearing.
- `TrackMotion.Predict` projects along the course by speed times elapsed time
  with `Geodesy.Project`, returning the position itself when the distance is
  zero.
- The comments on `RadarNormalizer` and `RadarTargetReport` now say a report
  without a usable pose is rejected.

Passing run (2026-10-26, Windows): `dotnet build Core.Application.proj` had no
warnings or errors. `dotnet test Poseidon.slnx` passed 761 of 761 with no
failures: the earlier 677 plus the 42 new slice 1 tests, which the solution
runs twice like every other test. Contracts.Tests passed 103 of 103 and
CoreRadar.Tests 23 of 23. Every slice 1 row above now passes.

Assumed while writing the tests (approved with the implementation order):

- A descriptor's bearing uncertainty is optional (null means the source does
  not report bearings) and must be above 0 and below 180 degrees when given; the
  maximum association range is optional and must be positive and finite.
- A positioned report may also carry the polar measurement it was converted
  from, without its own-ship position (the radar adapter does this today). The
  own-ship requirements apply only when the report has no position.
- `Predict` at the measured time, or with zero speed, returns the track's
  position.

### Slice 2 (estimator): Step 2, tests written 2026-10-27

Design assumed while writing the tests, needed to give the estimator a concrete
shape before any numeric test can be written (the approved plan fixes the
approach in section 21 "Estimate" and the starting values in 21.4; this fixes
the implementation seam):

- A new internal type, `TrackEstimator` (`System/Core/CoreTracks/TrackEstimator.cs`,
  namespace `Poseidon.Modules.Tracks`), holds one track's filter state: the local
  east/north frame's current origin, position and velocity and their
  covariance, and the latest bearing contribution if any. It is constructed
  from an initial position and its uncertainty (R8's starting point, a report
  that already has a position) and is not used for a Bearing track (R33),
  which has no position until a ranged report arrives (slice 3).
- State is `[east, north, veast, vnorth]` with a full 4x4 covariance so a
  bearing update, which couples east and north, can update both from one
  scalar measurement; position and velocity measurements are applied as
  sequential scalar updates (north, then east; north, then east) as the plan
  specifies, each a standard scalar Kalman update (gain, state and covariance)
  against the shared state.
- Process noise between updates uses the continuous white-noise-acceleration
  discretization (`q = processNoiseAcceleration²`, applied per axis as
  `[[dt³/3, dt²/2], [dt²/2, dt]]` on each axis's position/velocity block, no
  cross terms between east and north). The default is the 0.3 m/s² starting
  value (21.4).
- The filter's own clock never moves backward. A call that would advance to an
  earlier time than the filter's current state time predicts zero elapsed time
  instead (R9): a late report is applied as a measurement update at the
  current state time, not rolled back.
- The frame re-anchors (origin becomes the current estimate, position resets to
  the origin, velocity and covariance are unchanged) whenever advancing the
  filter leaves it about 20 km from its origin (21.7), transparently to the
  public surface.
- A bearing measurement is linearized at the current predicted position
  (bearing from the own-ship position to the estimate) and applied as one
  scalar update against the full state, so it can move both east and north
  together; a bearing contribution's effect on velocity is through the same
  covariance, not a separate step. It always updates the recorded bearing
  (`Estimate.BearingDegrees` and related fields, for a Hybrid track's display
  line); it refines position only when the caller passes
  `hasActiveRangedContribution: true` (R35; the AIS-only nuance is the
  manager's decision in slice 3, not the estimator's).
- `TrackEstimate` (a new internal record, not the public `Track`) is the
  estimator's output: position and its uncertainty, velocity and its
  uncertainty once known, and the latest bearing. Mapping it onto the public
  `Track` (including the 3 m/s velocity-reporting threshold and the Hybrid
  kind) is slice 3.

Tests (`Tests/Core/CoreTracks.Tests/TrackEstimatorTests.cs`), against the
internal `TrackEstimator` (visible to the test project via the existing
`InternalsVisibleTo`):

| Requirement | Test |
|---|---|
| R8 (convergence) | `UpdatePosition_RepeatedConsistentMeasurements_ConvergeNearTheMeasurementWithShrinkingUncertainty` |
| R8 (weighting) | `UpdatePosition_WeightsByUncertainty_ATighterMeasurementPullsTheEstimateHarder` |
| R9 (late reports) | `UpdatePosition_ALateMeasurement_IsAppliedAtTheCurrentTime_WithoutBreakingTheFilter` |
| 21.7 (rebasing) | `Estimate_StaysAccurate_AsTheTrackDriftsWellBeyondTheFramesRebaseDistance` |
| R34, R35 (bearing update) | `UpdateBearing_WithAnActiveRangedContribution_NudgesThePositionTowardTheBearingLine`, `UpdateBearing_WithoutAnActiveRangedContribution_RecordsTheBearingButNeverMovesThePosition` |
| R36 (hybrid extrapolation) | `Predict_WithoutMeasurements_GrowsPositionUncertaintyMonotonically_ForHybridExtrapolation` |
| R9 (never rolling backward) | `Predict_ReturnsNothingNewWhenCalledAtOrBeforeTheCurrentTime_NeverRollingBackward` |

Failing run (2026-10-27): all 8 new tests fail with `NotImplementedException`
from the stub `TrackEstimator` (`Estimate`, `Predict`, `UpdatePosition`,
`UpdateVelocity`, `UpdateBearing` all throw; the constructor stores nothing).
`dotnet test Tests/Core/CoreTracks.Tests/CoreTracks.Tests.csproj`: 8 failed of
41 (the other 33, from the as-built manager, module and pipeline tests, are
unaffected). `dotnet build Core.Application.proj`: no warnings or errors.
`dotnet test Poseidon.slnx`: Contracts.Tests, Core.Tests, CoreCharts.Tests,
CoreAIS.Tests, CoreUI.Tests (28/28), CoreDisplay.Tests (18/18),
CoreNavigation.Tests (8/8), CoreRadar.Tests (23/23) and CoreSonar.Tests (8/8)
all pass; the Civilian aggregate's own module-suite rerun
(`RunCivilianModuleSuites`) surfaces the same CoreTracks failure as an
MSBuild MSB4181 (its usual way of propagating an inner suite failure, not a
new problem: the same mechanism would have surfaced slice 1's Contracts.Tests
and CoreRadar.Tests failures through the same target).

### Slice 2 (estimator): Step 3, code written 2026-10-27

The user accepted the slice 2 tests and design and ordered the implementation
on 2026-10-27.

- `TrackEstimator` is implemented exactly to the Step 2 design: the 4-state
  `[east, north, veast, vnorth]` filter with a full 4x4 covariance, the
  continuous white-noise-acceleration process noise per axis, sequential
  scalar position and velocity updates (north then east), the bearing update
  linearized at the current predicted position and gated on
  `hasActiveRangedContribution`, the filter clock that never moves backward
  (a late report predicts zero elapsed time instead), and the ~20 km frame
  re-anchor. `PositionUncertaintyMetres` and `VelocityUncertaintyMetresPerSecond`
  are each axis's combined DRMS figure (`sqrt(Pxx + Pyy)`), not a single axis's
  own sigma.
- Two of the slice 2 tests had a wrong assumption, found while making them
  pass, and were corrected rather than the production code (development
  workflow rule 2: a wrong test is fixed and the reason recorded here, not
  silently adjusted):
  - `UpdatePosition_RepeatedConsistentMeasurements_ConvergeNearTheMeasurementWithShrinkingUncertainty`
    compared the converged combined (DRMS) uncertainty against a single
    report's own one-axis uncertainty (10 m) directly; a DRMS over two axes is
    `sqrt(2)` times any one axis's figure even when each axis individually
    converges tighter than the measurement, so the comparison is now against
    `10 * sqrt(2)`, the single report's own uncertainty expressed the same way.
  - `Predict_WithoutMeasurements_GrowsPositionUncertaintyMonotonically_ForHybridExtrapolation`
    fed one position-plus-velocity report, then dead-reckoned against the
    reported 5 m/s exactly; a single report converges the fused velocity only
    to within its Kalman gain (here, about 99% of the way), so dead reckoning
    at exactly 5 m/s over 1,000 s was never going to match within 50 m. The
    test now feeds ten consistent reports of a target actually moving at 5 m/s
    east (position advancing to match, not held at the origin) before
    extrapolating, which is also a closer match to what a real Hybrid track
    looked like before it lost its range.
- Both corrected tests, and the other six, pass unchanged in behavior from the
  approved design; no requirement or design decision changed.

Passing run (2026-10-27, Windows): `dotnet build Core.Application.proj` had no
warnings or errors. `dotnet test Tests/Core/CoreTracks.Tests/CoreTracks.Tests.csproj`:
41 of 41 passed (the 8 new estimator tests plus the unaffected 33). `dotnet test
Poseidon.slnx`: every test project passed with zero failures, including the
Civilian and Military aggregates (whose module-suite rerun no longer hits the
MSB4181 collateral from slice 2's prior failing state) and the CMS smoke
projects. Every slice 2 row above now passes.

**Accepted by the user on 2026-10-27**, including the two test corrections
above. Slice 2 is done; slice 3 (manager) has not started.

### Slice 3 (manager): Step 2, tests written 2026-10-27

Scope: R1 to R21 and R33 to R36 (correlator including bearing association,
contributions, lifecycle, kind changes, automatic split, operator merge/split,
checkpoint schema 2). The ingress/queue swap, the publisher/operations module
points, adapters-as-publishers and the throughput baseline (R22 to R26, R29 to
R31) are slice 4 and are not touched here.

Contract and policy shapes written for real (data shape, not the behavior
under test), per the approved design (21.2, 21.12):

- `Track` is reshaped: `SourceId`, `SourceTargetId` and `LastSequence` are
  replaced by `Kind` (`Point`, `Bearing`, `Hybrid`), a nullable `Bearing`
  (`TrackBearing`: true bearing, uncertainty, own-ship position, measured
  time) and `Contributions` (`TrackContribution`: source ID, source-local ID,
  whether it is ranged, state, joined/measured/updated times, last sequence,
  update count). Position is null exactly when `Kind` is `Bearing`. `Track`
  keeps explicit value equality (contribution lists compare by content).
- `ITrackOperations` and `TrackOperationResult` are added (R17, R18); `tracks.
  operations` and the module's own exposure of it are slice 4 (`TracksModule`
  is unchanged this slice, still only exposing `tracks.ingestion` and
  `tracks.feed`, consistent with 21.3).
- `TrackPolicy` (internal, `CoreTracks`) is reshaped from one global ageing
  policy into the fallback for unregistered sources
  (`Unregistered{Stale,Lost,Remove}After`, default uncertainties) plus the
  correlation/estimation tunables from 21.4 (process noise, the 9.21 position
  and velocity gates, the 0.75 dominance threshold, the 200 m maximum join
  distance, the 3-update split confirmation count, the 6.63 bearing gate, the
  3 m/s velocity-reported-when threshold, the 10 m/s initial velocity
  uncertainty). Per-source timings and defaults come from each source's
  registered `TrackSourceDescriptor` instead (R11); a new internal
  `RegisterSource(TrackSourceDescriptor)` is `TrackManager`'s registration
  entry point for this slice (`ITrackPublisher.Register` wires to it in
  slice 4).
- `TrackManager` keeps implementing `ITrackIngestion` unchanged (`Submit`,
  `SubmitBatch`, `ReportSourceHealth`) and now also `ITrackOperations`
  (`Merge`, `Split`). Every behavior-bearing member is a Step 2 stub throwing
  `NotImplementedException`, except `Advance()`, which is a no-op: it runs on
  `TracksModule`'s own background timer, and throwing there would crash that
  timer instead of failing a single test cleanly. `GetSnapshot`/`GetChanges`
  return a fixed empty result rather than throw, for the same reason (nothing
  else calls them from a background thread today, but keeping every read path
  exception-free is simpler to reason about than deciding path by path).

Tests (`Tests/Core/CoreTracks.Tests/TrackManagerTests.cs`, rewritten per 21.6:
the former E2 "nearby positions stay separate" premise and its variants are
removed, not patched, since fusion makes the opposite true; `Tests/Core/
CoreTracks.Tests/TrackBearingTests.cs`, new, for R33 to R36):

| Requirement | Tests |
|---|---|
| R1 (invalid, carried over) | `InvalidObservations_AreRejectedWithoutCreatingTracks` |
| R2 | `UnknownSourceTrack_ThatCorrelatesWithNothing_CreatesATrackWithAGeneratedNumber`, `KnownSourceTrack_UpdatesItsContributionAndTheTrack` |
| R3 | `ANewSourceTrack_InsideTheGateOfExactlyOneCandidate_JoinsItAsAContribution` |
| R4 | `ANewSourceTrack_OutsideEveryGate_CreatesANewTrack` |
| R5 | `ANewSourceTrack_WellInsideOneCandidateAndFarFromAnother_JoinsTheDominantOne`, `ANewSourceTrack_EquidistantBetweenTwoCandidates_IsAmbiguousAndCreatesANewTrack` |
| R6 | `ATrack_NeverGainsASecondActiveContributionFromTheSameSource` |
| R7 | `ContributionsThatStopAgreeing_SplitAfterTheConfirmationCount_NotOnATransientDisagreement` |
| R8 | `JoiningContributions_FuseIntoOneEstimate_WeightedByTheirReportedUncertainty` |
| R9 | `ALateReportFromAnotherSource_IsAppliedWithoutBreakingTheTrack` |
| R10 (carried over) | `ExactRepeatIsDuplicate_AndOlderObservationIsOutOfOrder_WithoutChangingTheTrack` |
| R11 | `RegisteredSourceTimings_ApplyImmediately_AndSupersedeTheUnregisteredDefaults`, `UnregisteredSource_UsesThePolicyDefaults` |
| R12 | `Track_IsActiveWhileAnyContributionIsActive_StaleWhenNoneAreActive_LostWhenAllAre` |
| R13 | `ATrack_IsRemovedOnlyWhenItsLastContributionIsRemoved_AndKeepsItsNumberMeanwhile` |
| R14 (carried over, per contribution) | `DisconnectedSource_HoldsItsContributionsAtStale_NotLostOrRemoved_UntilItResumes` |
| R15 | `ANewSourceTrack_FromASourceWhoseEarlierContributionIsLost_MayRejoinTheSameTrack` |
| R17 | `Merge_KeepsTheOlderNumber_MovesContributions_AndLaterReportsUpdateTheSurvivor`, `Merge_FailsWithAReason_ForAnUnknownTrack` |
| R18 | `Split_MovesOneContributionToANewTrack_AndReEstimatesBoth`, `Split_FailsWithAReason_WhenItWouldRemoveTheLastContribution`, `Split_FailsWithAReason_ForAnUnknownTrackOrContribution` |
| R19 (carried over) | `NewTracksBeyondCapacityAreRejected_ButExistingContributionsStillUpdate` |
| R20 (carried over, reshaped) | `Feed_OffersGaplessOrderedChanges_IncludingContributions_ThatConvergeWithTheSnapshot`, `Feed_RequiresResyncForUnknownEpochFutureRevisionOrTrimmedLog` |
| R21 (schema 2) | `Checkpoint_PreservesContributionsDescriptorsAndSourceHealth_AndRestoredTracksStillCorrelate`, `Restore_RejectsUnsupportedOrCorruptState` |
| R33 | `BearingOnlyReport_ThatCorrelatesWithNothing_CreatesABearingTrack`, `BearingTrack_AgesAndIsRemoved_LikeAnyOtherTrack_AndItsNumberIsRetired` |
| R34 | `BearingReport_ThatCorrelatesWithExactlyOnePointTrack_JoinsItAsAContribution`, `BearingReport_AmbiguousBetweenTwoTracks_StaysStandalone_AndJoinsOnceTheAmbiguityClears`, `BearingTracks_NeverCorrelateWithEachOther` |
| R35 | `BearingContribution_RefinesThePoint_WhenTheOnlyActiveRangedContributionIsAis`, `BearingContribution_DoesNotRefineThePoint_WhileARadarContributionIsActive`, `BearingContribution_ThatStopsAgreeingForTheConfirmationCount_SplitsOffAsABearingTrack` |
| R36 | `ABearingTrackWhoseSourceReportsARange_BecomesAPointTrack_KeepingItsNumber`, `ATrackWhoseRangedContributionsAllStop_WhileABearingContributionIsActive_BecomesHybrid`, `AHybridTrack_BecomesPointAgain_WhenARangedReportArrives` |

Compile fixes elsewhere for the reshaped `Track` (data shape only, no behavior
change): the local `Track(...)` helpers in `TrackMotionTests.cs`,
`TrackReplicaTests.cs` and `CoreDisplay.Tests/TrackLayerTests.cs`.
`TrackPipelineIntegrationTests.cs` is adjusted: its AIS+radar scenario is
trimmed to the still-valid single-source (AIS-only) path and its old-shape
field access updated; the "a radar target at the same place is a separate
track" assertion is removed, since that premise is exactly what fusion
reverses (R3) — its correct replacement is slice 5's real-runtime fusion
scenario (R27, R28), not this slice.

Failing run (2026-10-27): `dotnet build Core.Application.proj` and `dotnet
build Poseidon.slnx`: no warnings or errors. `dotnet test Tests/Core/
CoreTracks.Tests/CoreTracks.Tests.csproj`: 46 failed of 63. The 38 new slice 3
tests (27 in `TrackManagerTests.cs`, 11 in `TrackBearingTests.cs`) fail with
`NotImplementedException` from the stub, as expected. The other 8 failures are
expected collateral from the same stub, not new problems: `TracksModuleTests`'
two tests and the shared `ModuleDeclarationTests.StopBeforeStart_IsSafeAndIdempotent`
fail immediately with the same exception (`Submit`/`Capture` on the real
`TracksModule`); the four `TrackPipelineIntegrationTests` fail instead with a
15 s timeout, because a submitted observation is normalized and handed to
`ObservationRelay`'s background consumer thread, where `Submit`'s exception is
not observed by the awaiting test — the symptom differs (timeout vs.
exception) but the cause is the same stub. `Contracts.Tests` (105 of 105) and
`CoreDisplay.Tests` (18 of 18) are unaffected by the reshape.

`dotnet test Poseidon.slnx` (full solution): every project passes except
`CoreTracks.Tests` (46 of 63, as above) and one more collateral failure found
this way, `CoreUI.Tests.ShellViewportIntegrationTests.AisInput_ReachesTheShellViewportThroughTracksAndDisplay`
(1 of 28) — the same real-runtime AIS-to-Tracks-to-Display path as the
`TrackPipelineIntegrationTests`, timing out for the same reason. The Civilian
aggregate's own module-suite rerun again surfaces an MSBuild MSB4181 for the
same reason as slice 2's failing run (its usual way of propagating an inner
suite failure); Military.Tests was not reached before the run stopped there.
Every CMS smoke project, `Core.Tests`, `CoreAIS.Tests`, `CoreCharts.Tests`,
`CoreNavigation.Tests`, `CoreRadar.Tests` and `CoreSonar.Tests` pass.

### Slice 3 (manager): Step 3, code written 2026-10-28

The user ordered the implementation on 2026-10-28.

- `TrackManager` is rewritten in full: per-source registration (`RegisterSource`),
  correlation (nearest-neighbour position gate plus the optional velocity gate,
  both 9.21 chi-square two degrees of freedom; the 0.75 dominance score; the
  200 m absolute join cap; R6's same-source exclusion, which allows a Lost
  contribution to be rejoined and replaced, R15), per-contribution ageing and
  removal (R12 to R14), automatic split by comparing each ranged or bearing
  contribution's own dead-reckoned last report against the first contributor's
  (R7, R35), operator merge/split (R17, R18, both re-estimating from the
  surviving contributions' own last reports), bearing gating and refinement
  (R33 to R36), and checkpoint schema 2. The correlator stayed inline in this
  file rather than moving to a separate `TrackCorrelator.cs`; nothing observes
  file layout, and splitting it out later is free to do if it helps.
- Gating and the dominance score use each candidate's combined (DRMS) position
  uncertainty as if it were isotropic (independent, equal variance in both
  axes: variance ≈ DRMS²/2 per axis) rather than its true covariance, and the
  dominance likelihood considers position only, not velocity. Documented as a
  starting point, consistent with the plan's own D1/D3.
- `TrackEstimator` gained three members needed by the manager, all additive
  (no existing slice 2 test changed behavior): `Peek(atUtc)`, a non-mutating
  version of `Predict` used to score several candidate tracks against one
  report without committing to any of them; and `Export()`/`Restore(...)`,
  a full internal-state snapshot (frame origin, clock, the raw state and
  covariance, the latest bearing) for the checkpoint. The covariance is
  flattened to a 16-element array in the snapshot, since `System.Text.Json`
  does not serialize rectangular (`double[,]`) arrays.
- Found and corrected test-design bugs while making the tests pass (the
  tests, not the production code, per development workflow rule 2 — these
  are wrong assumptions in the tests themselves, not requirement changes):
  - Two tests disconnected a source and expected its contribution to reach
    Lost; R14 holds a disconnected contribution at Stale indefinitely, so it
    never does. Both (`BearingReport_AmbiguousBetweenTwoTracks...` and
    `ANewSourceTrack_FromASourceWhoseEarlierContributionIsLost...`) now use a
    short registered descriptor and let the contribution age to Lost while
    staying connected instead.
  - `KnownSourceTrack_UpdatesItsContributionAndTheTrack` expected a second
    report to land exactly on the new position; under fusion it is pulled
    toward it by the Kalman gain, not replaced outright. Now asserts movement
    toward the new report, not onto it.
  - `BearingContribution_DoesNotRefineThePoint_WhileARadarContributionIsActive`
    compared positions a second apart with exact equality; the fused point
    still dead-reckons forward with elapsed time regardless of the bearing.
    The bearing report now shares the same measured instant as the ranged
    reports, so only the bearing's own (non-)effect is being compared.
  - `RegisteredSourceTimings_ApplyImmediately...` re-registered a source with
    a removal time shorter than the time already elapsed, so the track was
    removed outright instead of landing on Lost as asserted. The new
    descriptor's removal time is now generous enough to observe Lost first.
  - `TrackPipelineIntegrationTests` asserted a freshly created track's speed
    immediately, echoing the raw AIS report; R8 reports speed/course only
    once the fused velocity has converged, which one report does not reach.
    The assertion is removed; the position-on-creation assertion (an exact
    seed, not a fused update) is untouched and still holds.
  - One production fix found this way, not a test fix: a new source track
    joining a track to replace its own Lost contribution (R15) was adding the
    new contribution without removing the Lost one, leaving both. Fixed in
    `ApplyObservation`.

Passing run (2026-10-28, Windows): `dotnet build Poseidon.slnx` had no
warnings or errors. `dotnet test Tests/Core/CoreTracks.Tests/CoreTracks.Tests.csproj`:
63 of 63 passed. `dotnet test Poseidon.slnx`: every project passed with zero
failures, including `CoreUI.Tests` (28 of 28, including the previously
timing-out `ShellViewportIntegrationTests.AisInput_ReachesTheShellViewportThroughTracksAndDisplay`)
and both the Civilian and Military aggregates (no MSB4181 this time). Every
slice 3 row above now passes.

**Accepted by the user on 2026-10-28**, including the test and production
fixes above. Slice 3 is done; slice 4 (ingress) is accepted (see below);
slices 5 and 6 have not started.

### Slice 4 (ingress): Step 2, tests written 2026-10-28

Scope: R22 to R26, R29 to R31 (publisher, per-source queues, consumer thread,
statistics, system health, throughput baseline) and R38 to R40, the
multi-sensor-channel amendment (21.13) raised before this slice's tests were
written and folded into the plan the same day. R22 (`TrackMotion.Predict`)
was already implemented in slice 1. The adapters' full rewire onto the
publisher (removing `ObservationRelay`/`ITrackIngestion`) and R23's full
real-runtime shape are Step 3; `tracks.ingestion` stays working unchanged
until then.

Contract shapes added for real (data shape, not the behavior under test), per
21.2: `TrackPoints.Publisher`/`Operations`; `ITrackSink` and `ITrackPublisher`;
`SourceStatistics`, folded into `SourceStatus` as an optional field;
`TrackSystemHealth`/`TrackSystemStatus`, folded into `TrackSnapshot` and
`TrackChange`/`TrackChangeKind` (a new `System` kind). `RadarTargetReport`
gains `ChannelId` (default `"default"`, so existing callers are unaffected);
`IRadarInput` gains `SetAntennaRotationRate(channelId, rpm)`.

Production stubs (Step 2, throw `NotImplementedException` for the part that
is genuinely new this slice):

- `TrackIngress` (new, `CoreTracks`), the publisher side of 21.9: its
  `Register` delegates to `TrackManager.RegisterSource`, already real and
  working since slice 3 (validates the descriptor and stores it), and only
  then throws, because the per-source queue and the sink it would return do
  not exist yet. `TracksModule.Features` now also exposes
  `tracks.publisher` (backed by `TrackIngress`) and `tracks.operations`
  (backed by `TrackManager`, whose `Merge`/`Split` have been real since
  slice 3 — exposing them through the module is plumbing, not new
  behavior). `CoreTracks/module.json` gains both points and contributions.
- `RadarModule.SetAntennaRotationRate` stub.
- `RadarHardwareConfiguration.LoadChannels` (new, `CoreRadar`, internal): the
  R38/R40 channel-config reader; fully stubbed, since nothing of it existed
  before this slice. Not yet called from `RadarModule.StartAsync` or
  `Submit`; that wiring, and the actual adapter rewrite, are Step 3.

Tests (new `Tests/Core/CoreTracks.Tests/TrackIngressTests.cs`; additions to
`TracksModuleTests.cs`; new `Tests/Core/CoreRadar.Tests/RadarHardwareConfigurationTests.cs`;
additions to `RadarAdapterTests.cs`), each written as if the feature already
worked so it exercises real assertions once Step 3 lands:

| Requirement | Tests |
|---|---|
| R23 | `TrackIngressTests.Register_WithAnInvalidDescriptor_ThrowsArgumentException` (real, passes today), `Register_WithAValidDescriptor_ReturnsASinkBoundToTheSource`, `Register_TheSameSourceIdAgain_ReplacesTheEarlierSink_WhichStopsAcceptingPublishes`; `TracksModuleTests.Publisher_Register_ValidatesTheDescriptor_AndReturnsASinkBoundToTheSource` |
| R24 (partial) | `TracksModuleTests.TracksModule_RunsCorrectly_WithNoSourceEverRegistering` (real, passes today). The reload pick-up/drop half needs the real ingress and moves to slice 5's real-runtime scenario (R27, R28) |
| R25, R40 | `RadarAdapterTests.SetAntennaRotationRate_ChangesTheChannelsRegisteredInterval` |
| R26 | `TrackIngressTests.Publish_WhenASourcesQueueIsFull_DropsTheOldestReportAndCountsItInsteadOfBlocking`, `TrackSystemHealth_BecomesDegraded_WhileAQueueIsDropping_AndRecoversAfterTenSecondsWithoutDrops` |
| R29 | `TrackIngressTests.ConcurrentSources_EachKeepTheirOwnOrder_AndNoneStarvesTheOthers` |
| R30 | `TrackIngressTests.SourceStatistics_ExposeReceivedCreatedUpdatedRejectedDroppedQueueDepthAndLastRejection` |
| R31 | `TrackIngressTests.ThroughputBaseline_IsMeasuredAtOneThousandTracks_AsAMeasurementNotAGate` |
| R38 | `RadarHardwareConfigurationTests.LoadChannels_WithNoConfigurationFile_ReturnsOneImplicitDefaultChannel` |
| R39 | `RadarAdapterTests.Submit_WithAChannelIdThatIsNotConfigured_IsRejectedAsInvalid` (real-behavior-gap failure: today's `Submit` ignores `ChannelId` entirely) |
| R40 | `RadarHardwareConfigurationTests.LoadChannels_ParsesEachConfiguredChannel_WithItsOwnRotationRate`, `LoadChannels_RejectsDuplicateChannelIds_AsAMalformedFile`, `LoadChannels_RejectsANonPositiveRotationRate_AsAMalformedFile` |
| Module wiring (21.3, 21.5; not its own R-id) | `TracksModuleTests.Manifest_DeclaresAllFourPointsAndContributesEachOfThem`, `Features_ImplementTheDeclaredSharedContracts` (both real, pass today) |

Failing run (2026-10-28, Windows): `dotnet build Core.Application.proj`: no
warnings or errors. `dotnet test Tests/Core/CoreTracks.Tests/CoreTracks.Tests.csproj`:
8 failed of 73 — the 8 new tests that need a working sink, all with
`System.NotImplementedException` from `TrackIngress.Register` (uncaught); the
other 65, including the real `Register_WithAnInvalidDescriptor...`,
`Manifest_...`, `Features_...` and `TracksModule_RunsCorrectly...` tests and
every slice 1 to 3 test, pass. One collateral fix: `TrackPipelineIntegrationTests`'
hard-coded feature-point list is updated for the two new points (not a
behavior change). `dotnet test Tests/Core/CoreRadar.Tests/CoreRadar.Tests.csproj`:
6 failed of 29 — 4 via the uncaught `NotImplementedException` from
`RadarHardwareConfiguration.LoadChannels` or `RadarModule.SetAntennaRotationRate`,
1 (`LoadChannels_Rejects...`, both malformed-file cases) via `Assert.Throws<InvalidDataException>`
catching the stub's `NotImplementedException` instead, and 1
(`Submit_WithAChannelIdThatIsNotConfigured...`) because today's `Submit`
returns `None` (accepted) rather than `Invalid`, since it does not look at
`ChannelId` at all yet; the other 23 pass. `dotnet test Poseidon.slnx`: every
other project passes (`Contracts.Tests` 105 of 105, `Core.Tests` 116 of 116,
`CoreAIS.Tests` 30 of 30, `CoreUI.Tests` 28 of 28, `CoreDisplay.Tests` 18 of
18, `CoreSonar.Tests`/`CoreNavigation.Tests`/`CoreCharts.Tests` 8 of 8 each,
every CMS smoke project); the Civilian aggregate's own module-suite rerun surfaces
the same CoreTracks/CoreRadar failures as an MSBuild MSB4181 (its usual way of
propagating an inner suite failure, consistent with every earlier slice's
failing run) and the run stops there, so Military's rerun was not reached —
expected collateral, not a new problem.

Assumed while writing the tests:

- `TrackIngress.Register`'s split between real validation/storage (delegated
  to the already-accepted `TrackManager.RegisterSource`) and a stub for the
  genuinely new queue/sink: nothing in the approved plan constrained how the
  stub boundary falls inside `Register` itself, so the split was chosen to
  give Step 2 a real, passing assertion for descriptor validation (consistent
  with R11 already being accepted behavior) while still failing for the right
  reason on the new sink.
- `RadarHardwareConfiguration.LoadChannels` is fully stubbed with no such
  split, because nothing about reading a hardware configuration file existed
  before this slice; the file format decided above is recorded in 21.13, not
  assumed silently.
- R24's reload pick-up/drop half is deferred to slice 5 rather than given a
  speculative test now, consistent with how slice 3 deferred the AIS+radar
  fusion scenario to the same place (R27, R28).

### Slice 4 (ingress): Step 3, code written 2026-10-28

The user ordered the implementation on 2026-10-28.

**Scope decision, flagged for the Step 3 gate rather than assumed silently:**
this pass makes the 14 approved tests pass and nothing else; it does not
remove `ObservationRelay`/`ITrackIngestion` or rewire `CoreAIS`/`CoreRadar`
onto `ITrackPublisher`/`ITrackSink`, because no approved test exercises that
path (the adapters still work exactly as before, through the relay). The
slice 4 Step 2 entry's note that the full rewire "are Step 3" was broader
than what the approved tests actually require; the rewire (and R23's full
real-runtime shape) stays open, now called out explicitly below rather than
assumed done.

- `TrackIngress` is implemented for real: a bounded, drop-oldest, per-source
  queue (`max(4,096, 10 x declared peak rate)`, per 21.4), a `QueuedTrackSink`
  that rejects publishes once its source re-registers and replaces it
  (`Unavailable`), a background consumer (`Start`/`StopAsync`) that round-
  robins every source's queue in batches of 128, and an internal `DrainOnce`
  that performs exactly one such round synchronously — the production pump
  calls it in a loop with a short idle delay; tests call it directly instead
  of starting the pump, so they stay deterministic with no polling.
- `TrackManager` gains per-source statistics (R30) and system health (R26):
  `SubmitLocked` is wrapped (renamed to `SubmitLockedCore`) so every outcome
  updates a `SourceStatisticsAccumulator` (Received always; Created, Updated,
  or a rejection-reason bucket and `LastRejection`); a new internal
  `RecordQueueMetrics(sourceId, queueDepth, droppedDelta)` is how
  `TrackIngress` reports its own queue's depth and drops, since the manager
  has no other way to know them. A drop stamps a system-wide `lastDropUtc`;
  `GetSnapshot()` computes `TrackSystemStatus` from it (Degraded within 10 s
  of the last drop, Healthy otherwise) and merges each source's statistics
  into its `SourceStatus`. Statistics and the drop timestamp are not
  checkpointed (documented in `Tracks.cs`): they are runtime observability,
  reset like any other process-lifetime counter would be, not track state.
- `TracksModule.StartAsync`/`StopAsync` now also start/stop the ingress pump.
- `RadarHardwareConfiguration.LoadChannels` is implemented for real: reads
  `sensors.json` next to the module's own assembly, returns one implicit
  `default` channel (24 rpm) when the file is absent, and throws
  `InvalidDataException` for a missing/empty channel list, a blank channel
  ID, a duplicate channel ID, or a non-positive rotation rate.
- `RadarModule` loads its channel list on every `StartAsync` (so a reload
  picks up an edited file) and `Submit` rejects a report naming a channel
  that was not loaded, as `Invalid` (R39). `SetAntennaRotationRate` stores
  the rate per channel; it does not yet register a per-channel descriptor
  with Tracks (that needs the deferred rewire above). The leftover
  `cms.radar.surveillance` capability (noted in passing during the 21.13
  discussion) is removed, since `CoreRadar` is navigation-only.

Test-design fixes found while making the tests pass, corrected rather than
the production code (development workflow rule 2; a wrong test is fixed and
the reason recorded here, not silently adjusted):

- `TrackIngressTests.ConcurrentSources_...`, `SourceStatistics_...` and
  `ThroughputBaseline_...` asserted on `TrackManager.GetSnapshot()`
  immediately after publishing, with no consumer ever draining the queue;
  under the approved async ingress design (21.9) that assertion can never
  pass. Each now calls the new `DrainOnce()` (in a `while` loop) after
  publishing, which processes everything deterministically without starting
  the background pump or polling — a mechanical fix to how the test awaits
  the result, not to what it checks.
- `TrackManagerTests.Checkpoint_PreservesContributionsDescriptorsAndSourceHealth_AndRestoredTracksStillCorrelate`
  (slice 3) compared `before.Sources` and `after.Sources` for full equality;
  `SourceStatus` now carries `Statistics`, which is intentionally not
  checkpointed, so a restored source's statistics are always null while the
  original's are not. The comparison now excludes `Statistics` and a new
  assertion states directly that it is null after restore, rather than
  silently dropping the check.

Passing run (2026-10-28, Windows): `dotnet build Core.Application.proj`: no
warnings or errors. `dotnet test Tests/Core/CoreTracks.Tests/CoreTracks.Tests.csproj`:
73 of 73 passed (the 8 new ingress tests, the corrected checkpoint test, and
every slice 1 to 3 test). `dotnet test Tests/Core/CoreRadar.Tests/CoreRadar.Tests.csproj`:
29 of 29 passed. `dotnet test Poseidon.slnx`: every project passed with zero
failures, including both aggregates (no MSB4181 this time) and every CMS
smoke project. Every row in the slice 4 Step 2 requirement-to-test table now
passes.

**Not done in this pass, carried forward:**

1. `ObservationRelay`/`ITrackIngestion` removal and rewiring `CoreAIS`/
   `CoreRadar` onto `ITrackPublisher`/`ITrackSink` (21.6); `tracks.ingestion`
   stays in the manifest and working until then.
2. `CoreRadar` registering one `TrackSourceDescriptor` per configured
   channel with Tracks (today it loads channels and rejects unconfigured
   ones, but does not register anything with the publisher); `CoreAIS`
   registering its own descriptor (R25).
3. `CoreSonar` reading its own hardware configuration file (noted in 21.13
   as not touched by M1-E, since civilian `Core.Sonar` is not a track
   source).
4. R23's full real-runtime shape and R24's reload pick-up/drop half, both
   already deferred to slice 5's real-runtime scenario in the Step 2 entry.
5. R31's throughput baseline is exercised by a correctness test
   (`ThroughputBaseline_IsMeasuredAtOneThousandTracks...`), not yet a timed
   measurement recorded as data; turning it into an actual rate measurement
   is still open.

**Accepted by the user on 2026-10-28**, including the "not done in this
pass" list above as carried-forward scope rather than finished work. Slice 4
is done on those terms; items 1 and 2 of that list (the `ObservationRelay`/
`ITrackIngestion` removal, the adapter rewire onto the publisher, and
`CoreRadar`/`CoreAIS` registering their descriptors) are a prerequisite for
slice 5's real-runtime fusion scenario (R27, R28), which needs sources
actually publishing through the new ingress to be a meaningful end-to-end
test of this slice's own design. They are picked up as the first step of
slice 5's own Step 2/3 work, not as a separate slice of their own.

### Slice 5 (consumers and integration): Step 2, tests written 2026-10-29

Two open points were resolved with the user before writing tests, since the
plan did not pin them down: which three integration files 21.5 item 5 means
(the two existing `*IntegrationTests.cs` files plus a new real-runtime fusion
test), and how R37's bearing line should be realized given `ChartViewport` has
no chart projection or canvas yet (expose the line's geographic data, not
pixels, for a future renderer to draw).

Carried-forward rewire (slice 4's deferred first step, 21.6):

- `ITrackIngestion` and `tracks.ingestion` (both the point and the
  `TrackPoints` constants) are removed outright, with no compatibility shim,
  from `Tracks.cs`, `module.json`, `TrackManager` (keeps `Submit`/
  `SubmitBatch`/`ReportSourceHealth` as plain methods; `TrackIngress` still
  calls them directly) and `TracksModule`'s `Features`.
- `ObservationRelay.cs` and its test file are deleted. A new
  `TrackSourceConnector` (`Poseidon.Core.Modules`, Contracts) replaces its
  role per source: real descriptor validation, `Publish`/`ReportHealth`/
  `UpdateDescriptor`, but `Start`/`StopAsync` (the background task that
  retries `ITrackPublisher.Register` every 200 ms, 21.4) are
  `NotImplementedException` stubs, matching the precedent `TrackIngress`
  itself set in slice 4 Step 2.
- `AisModule` and `RadarModule` are rewired onto `TrackSourceConnector`.
  `RadarModule` holds one connector per configured channel (source ID
  `Core.Radar` for the default channel, `Core.Radar.<ChannelId>` otherwise,
  21.4), derives each channel's descriptor from its rotation rate
  (`DescriptorFor`, real), and `SetAntennaRotationRate` now re-registers the
  recomputed descriptor instead of only recording the rate. Because
  `ObservationRelay` used to stamp each observation's sequence number and
  checkpoint the counter, and nothing else inherits that role, `AisModule`
  and `RadarModule` (per channel) now own their own sequence counter and
  checkpoint directly (new schema versions: 1 for AIS, 2 for Radar's
  per-channel dictionary).
- `Tests/Shared/FakeTrackHost.cs`: `RecordingIngestion`/`Ingestion` become
  `RecordingPublisher`/`Publisher` (an `ITrackPublisher` that also records
  the last descriptor registered per source, for the rotation-rate test
  below), routed through `tracks.publisher` instead of `tracks.ingestion`.

| Requirement | Tests | Failing run (2026-10-29) |
|---|---|---|
| R23 (carried over) | `AisModuleTests.RunningAdapter_NormalizesAndForwardsReportsAndHealthToTracks`, `RadarModuleTests.Adapter_IsUnavailableUntilStarted_ThenForwardsReportsAndHealth`, `Sequence_IsCheckpointedAcrossRestarts_AndUnsupportedStateIsRejected` (both adapters), `ModuleDeclarationTests.StopBeforeStart_IsSafeAndIdempotent` | Fail: `TrackSourceConnector.Start` is a stub |
| R25, R40 (rotation rate → descriptor) | `RadarModuleTests.DescriptorFor_DerivesTimingsFromTheRotationRate_ScalingUpOnlyWhenSlow` (real logic, passes now), `SetAntennaRotationRate_ReRegistersTheDefaultChannelsDescriptorWithTheNewInterval` | The second fails: needs the real connector to observe the re-registered descriptor |
| R38 to R40 (per-channel source ID) | `RadarModuleTests.SourceIdFor_NamesTheDefaultChannelPlainly_AndOthersWithTheirOwnChannelId` (real, passes), `SetAntennaRotationRate_OnAnUnconfiguredChannel_ThrowsArgumentException` (real, passes), `Submit_WithAChannelIdThatIsNotConfigured_IsRejectedAsInvalid` (fails: needs Start) | |
| R27, R28 (real-runtime fusion) | `TrackPipelineIntegrationTests.AisAndRadar_OfOneVessel_FuseIntoOneTrack_WhichSurvivesReload_AndOutlivesEitherSource` (new) | Fails: needs both adapters' connectors to register for real |
| R37 (Display bearing line) | `TrackLayerTests.BearingLines_IncludeBearingAndHybridTracksOnly_StyledByEachTracksOwnState` | Fails: `ChartViewport.BearingLines` is a stub |
| R33/R37 (replica carries bearing tracks) | `TrackReplicaTests.ABearingTrack_WithNoPositionAndANonNullBearing_FlowsThroughSnapshotAndChanges` (new) | Passes already: `TrackReplica` is unchanged (21.6), added as coverage, not forced red |

Failing run (2026-10-29, Windows): `dotnet test Poseidon.slnx`: 16 tests fail
across `CoreAIS.Tests` (4), `CoreRadar.Tests` (5), `CoreTracks.Tests` (5),
`CoreUI.Tests` (1, `ShellViewportIntegrationTests.AisInput_...` — purely
collateral from `AisModule.StartAsync`'s stub, confirming it needed no
behavioral edit of its own) and `CoreDisplay.Tests` (1). Every failure is
`NotImplementedException` from `TrackSourceConnector.Start`/`StopAsync` or
`ChartViewport.BearingLines` — none is a compile error or a mis-wired test.
Every other test, including every slice 1 to 4 test, still passes.

Bug found and fixed while verifying the failing run (development workflow
rule: a wrong test or a wrong assertion is fixed and the reason recorded, not
silently left): `RadarModule.Submit` returned `Invalid` ("channel not
configured") instead of `Unavailable` when called before `StartAsync`,
because an empty `connectors` dictionary looked the same as a populated one
missing that channel. `AisModule` already distinguished these correctly (a
nullable single connector). Fixed by adding an explicit `started` flag to
`RadarModule`, checked first in `Submit`.

Not done in this pass, by design (Step 2 only): the real registration/retry
loop in `TrackSourceConnector`, and the real Bearing/Hybrid filtering in
`ChartViewport.BearingLines`. Both are Step 3.

Gate: presented to the user 2026-10-29; approved the same day, ordering
Step 3 (code).

### Slice 5 (consumers and integration): Step 3, code written 2026-10-29

- `TrackSourceConnector.Start`/`StopAsync` are implemented for real: `Start`
  runs a background task (execution-context flow suppressed, matching
  `ObservationRelay`/`TrackReplicaPump`) that retries
  `ITrackPublisher.Register` every `retryDelay` (200 ms default) until it
  succeeds, then holds the sink; `StopAsync` cancels the loop and disposes
  the sink. An `UpdateDescriptor` call that lands between the loop reading
  the descriptor and the registration completing is reconciled once the
  sink is set (the stored descriptor and the one just used are compared by
  value; a mismatch is forwarded to the fresh sink), so a rotation-rate
  change is never silently lost to that race (R25: "the new timings apply
  immediately").
- `ChartViewport.BearingLines` is implemented for real: one `BearingLine`
  per Bearing- or Hybrid-kind track with a non-null `Bearing`, carrying that
  track's own `State`.

Bugs found and fixed while making the tests pass (development workflow
rule: corrected, not patched around, and recorded here):

- `AisModule.Submit` and `RadarModule.Submit` incremented their sequence
  counter before checking whether the connector had actually registered
  (`Sink is null`), so a report submitted in the window between `StartAsync`
  returning and the background registration completing was rejected as
  `Unavailable` but still permanently consumed a sequence number. Fixed by
  moving the `Sink is null` check before the increment in both, matching
  `ObservationRelay`'s old behavior (a rejected-before-the-channel report
  never advanced its sequence either).
- Three adapter tests (`RunningAdapter_...`/`Adapter_IsUnavailableUntilStarted_...`,
  both `Sequence_IsCheckpointedAcrossRestarts_...`, and
  `SetAntennaRotationRate_ReRegistersTheDefaultChannelsDescriptorWithTheNewInterval`)
  asserted on the very first `Submit`/descriptor read immediately after
  `StartAsync`, assuming registration had already completed; now that it is
  a real background task, that is a race. Fixed by polling past it
  (`SubmissionPolling.UntilRegisteredAsync` in `FakeTrackHost.cs`, and an
  inline poll for the descriptor test) — a mechanical fix to how each test
  awaits readiness, not to what it checks, the same category as slice 4's
  `DrainOnce` fixes.

Passing run (2026-10-29, Windows): `dotnet build Poseidon.slnx`: no warnings
or errors. `dotnet test Poseidon.slnx` run three times in a row: every
project passes with zero failures each time (including both aggregates,
every CMS smoke project, and `CoreUI.Tests`' `ShellViewportIntegrationTests`),
with no MSB4181 recurrence. Every row in the slice 5 Step 2 requirement-to-
test table now passes.

**Accepted by the user on 2026-10-29.** Slice 5 is done. Slice 5's code was
committed on 2026-10-31 (`ee0e486`). Slice 6 (close-out) has not started.

### Slice 6 (close-out): cross-platform CI failures on the slice 5 PR, fixed 2026-10-31

The slice 5 PR's CI failed on all three platforms (Windows, Arch Linux,
RHEL-compatible). Both failures are in
`Tests/Core/CoreTracks.Tests/TrackPipelineIntegrationTests.cs`, which
deliberately exercises the real background registration/retry loop
(`TrackSourceConnector.Start`, R23) and the real ingress pump
(`TrackIngress`'s background task) rather than `DrainOnce()`, so these tests
are exposed to real async scheduling delays that the CI runners apparently
hit harder than the development machine:

1. `SourceDisconnect_IsVisibleInTheFeed_AndAnObservationRestoresIt` timed out
   at its 15s deadline on Windows and Arch Linux, but passed cleanly on an
   immediate retry within the same job (the solution's `Civilian.Tests`/
   `Military.Tests` aggregates re-run every referenced test project, so each
   project's tests execute twice per `dotnet test` invocation). Not a logic
   bug: `WaitForTracks`'s margin was simply too tight for a loaded CI runner.
2. `Reload_KeepsTrackIdsAndConsumerCursors_AndTheSensorKeepsUpdatingTheSameTrack`
   failed deterministically (twice) on RHEL with `.Accepted` false immediately
   after the test's post-reload `Submit`, not a timeout. Touching `CoreTracks`
   forces `AisModule.StartAsync` to recreate its `TrackSourceConnector`, whose
   registration against the new `TrackIngress` runs on a background task
   (21.3, R23) and is not guaranteed to complete before the test's very next
   `Submit` call, which asserted `.Accepted` with no retry margin at all. The
   identical race is already handled for the adapter unit tests by
   `SubmissionPolling.UntilRegisteredAsync` (`Tests/Shared/FakeTrackHost.cs`);
   this integration test just never got the same treatment.
3. The RHEL log's trailing `error MSB4181` from `Civilian.Tests` is not a
   third bug: it is simply how the aggregate's nested
   `<MSBuild Targets="VSTest">` (`RunCivilianModuleSuites`) echoes an inner
   test project's failure (documented above, "its usual way of propagating
   an inner suite failure"). It disappeared once (2) was fixed.

Fixed (test-only, mechanical, development workflow rule: wrong test timing
assumptions corrected and recorded, not the production code, which is
correct):

- `WaitForTracks`'s deadline raised from 15s to 30s.
- Added `SubmitAisUntilRegistered`, the same shape as
  `SubmissionPolling.UntilRegisteredAsync`, and used it for the one `Submit`
  that follows a reload in `Reload_KeepsTrackIdsAndConsumerCursors_...`.

Passing run (2026-10-31, Windows): `dotnet build Core.Application.proj`: no
warnings or errors. `dotnet test Poseidon.slnx` run three times in a row:
every project passes with zero failures each time, including both
aggregates (no MSB4181) and `CoreTracks.Tests` (74/74 each time).

A second push's Arch Linux CI run then failed again, this time
`Reload_KeepsTrackIdsAndConsumerCursors_...` itself timing out at the new 30s
deadline. Raising `WaitForTracks`'s margin was treating a symptom: the real
cause is that several classes in this assembly
(`TrackPipelineIntegrationTests`, `TracksModuleTests`, `CoreTracksModuleTests`)
each run a real `TracksModule`/`ModuleRuntime` — a real 1s
`TimeProvider.System` tick timer plus `TrackIngress`'s and
`TrackSourceConnector`'s background `Task.Run` pumps and registration loops —
and xunit's default test-collection parallelism (one collection per class,
run concurrently) packs many of these onto the thread pool at once. Under a
constrained CI runner that starves them badly enough to blow even a 30s poll,
which no amount of further bumping meaningfully fixes.

Fixed instead at the source: `[assembly: CollectionBehavior(DisableTestParallelization = true)]`
added for `CoreTracks.Tests` (`AssemblyInfo.cs`), so these real-background-task
test classes run sequentially instead of fighting each other for the thread
pool. Confirmed with `dotnet test Tests/Core/CoreTracks.Tests` three times in
a row (74/74 each time, ~3s) and `dotnet test Poseidon.slnx` twice more
(zero failures, both aggregates, no MSB4181). The 30s `WaitForTracks` margin
and the `SubmitAisUntilRegistered` registration-race fix both stay as
defense in depth.

If this same class of flake recurs in another assembly that mixes real
`ModuleRuntime`/background-task tests with many unit tests (`CoreAIS.Tests`,
`CoreRadar.Tests`, `CoreUI.Tests` all have real adapters/timers too), the same
fix applies there.

A third push's RHEL CI run then failed again on the very same
`SourceDisconnect_IsVisibleInTheFeed_...`, timing out at the new 30s
deadline, even with per-assembly test-collection parallelism disabled.
Disabling parallelism inside `CoreTracks.Tests` only addressed contention
*within* that one process; the CI logs show `dotnet test Poseidon.slnx`
itself running many projects' VSTest executions concurrently (the default
for MSBuild/`dotnet test` on a multi-project solution, confirmed locally:
without a cap, `Test run for X.dll` lines from different projects visibly
interleave before any one of them finishes). On a CPU-constrained CI
container, that cross-process contention — not anything inside one test
assembly — is what starves `TrackIngress`'s/`TrackSourceConnector`'s
background tasks badly enough to blow any poll deadline; it also explains
why only the *first* run of `CoreTracks.Tests.dll` in a job has ever failed,
never the aggregate's later rerun of the same binary, since by then most
other projects' test runs have already finished and the contention is gone.

Fixed at the actual source this time: added `-maxCpuCount:1` to the
`dotnet test Poseidon.slnx` invocation in both `.github/scripts/ci-linux.sh`
and the Windows job of `.github/workflows/build-test-package.yml`, forcing
every project's build+test to run one at a time instead of all at once.
Confirmed locally that this flag is honored (every `Test run for X.dll` now
completes before the next one starts, no interleaving) and that
`dotnet test Poseidon.slnx -maxCpuCount:1` still passes cleanly end to end.
This is a CI-script change, not a test-only one, and trades some CI wall
time for removing the actual cross-process contention; current per-project
durations suggest the total stays well inside the existing 30/45 minute
job timeouts, but that's only confirmed once it has actually run in CI.

A fourth push's RHEL and Windows CI runs then both failed fast and
deterministically (not a timeout) on a different test:
`TracksModuleTests.Module_CheckpointsItsTracks_AndRestoresThemAfterRestart`,
`Assert.Single() Failure: The collection was empty`. `-maxCpuCount:1`
worked exactly as intended (every `Test run for X.dll` in both logs
completes before the next starts, no interleaving at all), so this is a
genuinely different bug, not a recurrence of the cross-process contention.

Reproduced locally (not CI-specific): running the whole `CoreTracks.Tests`
project repeatedly reproduces it roughly 1 run in 3 to 5. Diagnostic output
added temporarily at the failure point showed `sink.Publish(...).Accepted`
true (the observation really was enqueued) but every one of
`TrackManager`'s per-source counters (`Received`/`Created`/etc.) at zero —
proof `SubmitBatch` was never reached for it at the moment checked, not that
it was rejected.

Root cause: a genuine TOCTOU race in the test helper `DrainSynchronously`
(`while (ingress.DrainOnce()) {}`), used by both `TracksModuleTests` and
`TrackIngressTests`, but only risky where the module's real background pump
is also running (`TracksModule.StartAsync` calls `ingress.Start()`; only
`TracksModuleTests` goes through that — `TrackIngressTests` constructs a bare
`TrackIngress` and never starts it, so it was never at risk).
`TrackIngress.DrainOnce()` takes its queue snapshot under a lock but then
dequeues and calls `TrackManager.SubmitBatch` *outside* that lock, so the
real pump and a test's manual drain can run `DrainOnce()` concurrently: the
pump can dequeue the one item (so the test's own `DrainOnce()` call finds
the queue already empty and returns false) while still being mid-call into
`SubmitBatch` — a caller that asserts on the manager's state immediately
after `DrainSynchronously` returns can observe that gap. The helper's own
old comment ("DrainOnce finds nothing once the pump already consumed an
item") assumed finding nothing meant the pump had *finished*, not just
*started*; that assumption is what was wrong. Not a production bug — nothing
in production code ever calls `DrainOnce()` manually like this.

Fixed in `TracksModuleTests.cs` (test-only, mechanical, development
workflow rule: documented here, not in the test body as a comment on every
line): the two spots that asserted immediately after
`DrainSynchronously(publisher)` while the module's pump was still running
(`Module_AgesTracksOnItsOwnTimer_AndStopsIdempotently`'s first assertion,
and `Module_CheckpointsItsTracks_AndRestoresThemAfterRestart`) now use the
file's own `WaitUntil` helper to poll for the expected track count instead
of asserting on a single snapshot. `DrainSynchronously`'s comment rewritten
to explain the real race and point future readers at the `WaitUntil`
pattern. The other `DrainSynchronously` call in
`Module_AgesTracksOnItsOwnTimer_...` (after the module is stopped) was never
at risk, since nothing is running the pump by then, and was left as is.

Confirmed locally: 30 runs of `dotnet test Tests/Core/CoreTracks.Tests` in a
row, zero failures (previously reproduced in roughly 1 of every 3 to 5 runs
before this fix), plus a full `dotnet test Poseidon.slnx -maxCpuCount:1`
run, zero failures, no MSB4181.

Pushed and confirmed: CI passed on all three platforms (Windows, Arch
Linux, RHEL-compatible) on the next run. The slice 5 PR's CI is green.

### Slice 6 (close-out): requirement ledger, known limits and final verification, 2026-10-31

Per 21.5 item 6: full solution run, ledger rows, documentation of tuning
values and known limits, then PR and cross-platform CI (the PR's CI is
already green, from the fix pass above).

**Final passing run (2026-10-31, Windows):** `dotnet build Core.Application.proj`:
no warnings or errors. `dotnet test Poseidon.slnx -maxCpuCount:1`: zero
failures across every project, run twice (once directly, once via each
aggregate's nested rerun), including both aggregates and every CMS smoke
project. Unique test count across the whole solution: 430 (Contracts.Tests
94, Core.Tests 116, CoreTracks.Tests 74, CoreAIS.Tests 30, CoreRadar.Tests
34, CoreUI.Tests 28, CoreDisplay.Tests 19, CoreCharts.Tests 8,
CoreNavigation.Tests 8, CoreSonar.Tests 8, nine CMS smoke projects at 1
each, Civilian.Tests 1, Military.Tests 1).

**Requirement-to-test ledger (M1-E rework, R1 to R40 with R16a).** Test
classes are under `Tests/Core/CoreTracks.Tests` unless noted; `Contracts`
means `Tests/Shared/Contracts.Tests`.

| Requirement | Covered by |
|---|---|
| R1 (invalid reports rejected, carried over from E1) | `Contracts/TrackObservationRulesTests.InvalidObservations_AreRejectedWithAReason`, `PositionedObservations_AreAccepted_IncludingBoundaryValues`; `TrackManagerTests.InvalidObservations_AreRejectedWithoutCreatingTracks` |
| R2 (correlate or create) | `TrackManagerTests.UnknownSourceTrack_ThatCorrelatesWithNothing_CreatesATrackWithAGeneratedNumber`, `KnownSourceTrack_UpdatesItsContributionAndTheTrack` |
| R3 (join the one gated candidate) | `TrackManagerTests.ANewSourceTrack_InsideTheGateOfExactlyOneCandidate_JoinsItAsAContribution` |
| R4 (outside every gate creates a track) | `TrackManagerTests.ANewSourceTrack_OutsideEveryGate_CreatesANewTrack` |
| R5 (dominance threshold) | `TrackManagerTests.ANewSourceTrack_WellInsideOneCandidateAndFarFromAnother_JoinsTheDominantOne`, `ANewSourceTrack_EquidistantBetweenTwoCandidates_IsAmbiguousAndCreatesANewTrack` |
| R6 (never a second active/stale contribution from one source) | `TrackManagerTests.ATrack_NeverGainsASecondActiveContributionFromTheSameSource` |
| R7 (automatic split) | `TrackManagerTests.ContributionsThatStopAgreeing_SplitAfterTheConfirmationCount_NotOnATransientDisagreement` |
| R8 (Kalman fusion) | `TrackEstimatorTests.UpdatePosition_RepeatedConsistentMeasurements_ConvergeNearTheMeasurementWithShrinkingUncertainty`, `UpdatePosition_WeightsByUncertainty_ATighterMeasurementPullsTheEstimateHarder`; `TrackManagerTests.JoiningContributions_FuseIntoOneEstimate_WeightedByTheirReportedUncertainty` |
| R9 (late reports) | `TrackEstimatorTests.UpdatePosition_ALateMeasurement_IsAppliedAtTheCurrentTime_WithoutBreakingTheFilter`, `Predict_ReturnsNothingNewWhenCalledAtOrBeforeTheCurrentTime_NeverRollingBackward`; `TrackManagerTests.ALateReportFromAnotherSource_IsAppliedWithoutBreakingTheTrack` |
| R10 (duplicate/out-of-order, carried over from E3) | `TrackManagerTests.ExactRepeatIsDuplicate_AndOlderObservationIsOutOfOrder_WithoutChangingTheTrack` |
| R11 (source descriptor registration) | `Contracts/TrackSourceDescriptorTests.ValidDescriptors_AreAccepted_WithAndWithoutTheOptionalBearingValues`, `Kinds_AreOpenStrings_SoOtherModulesCanDeclareTheirOwn`, `InvalidDescriptors_AreRefusedWithAReason`; `TrackManagerTests.RegisteredSourceTimings_ApplyImmediately_AndSupersedeTheUnregisteredDefaults`, `UnregisteredSource_UsesThePolicyDefaults` |
| R12 (per-contribution ageing/state) | `TrackManagerTests.Track_IsActiveWhileAnyContributionIsActive_StaleWhenNoneAreActive_LostWhenAllAre` |
| R13 (removal, number retirement) | `TrackManagerTests.ATrack_IsRemovedOnlyWhenItsLastContributionIsRemoved_AndKeepsItsNumberMeanwhile` |
| R14 (disconnected source holds at Stale, carried over from E6) | `TrackManagerTests.DisconnectedSource_HoldsItsContributionsAtStale_NotLostOrRemoved_UntilItResumes` |
| R15 (rejoin after Lost) | `TrackManagerTests.ANewSourceTrack_FromASourceWhoseEarlierContributionIsLost_MayRejoinTheSameTrack` |
| R16a (position/polar/bearing acceptance, replaces R16) | `Contracts/TrackObservationRulesTests.RangeAndBearing_IsAcceptedWithTheOwnShipPosition_AndRelativeBearingsWithItsHeading`, `BearingOnly_IsAcceptedWithTheOwnShipPosition_AndRelativeBearingsWithItsHeading`; `Tests/Core/CoreRadar.Tests/RadarAdapterTests.WithoutAPoseOrWithARelativeBearingAndNoHeading_TheReportIsRejectedWithAProblem` |
| R17 (operator merge) | `TrackManagerTests.Merge_KeepsTheOlderNumber_MovesContributions_AndLaterReportsUpdateTheSurvivor`, `Merge_FailsWithAReason_ForAnUnknownTrack` |
| R18 (operator split) | `TrackManagerTests.Split_MovesOneContributionToANewTrack_AndReEstimatesBoth`, `Split_FailsWithAReason_WhenItWouldRemoveTheLastContribution`, `Split_FailsWithAReason_ForAnUnknownTrackOrContribution` |
| R19 (capacity, carried over from E7) | `TrackManagerTests.NewTracksBeyondCapacityAreRejected_ButExistingContributionsStillUpdate` |
| R20 (gapless feed/resync, carried over from E8) | `TrackManagerTests.Feed_OffersGaplessOrderedChanges_IncludingContributions_ThatConvergeWithTheSnapshot`, `Feed_RequiresResyncForUnknownEpochFutureRevisionOrTrimmedLog` |
| R21 (checkpoint/restore, schema 2) | `TrackManagerTests.Checkpoint_PreservesContributionsDescriptorsAndSourceHealth_AndRestoredTracksStillCorrelate`, `Restore_RejectsUnsupportedOrCorruptState`; `TracksModuleTests.Module_CheckpointsItsTracks_AndRestoresThemAfterRestart` |
| R22 (`TrackMotion.Predict`) | `Contracts/TrackMotionTests.Predict_MovesAlongTheCourseBySpeedTimesElapsedTime`, `Predict_AtTheMeasuredTimeOrWithoutSpeed_ReturnsTheTrackPosition`, `Predict_ReturnsNothing_WhenTheVelocityIsUnknown` |
| R23 (background registration/retry; `Unavailable` before) | `TrackIngressTests.Register_WithAnInvalidDescriptor_ThrowsArgumentException`, `Register_WithAValidDescriptor_ReturnsASinkBoundToTheSource`, `Register_TheSameSourceIdAgain_ReplacesTheEarlierSink_WhichStopsAcceptingPublishes`; `Tests/Core/CoreAIS.Tests/AisAdapterTests.RunningAdapter_NormalizesAndForwardsReportsAndHealthToTracks`, `Sequence_IsCheckpointedAcrossRestarts_AndUnsupportedStateIsRejected`; `Tests/Core/CoreRadar.Tests/RadarAdapterTests.Adapter_IsUnavailableUntilStarted_ThenForwardsReportsAndHealth`, `Sequence_IsCheckpointedAcrossRestarts_AndUnsupportedStateIsRejected`; `TracksModuleTests.Publisher_Register_ValidatesTheDescriptor_AndReturnsASinkBoundToTheSource` |
| R24 (source/Tracks independence; reload pick-up/drop) | `TracksModuleTests.TracksModule_RunsCorrectly_WithNoSourceEverRegistering`; `TrackPipelineIntegrationTests.AisAndRadar_OfOneVessel_FuseIntoOneTrack_WhichSurvivesReload_AndOutlivesEitherSource`, `WithoutTracks_TheTrackSensorsAreBlockedAndSonarIsUnaffected_ThenTheyRecover` |
| R25 (AIS descriptor; Radar interval from rotation rate) | `Tests/Core/CoreRadar.Tests/RadarAdapterTests.DescriptorFor_DerivesTimingsFromTheRotationRate_ScalingUpOnlyWhenSlow`, `SetAntennaRotationRate_ReRegistersTheDefaultChannelsDescriptorWithTheNewInterval` |
| R26 (bounded queue; Degraded/Healthy) | `TrackIngressTests.Publish_WhenASourcesQueueIsFull_DropsTheOldestReportAndCountsItInsteadOfBlocking`, `TrackSystemHealth_BecomesDegraded_WhileAQueueIsDropping_AndRecoversAfterTenSecondsWithoutDrops` |
| R27 (consumers keep working against the reshaped `Track`) | `Contracts/TrackReplicaTests` (all 11 cases, notably `ABearingTrack_WithNoPositionAndANonNullBearing_FlowsThroughSnapshotAndChanges`); `Tests/Core/CoreDisplay.Tests/TrackLayerTests` (all); `TrackPipelineIntegrationTests.AisAndRadar_OfOneVessel_FuseIntoOneTrack_WhichSurvivesReload_AndOutlivesEitherSource` |
| R28 (real-runtime end to end) | `TrackPipelineIntegrationTests.AisAndRadar_OfOneVessel_FuseIntoOneTrack_WhichSurvivesReload_AndOutlivesEitherSource`, `SourceDisconnect_IsVisibleInTheFeed_AndAnObservationRestoresIt`, `WithoutTracks_TheTrackSensorsAreBlockedAndSonarIsUnaffected_ThenTheyRecover` |
| R29 (concurrent sources, no starvation) | `TrackIngressTests.ConcurrentSources_EachKeepTheirOwnOrder_AndNoneStarvesTheOthers` |
| R30 (per-source statistics) | `TrackIngressTests.SourceStatistics_ExposeReceivedCreatedUpdatedRejectedDroppedQueueDepthAndLastRejection` |
| R31 (throughput baseline, measurement not a gate) | `TrackIngressTests.ThroughputBaseline_IsMeasuredAtOneThousandTracks_AsAMeasurementNotAGate` |
| R32 (radar position conversion from own-ship pose) | `Tests/Core/CoreRadar.Tests/RadarAdapterTests.TrueBearingWithPose_ProjectsAPositionAndKeepsTheMeasurement`, `RelativeBearing_UsesThePlatformHeading`, `WithoutAPoseOrWithARelativeBearingAndNoHeading_TheReportIsRejectedWithAProblem` |
| R33 (Bearing track creation) | `TrackBearingTests.BearingOnlyReport_ThatCorrelatesWithNothing_CreatesABearingTrack`, `BearingTrack_AgesAndIsRemoved_LikeAnyOtherTrack_AndItsNumberIsRetired` |
| R34 (bearing-to-point/hybrid correlation) | `TrackBearingTests.BearingReport_ThatCorrelatesWithExactlyOnePointTrack_JoinsItAsAContribution`, `BearingReport_AmbiguousBetweenTwoTracks_StaysStandalone_AndJoinsOnceTheAmbiguityClears`, `BearingTracks_NeverCorrelateWithEachOther` |
| R35 (bearing refines only when AIS-only; splits off) | `TrackBearingTests.BearingContribution_RefinesThePoint_WhenTheOnlyActiveRangedContributionIsAis`, `BearingContribution_DoesNotRefineThePoint_WhileARadarContributionIsActive`, `BearingContribution_ThatStopsAgreeingForTheConfirmationCount_SplitsOffAsABearingTrack`; `TrackEstimatorTests.UpdateBearing_WithAnActiveRangedContribution_NudgesThePositionTowardTheBearingLine`, `UpdateBearing_WithoutAnActiveRangedContribution_RecordsTheBearingButNeverMovesThePosition` |
| R36 (kind changes; hybrid extrapolation) | `TrackBearingTests.ABearingTrackWhoseSourceReportsARange_BecomesAPointTrack_KeepingItsNumber`, `ATrackWhoseRangedContributionsAllStop_WhileABearingContributionIsActive_BecomesHybrid`, `AHybridTrack_BecomesPointAgain_WhenARangedReportArrives`; `TrackEstimatorTests.Predict_WithoutMeasurements_GrowsPositionUncertaintyMonotonically_ForHybridExtrapolation`; `Contracts/TrackMotionTests.Predict_ReturnsNothing_ForABearingTrack` |
| R37 (Display bearing line) | `Tests/Core/CoreDisplay.Tests/TrackLayerTests.BearingLines_IncludeBearingAndHybridTracksOnly_StyledByEachTracksOwnState` |
| R38 (per-module hardware configuration file) | `Tests/Core/CoreRadar.Tests/RadarHardwareConfigurationTests.LoadChannels_WithNoConfigurationFile_ReturnsOneImplicitDefaultChannel` |
| R39 (per-channel descriptor/source ID; rejects an unknown channel) | `Tests/Core/CoreRadar.Tests/RadarAdapterTests.SourceIdFor_NamesTheDefaultChannelPlainly_AndOthersWithTheirOwnChannelId`, `Submit_WithAChannelIdThatIsNotConfigured_IsRejectedAsInvalid` |
| R40 (CoreRadar as the concrete multi-channel case) | `Tests/Core/CoreRadar.Tests/RadarHardwareConfigurationTests.LoadChannels_ParsesEachConfiguredChannel_WithItsOwnRotationRate`, `LoadChannels_RejectsDuplicateChannelIds_AsAMalformedFile`, `LoadChannels_RejectsANonPositiveRotationRate_AsAMalformedFile`; `RadarAdapterTests.SetAntennaRotationRate_OnAnUnconfiguredChannel_ThrowsArgumentException` |

Every row above is a real, passing test as of the final run; none is
against a stub. R32 predates this rework (carried over) and was not
re-tested here beyond what `RadarAdapterTests` already covers.

**Known gaps and follow-ups** (replaces the older
[m1-e-track-pipeline.md](m1-e-track-pipeline.md)'s own "Known gaps and
follow-ups", which this rework supersedes):

- **Display does not draw the bearing line's pixels yet.** `ChartViewport.BearingLines`
  (R37) exposes each Bearing/Hybrid track's geographic bearing-line data
  (own-ship position, true bearing, state), real and tested, but there is
  still no chart projection or canvas (M1-D) to actually draw it, or any
  other track symbol, on screen. Same gap as the pre-rework pipeline, not
  introduced here.
- **R31's throughput baseline is a correctness test, not a timed
  measurement.** `ThroughputBaseline_IsMeasuredAtOneThousandTracks_AsAMeasurementNotAGate`
  proves 1,000 tracks' worth of reports are all processed correctly; it
  does not yet record an actual reports-per-second rate as data. Turning it
  into a real timed measurement is still open (noted since slice 4).
- **The starting values in 21.4 are unvalidated against real sensor data.**
  The 200 m join/split distance, the per-source queue sizing (AIS 1,000 and
  radar 500 reports/s declared peaks), the process noise, the gate
  chi-squares and the direction-finder bearing uncertainty are all
  documented starting points the plan itself flags as "tuned later" once
  real recordings or hardware are available (21.4's own "Note" column).
  Nothing here changes that; this close-out does not attempt the tuning.
- **No real device protocol or recorded-file replay transport exists.**
  Adapters still accept reports pushed through their input feature only;
  AIS NMEA/radar protocol selection (master plan section 9) and the
  sensor-simulators feature (planned, tests not yet started) are what will
  eventually feed them for real. Same gap as the pre-rework pipeline.
- **A module's background pump/connector discards in-flight queued items
  on stop**, same as the pre-rework relay: `TrackIngress.StopAsync` and
  `TrackSourceConnector.StopAsync` cancel their loops immediately rather
  than draining what is still queued. Acceptable per the plan's reload
  design (21.7: "a generation reload stops every module first, so a sink
  reference cannot outlive Tracks"), not revisited here.
- **CoreSonar is still not a track source and has no hardware-configuration
  file reader of its own** (deferred in 21.13: civilian `Core.Sonar` is not
  a track source, T2). `CoreRadar`'s per-channel hardware-configuration
  mechanism (R38 to R40) exists and is tested; giving `CoreSonar` the same
  delivery mechanism for its own civilian channels (echo sounder, NDS
  sonar) without wiring them to Tracks is still open, per R40's own text.
- **EW/CMS bearing sources are out of scope.** Bearing association (R34)
  sits behind an internal seam specifically so CmsTracks can supply a
  different method for EW bearings later (CMS backlog); Core never
  cross-fixes bearings from one ship, by design.
- **M1-F (Navigation) has not started.** R32's radar-to-position conversion
  and R37's bearing line both fall back to the sensor report's own-ship
  pose until Navigation exists to supply a better one; this is expected,
  not a defect.

**Status.** M1-E rework (slices 1 to 6) is implemented, documented and
passing on Windows locally, with the slice 5 PR's CI green on Windows, Arch
Linux and RHEL-compatible containers. Presented to the user for review and
acceptance; M1-E is complete once accepted and the PR is merged.
