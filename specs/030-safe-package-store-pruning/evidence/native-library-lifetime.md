# Actual deferred packaged native-library lifetime

Test-only worker commit `55a5eeb0fc0426c5eb1cbe8c0a2a908e198e3851` is integrated
locally as `c7bb82b`. The fixture compiles a tiny owned C export into
`Shared.Dependency/runtimes/<current RID>/native` before the completion marker,
enrollment and graph protection are captured. Only Root.Second v1 declares its
P/Invoke; ordinary fixture defaults are preserved.

The test uses actual participant DI and `IScopedPackageLoader`, retains the exact
loaded marker Type, disposes the external graph-use owner and retires the old loader
context. Real reconciliation commits Root.Second v2. Coordinated state verification
proves the exact old Root.Second v1 install is absent from Active and recoverable
LKG graphs before the **first** invocation of the packaged native export.

The export returns its input-dependent expected result. The retained Type still
belongs to the exact old context; its whole-graph use remains Live and its sentinel
cannot be acquired. Clearing that Type permits actual collectible weak death,
followed by direct acquisition of the same native sentinel before maintenance
inspection. The subsequent inspection reports Stale; state, use-record bytes and
install evidence remain unchanged.

## Evidence and limits

Final focused attempt005 on Darwin arm64 passes **1/1**, no skips. Earlier compile
and unscoped-test-state-read failures are preserved separately; attempt004 also
passed before final helper corrections. The final compiler helper resolves Windows
`cl.exe` to an absolute path from the initialized VsDevCmd PATH and bounds compiler,
post-termination and output-drain waits. Missing compiler/toolchain fails this proof
instead of skipping it or substituting a system library.

The strict retained-Loading manifest preserves all prior50 cases and adds the exact
native-export row. At `a7a27d2`, the root combined suite passes **51/51**, zero failed
or skipped; its exact-name verifier accepts every required row once and Passed.
Independent source review verifies the merged fixture preserves both slices and
ordinary defaults. Hosted platform qualification remains pending.
The old Root.Second v1 is live-use-only after reconciliation; Shared.Dependency and
its native asset may remain protected by other Active/LKG graphs, so this evidence
does not claim that the native asset itself has only live-use protection.

Windows, Linux and Intel macOS execution remain unqualified at this checkpoint.
Existing HostIntegrated process-exit lifetime proofs remain separate; this new test
does not make an actual native call through a HostIntegrated context. Full T086
acceptance and public pruning activation remain open.
