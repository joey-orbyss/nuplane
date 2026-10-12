# Admitted active-package snapshot

The additive `NuplaneStore.OpenActivePackageSnapshotAsync` API takes a sealed `NuplaneStoreSnapshotAccess` resolved from the validated participant composition. It observes active descriptors without granting package authority, acquires one non-recovering full-catalog session in canonical physical-root order, and rereads the selected state member under that same ownership. The configured state root is explicitly included even when the active set is empty. It compares the serialized payload, protection revision/binding and active descriptors before publishing the scope; every active install must be enrolled.

The returned `AdmittedActivePackageSnapshot` exposes a copied read-only projection and counted borrows for captured paths. Repeated disposal shares one task and waits for existing borrows after closing new ones. Refusal releases selected and peer catalog ownership. Existing ordinary state/descriptor overloads remain pure observational readers and require no participant/native composition. Initial malformed state retains the ordinary JSON/I/O error behavior; membership and stable-state failures are typed admission refusals.

## Local proof

Source commit `72eaf25`, integrated as `fa1c3f1`, changes nine source/test paths. Root and independent review matched the same final source pins. Focused snapshot tests passed11/0/0, then the combined restored coordinated Runtime suite passed81/0/0. The 81-name required manifest retains every prior70 scenario and adds the eleven actual snapshot cases. Full Loading regression passed267/0/0 (267 distinct TRX test IDs; two existing long JSON parameter displays truncate to the same name). Nuplane builds for net8.0, net9.0 and net10.0 with zero warnings/errors.

The tests exercise selected-member routing, protected empty-state ownership, unchanged state/ledger/use evidence, pending/corrupt/incomplete peers, substituted DI facades, pre-admission cancellation, immutable descriptors, rejected foreign path borrows, repeated draining disposal, and peer ownership reacquisition after release. A real coordinated state mutation between initial observation and admission produces `StateMismatch` and drains both selected and peer ownership without further mutation.

A compiled control suppressed the stale-state comparison for an uncanceled caller. The stale-revision regression failed with the intended missing-refusal assertion. Its unexpected-return branch disposes the scope, preserving teardown even under the defect. Production source was restored byte-for-byte, rebuilt normally, and passed the 81-case gate; the restored assembly hash differs from the mutant. The initial compile's two type/name errors and corrections are retained as unaccepted intermediate evidence.

## Boundaries

Fresh hosted qualification remains pending, and T090/T098 are not yet accepted. The cancellation case does not prove cancellation after native ownership acquisition. Native absent-member empty state and path-identity substitution races are not exercised by these eleven cases. Actual host-integrated graph-use handoff remains T097; raw snapshot paths are never long-lived graph leases. Whole T100/T101, recursive deletion, public pruning activation, release and downstream adoption remain separate unfinished gates.
