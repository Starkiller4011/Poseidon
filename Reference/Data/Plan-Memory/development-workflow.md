# Development workflow

Date: 2026-10-26

This is how every feature in Poseidon is developed. It applies to new features,
reworks and behavior changes. Urgent production fixes are the only exception,
and even they add a reproducing test before the fix.

Work moves through three steps in order. **Each step ends at a gate**: when a
step's output is ready, stop and wait. Do not begin the next step until the
user has said the step is acceptable and given the order to continue. Approval
of one step is not approval of the next, and an earlier "go ahead" does not
carry over.

```text
1. Plan  --gate-->  2. Tests  --gate-->  3. Code  --gate-->  Done
```

## Resume context

A single file, `resume-context.md`, lets work resume from the files alone. It
is not a log: it is rewritten so that it always describes the present.

- **At the start of every session**, before anything else, read
  `resume-context.md`, then the master plan, then the feature file it points
  to.
- **It contains four parts**: the primary resume context (what the project
  is, how to find things, the files to read and the repository conventions a
  new session needs); the state of the feature in progress (which of the three
  steps are done, in progress or not started, what remains in each, and the
  state of the gate), so a session that ends in the middle of a feature, for
  example with planning complete and tests and code still to do, can be picked
  up exactly where it stopped; the last changes made (what was done most
  recently, the last verification run, and anything uncommitted); and what
  needs to be done next (the next action, open questions and anything waiting
  on the user).
- **Keep it current throughout the session**: update the feature state, the
  last changes and the next steps whenever the feature or step changes,
  progress within a step changes, a gate is reached, the user answers a
  question or decides something, and before the session ends. It is a working
  record, not a report written at the end.
- **Replace, do not append**: when something new is done, rewrite the "last
  changes" part to describe it and move what is still true into the feature
  file. History lives in the feature files, the ledger and git.
- It points to the feature file for the plan and requirements; it does not
  repeat them.
- Use the template at the end of this document.

## Step 1: Plan

Output: one planning file for the feature, created before any test or code.

- Location and name: `Plan-Memory/features/<milestone>-<slug>.md`, for example
  `features/m1-e-track-pipeline.md`. One file per feature; a large feature may
  be split into several, each with its own requirements.
- Create it from the template at the end of this document.
- The file records the problem, scope and non-goals, the design and the
  decisions behind it, the requirements, and the open questions. Requirements
  have stable IDs (for example `R1`) and are written as observable behavior,
  including failure, recovery, concurrency, lifecycle, platform and cleanup
  cases where they apply.
- Design decisions the user has not confirmed are marked as assumed. Anything
  that needs the user's decision goes in the open questions, and the plan is
  not ready for the gate while a question that affects the design is open.
- Update the top-level plan (see below) with a link to the feature file and
  its status.
- Gate: present the plan and wait. The user may ask for changes; revise the
  plan and present it again. Only an explicit order to continue moves on.

## Step 2: Tests

Output: the executable tests for every requirement in the approved plan, and
nothing else.

- Write the smallest appropriate test for each requirement (contract, unit,
  integration, aggregate or fixture-backed), named after the behavior. Tests
  exercise public behavior wherever practical; white-box seams are acceptable
  only for simulating operating-system events or failure paths.
- Production code is limited to what the tests need in order to compile, such
  as empty types or members that throw `NotImplementedException`. It must not
  contain behavior.
- Run the tests and confirm each one fails because the behavior is missing, not
  because of how the test is set up. Record the failing run (which tests failed
  and why) in the feature file's ledger.
- Every requirement has at least one test, and every test traces to a
  requirement. Add the requirement-to-test mapping to the ledger.
- If writing a test shows the plan is wrong or incomplete, stop, update the
  plan and return to the Step 1 gate. Do not quietly change what a requirement
  means.
- Gate: present the tests, the mapping and the failing run, and wait.

## Step 3: Code

Output: the production code that makes the approved tests pass.

- Implement only enough behavior to satisfy the tests, preserving existing
  tests and the architecture boundaries in the top-level plan.
- Do not change the approved tests to make them pass. If a test is wrong, stop
  and return to the Step 2 gate with the reason.
- Run the focused tests while working, then the owning project and every
  affected aggregate suite, then the full solution. A passing build or a
  coverage figure does not replace assertions over the required behavior.
- Update the ledger with the passing results and update documentation that the
  change makes inaccurate.
- Gate: present the result with the evidence (what was run and the totals) and
  wait. The feature is done only when the user accepts it. Cross-platform CI
  results are recorded when they are available.

## Rules that apply to every step

1. **One step at a time.** Never start a later step, or prepare its files, ahead
   of the order to continue.
2. **Changes go backwards through the gates.** A change to requirements goes
   back to Step 1. A change to a test goes back to Step 2. The user approves
   the revised step before work continues.
3. **Ask by writing the question down.** Questions for the user are written in
   the conversation and in the plan's open questions, then work stops until
   they are answered. Do not answer them by assumption.
4. **Keep the record current.** The feature file holds the plan and ledger and
   `resume-context.md` holds where the work stands; both are updated as things
   change, so work can resume from the files alone.
5. **Keep changes in scope.** Do not fix unrelated problems in the same
   feature; note them in the plan as follow-ups.
6. **Nothing is committed without being asked.**

## Where things live

All files are in `Reference/Data/Plan-Memory/`.

| File | Role |
|---|---|
| The master plan | Purpose, confirmed architecture decisions, ownership and data flow, milestones, cross-cutting decisions still open, and links to the workflow, the resume context and each feature file with its status. Kept short |
| `development-workflow.md` | This document: the steps, the gates and the templates |
| `resume-context.md` | How to resume: the primary resume context, the last changes made and what needs to be done next |
| `features/<milestone>-<slug>.md` | One feature: plan, requirements, decisions, ledger and status |

Facts that span features (module naming, extension points) belong in the master
plan; facts that belong to one feature stay in that feature's file; the current
position and the next action stay in `resume-context.md`. The test-first rule is
this workflow; the master plan refers to it rather than repeating it.

## Feature file template

```markdown
# <Feature name>

Milestone: <for example M1-E>
Status: Planning | Awaiting plan approval | Tests in progress | Awaiting test approval |
        Code in progress | Awaiting code approval | Done

## Problem and scope
What this feature does and why. Non-goals.

## Design
The approach, the contracts and modules it touches, and the alternatives
considered. Mark unconfirmed decisions as assumed.

## Requirements
| ID | Requirement |
|---|---|
| R1 | Observable behavior, including failure and recovery |

## Decisions
Confirmed answers from the user, with the date.

## Open questions
Questions that must be answered before the plan can be approved.

## Ledger
| Requirement | Test | Failing run (Step 2) | Passing run (Step 3) |
|---|---|---|---|

## Status and follow-ups
Where the work stands, known gaps and out-of-scope items.
```

## Resume context file template

```markdown
# Resume context

Updated: <date>

## Primary resume context
What the project is in a few lines, the files to read in order (workflow,
master plan, the current feature file), where the source and tests live, and
the conventions a new session needs (build and test commands, platforms,
rules such as "nothing is committed unless asked").

## Feature in progress
Feature: <feature file link>, or "none in progress".

| Step | State | Detail |
|---|---|---|
| 1. Plan | Not started / In progress / Done (approved <date>) | What remains |
| 2. Tests | Not started / In progress / Done (approved <date>) | Requirements covered so far and what remains |
| 3. Code | Not started / In progress / Done (approved <date>) | Slices or requirements done and what remains |

Progress within the current step: what is finished and what is next.
Gate: <waiting for the user to approve ... | approved on <date> to continue to ...>

## Last changes made
What was done most recently, the files changed and whether anything is
uncommitted, and the last verification (command, result, date).

## What needs to be done next
The next action, the open questions, and anything waiting on the user.
```

