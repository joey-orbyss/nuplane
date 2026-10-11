# Local pending publication recovery through production admission

Status: source reviewed and root-qualified locally, 2026-10-11. Exact-head native qualification of this test/CI increment is pending. Full T116/T117, schema-2 activation, public standalone SaveAsync routing and physical pruning remain unaccepted.

## Actual process proof

Three cases reuse the existing acknowledged two-member native process fixture and actual `CompleteEnrollmentAsync` with explicit quiescence. The prior ledger is Complete/schema-1, both state files are outside the install root, and both members have verified acknowledged protection. The existing publisher child calls the real registry publication API and pauses at a disk-verified checkpoint before owned termination.

A fresh AddNuplane provider explicitly registers the trusted root catalog. Its production configured-root Reconciliation admission recovers pending publication before returning a capability. `PendingPublished` restores exact Prior: original Complete ledger digest, target state digest/identity and protection revision 1. `StatePublished` commits exact Next: the actual newly published state identity, day-7 body and protection revision 2. Both return Complete/schema-1, retain exact member/epoch binding, remove transaction artifacts, preserve the sibling state's native identity/digest/protection, and permit another admission after the recovered capability drains.

At `StageFlushed`, the pending ledger does not bind the staged file identity. Production admission returns typed UnknownAuthority and preserves all captured native identities and captured file bytes/digests. Root/member lock identities are captured; their bytes are not read. No capability escapes. Child readiness/checkpoint/process identity and actual exit are checked, with a finite deadline and owned teardown that retains primary and distinct cleanup failures.

The fixture has known-empty Active/LKG closures. These cases prove preservation of the offline sibling record and those closures; they do not prove preservation of a nonempty sibling package graph, other crash points, ordinary non-catalog admission, or group/schema-2 recovery. The production pending caller already existed; this increment changes tests and CI qualification, not production behavior.

## Causal and local qualification

Root compiled omission of only `RecoverCatalogPendingAsync` from catalog admission, preserving subsequent full catalog/state/schema verification. Both Prior/Next success cases failed at full catalog verification because the retained roots remained pending; the ambiguous-stage refusal still passed. The production source was restored byte-for-byte: SHA-256 `5cdcbfc3f9251d69ffff6da5685fc34440af929c573d6b60fa92edc47e7f5c99` before and after.

The initial focused run passed all three cases and exposed two nullable warnings in new assertions. Explicit non-null checks corrected those warnings before the compiled control and restored full Release/net10 Integration run. That full run passed 241/241 with no failures/skips and no new nullable warnings. All 238 prior case names, outcomes and diagnostic outputs are unchanged; exactly the three new cases were added.

The native process workflow retains every previous 33-case requirement and adds the three exact actual TRX names in `build/test-cases/pending-v1-admission.json`, requiring 36 unique passing cases. Its exact Python verifier passed against the actual 36-case subset of the full run. The unchanged strict retained-loading manifest also passed against its actual 25-case subset of the same run. These are derived verification subsets, not separate test runs.

Actual native Windows/Linux/macOS x64/ARM64 qualification remains required for this new test/CI increment. The earlier stale-use recovery qualification is separate and does not qualify these new cases. No v2 runtime fence was relaxed, no public SaveAsync route changed, and no physical package deletion was implemented. The first owner support-policy card remains unanswered.
