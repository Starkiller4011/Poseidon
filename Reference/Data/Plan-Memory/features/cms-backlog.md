# CMS backlog

Milestone: Long-term backlog (after the civilian side is complete)
Status: Documentation only. No CMS implementation is planned until work on the
CMS side starts, which is after the initial core application is complete
(decision D8 of 2026-10-26).

This file keeps the CMS requirements and design context that were in the former
application plan (sections 3, 6, 9 and 20) so nothing is lost while CMS is
deferred. When CMS work is scheduled, each part below becomes its own feature
file and goes through the workflow in
[development-workflow.md](../development-workflow.md).

## Consoles and activities (future CMS scope; former section 3)

Retain these requirements for later. The initial core application must not
depend on CmsCore, roles, distributed locks, or a coordination service.

- Consoles have assigned roles with default activity sets.
- Activities can be released and claimed from any console.
- Navigation defaults to the Bridge role and permits concurrent claims.
- Other activities are exclusive unless explicitly declared shareable.
- Role defaults are assignment defaults, not hard-coded capability visibility
  restrictions. Permission rules beyond the established ownership model need
  a separate product decision.

## Multi-console coordination proposal (long-term CMS backlog; former section 6)

This section preserves future design context only. It is not initial-core work.

Start with a single-process coordinator behind an interface. For multi-console
deployment, use one authoritative coordination service with atomic claim/release
operations and monotonically versioned state; do not implement locks with local
UI flags or peer broadcasts.

Proposed activity model: activity ID, concurrency policy, default roles,
owners, claim revision, and availability derived from deployed capabilities.
An exclusive claim rejects competing owners; concurrent Navigation claims
retain all owners. Releases verify the current owner and claim revision.

Network topology, authentication, persistence, lease duration, offline behavior,
and failover require confirmation before distributed implementation. Never
silently reassign an exclusive activity on disconnect; show unknown/unavailable
ownership until the agreed lease/recovery protocol resolves it.

## CMS track system requirements (confirmed 2026-10-26)

The shared track requirements (T1 to T10, decisions D1 to D8) are in
[m1-e-track-fusion.md](m1-e-track-fusion.md). The parts that belong to CMS:

- `CmsTracks` expands `CoreTracks` through extension points; it does not replace
  or duplicate it (T1).
- CMS adds track sources through its own adapters: fire-control radar,
  surveillance radar, EW, military sonar, and EO/IR tracking with a laser range
  finder. Civilian `Core.Sonar` stays out of tracking; `CMS.Sonar` is a track
  source (T2).
- CMS-only track data (T6):
  - Identity: Hostile, Suspect, Neutral, Friend, Assumed Friend, Unknown.
  - Classification by domain (subsurface, surface, air; for example Possub 1-4).
  - Link contribution and ownership.
- **Link contribution (D4).** Tracks from Link 11, Link 16 and Link 22 are one
  more source. When a reported track correlates with a local one, the link data
  is added like any other source, but the identity and classification the
  reporting unit sends override local values and the operator cannot change
  them until the link contribution is lost.
- **Bearing-only reports and bearing tracks.** Changed on 2026-10-26 (D9 in the
  M1-E fusion file): civilian search-and-rescue direction finders also produce
  bearings, so bearing tracks are a CoreTracks feature (amendment 21.12 in
  [m1-e-track-fusion.md](m1-e-track-fusion.md)). CMS adds the military bearing
  sources (EW racket bearings, EO/IR tracking without a laser range finder)
  through its own adapters. **The estimator that ranges a target from bearings
  over time (bearings-only target motion analysis) stays in CmsTracks** and
  plugs into a CoreTracks extension point. CoreTracks fuses bearings with point
  tracks and keeps Hybrid tracks (a point and a bearing line once range stops),
  but never ranges a target from bearings alone. Also CMS: cross-fixing
  bearings (it needs bearings taken far enough apart, which one ship cannot
  do), and a bearing association method for EW racket bearings, plugged into
  the seam CoreTracks leaves for it (the angular gate suits cameras, not EW). A sensor that reports range and bearing has its
  position calculated from the own-ship position and is a Core concern.
- **Display.** Core and CMS tracks share `CoreDisplay` for now; a `CmsDisplay`
  module may extend it later if CMS-specific features need it (T7).
- **Non-geodetic sources (D7).** Searched on 2026-10-26 (section 21.11 of the
  M1-E fusion file). Civilian range-and-bearing sources are common and are
  converted to positions in Core. Civilian bearing-only sources exist only in
  search and rescue. The bearings-over-time estimator is CMS-only (D9).
- Higher update rates are not CMS-only; they are a `CoreTracks` goal (T6).

## Related decisions (from former section 9)

| Decision | Provisional position | Resolve before |
|---|---|---|
| Console topology | Deferred with CMS; preserve host extension points without implementing coordination now | Future CMS milestone |
| CMS scope | Preserve extension boundaries; no operational weapon functions in initial scope | Any CMS feature expansion |
| Core/CMS track relationship (decided 2026-10-26, details open) | CmsTracks extends CoreTracks and adds military sources; correlation and fusion live in CoreTracks for both products | Open questions in m1-e-track-fusion.md |

## Open items

1. Identity and classification extension shape in `CmsTracks`.
2. Whether civilian non-geodetic sources exist (D7).
3. Link 11, Link 16 and Link 22 correlation rules beyond the identity override.
4. CMS projects and their tests currently lack manifests and test alignment
   (they were renamed and expanded; see the CMS paragraph of the M1-A file).
