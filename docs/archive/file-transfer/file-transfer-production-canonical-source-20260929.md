# Production canonical protected-source custody — 2026-09-29

## Scope and first missing edge

The preceding canonical-role checkpoint binds both original app launches, but
protected pre-Send source acquisition still existed only in the explicit test
loader. This phase promotes that existing acquisition and lifetime check. It
does not enable live dispatch, sender native-read delivery, destination adoption,
writer/payload handoff, or route acceptance.

Baseline: `production-canonical-roles-20260929/FINAL-VERIFICATION.json`, SHA256
`1760eabd944cbf48becde6850814d6a30a414076e998e52fc2f300bcd508c974`.
All 10,146 baseline pins matched before implementation; HEAD remained
`a653d4063070235c71fe211480aeb37863d9b2a4` on `v0.7.0`, with 641 existing dirty
entries. Before-images preserve every pre-existing file edited in this phase.

## Bounded implementation

- Normal loading now includes `LocalGuiWriterCanonicalSource.cs`. The source
  token is non-constructible publicly and has no public setters. Acquisition
  requires the original consumed attempt and both original role launches.
- Acquisition remains one-shot and accepts exactly 268,435,456 bytes. The
  original read-only file handle denies writes and replacement; the SHA and
  native file identity describe that protected file, not a later path lookup.
- The original cycle calls `Get-FileTransferTunaSourceHash` after payload creation
  and before Send. Its private optional attempt/request inputs acquire custody
  and reuse that SHA instead of performing a second hash. Omitted custody uses
  the original hash function and preserves ordinary scenario behavior.
- Explicit attempt disposal retires source custody outside the owner lock.
  Owner teardown still closes native dispatch ownership only, preserving the
  established lock order. Cancellation or a closed underlying handle invalidates
  the source permanently; copied objects cannot retire the original.
- Existing writer-association and sender/payload fixtures use this production
  source implementation through thin test adapters. Their additional writer
  association check is compiled only in the explicit test profile.

The source's live-proof, transfer-integrity, and publication-completion flags
remain false. Hashing or holding a source is not proof that the sender data pump
read it, nor proof of destination ownership or a completed route cycle.

## Verification

The test-project build passed with 343 existing warnings and zero errors. The
initial normal-loader regression failed on both PowerShell hosts with
`Production protected source missing`, reproducing the missing edge before
implementation.

All 16 distinct new checks are green: two normal-loader API checks and seven
integration cases on each host. Cases cover valid custody, wrong size, an open
writer, cancellation, a closed handle, explicit disposal, and default hashing.
Embedded controls reject copied/wrong-role/null requests, copied attempts and
sources, repeats, and revival. Hash counts confirm that successful custody
reuses its SHA, while omission invokes the ordinary hash once. A further
two-host valid-case check verifies immediate file release before any status
query could lazily retire it; those reruns do not count as new distinct tests.

The exploratory run recorded six passes and ten fixture failures. Its privacy
assertion mistook a pre-existing script-text variable for a native capability;
the Windows PowerShell parent also lacked `Get-FileHash` in the fixture's
stripped environment. The assertion now checks actual capability types and
independent corroboration uses a SHA256 stream directly. All ten affected cases
passed on rerun (4m11s); no production condition was relaxed. The immediate
release check passed on both hosts (50s).

The directly affected compatibility lane passed all 22 cases in 6m31s: 14 prior
source-custody/loader checks, two normal role-loader checks, four ordinary
valid/default scenario launches, and two existing test-only sender metadata-read
handoffs. It was run once. The final total is **38 distinct green tests**, with
no remaining failures or skips. No aggregate or full GUI suite was run for this
bounded source-acquisition edge.

Tests use original parent/wrapper/GUI launch plumbing and headless role children.
They exercise only the extracted production payload/hash/Send ordering, with a
mocked Send boundary; they perform no network transfer or desktop automation.
Mechanical comparison confirms that the acquisition body and role/one-shot
checks were moved without behavioral changes. The phase verifier also checks
PowerShell syntax, whitespace, unchanged live entry, prior pins/dirty entries,
and recorded child-process cleanup.

Five new repository files bring the dirty-entry count to 646. The final seal
records the 11 edited pre-existing text files, the rebuilt test DLL, and all new
phase evidence; before-images retain their exact baseline contents.

## Remaining gate

The real top-level call remains
`Run-ScenarioFileTransferTunaHandoffFallback -Context $ctx`; it passes neither
owner nor canonical attempt. Next is the original sender's protected native-read
handoff, followed by destination custody and the remaining writer/payload chain.
Only after those edges and a reviewed clean checkpoint may fresh portable and
strict live preflight be considered. No acceptance/budget changes, portable
rebuild, live NKN run, full GUI matrix, commit, or push belong to this phase.
