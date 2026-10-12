# Native owner-aware local-feed resolution

Worker commit `44b5e194cef7e41a615e7f5effd761158925cad8` is integrated locally
as `5a2e548`. The scoped `MultiFeedPackageResolver` now enumerates local feeds,
selects versions and copies the selected archive through the existing owner-aware
native directory and package-stream seams. Ordinary resolution retains its managed
route; filename parsing and exact/range version selection are shared.

The selected candidate binds its native directory identity/name profile, exact
basename, file identity and length. A fresh native pin and exact checks precede
staging; copy reopens the same identity, enforces the shared 128 MiB archive bound
and validates the retained evidence again. The native install callback shares the
already validated root instead of re-entering its serialized gate. A changed or
uncertain selected candidate refuses without trying another feed.

An external directory with positively absent membership authority is allowed under
the borrowed operation. Encountering a different enrolled root remains RootMismatch;
Incomplete authority and directory aliases refuse. These changes preserve the
member replay scope rather than treating the borrowed root as an arbitrary external
feed's required physical root.

## Local evidence

The native macOS run passes **10/10**, zero failed or skipped. It exercises actual
participant DI reconciliation, range selection, local-first acquisition, lock/hash
verification, graph activation and Loading; normalized mixed-case exact selection
from an external unenrolled feed; replacement before staging; matching-directory,
foreign Complete/Incomplete root, directory-alias and symlink/hardlink refusal.

The tenth case drives the real native copy through an awaited destination barrier,
starts owner disposal during the pending copy, cancels, drains the borrow and
reacquires ownership. This proves native-copy cancellation/ownership lifetime; it
does not separately prove resolver staging cleanup or a pending-publication feed
fixture.

Affected ordinary Runtime suites pass **107**, with one Windows-only case skipped
on macOS and zero failures. Direct Restore admission passes **12/12**, no skips.
Root independently read each TRX. Failed setup and earlier draft attempts remain
preserved under external program evidence `local-feed-routing/`. A test-only null
forgiving annotation follows the native run; subsequent affected-suite builds
compile the corrected test source.

The strict retained-Loading manifest preserves all prior40 cases, adds the exact
ten native rows, and subsequently adds the actual packaged native-library lifetime
case. At `a7a27d2`, the combined retained-Loading suite passes **51/51**, zero failed
or skipped, and the exact-name verifier accepts the TRX. Integrated affected Store
authority/admission/package-install suites pass **73**, zero failed, with three
ext4-only skips on macOS. Independent source/result review found no bounded blocker.
Hosted native-platform qualification remains required. This evidence does not
accept full T071 or public maintenance activation.
