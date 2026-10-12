# Participant catalog reads

This bounded increment implements active-catalog reads and descriptive loaded-package projection for the explicit coordinated composition. It does not activate maintenance execution or complete T100/T101.

## Behavior

`ActivePackageCatalog` retains its public ordinary constructor. The participant DI factory validates the selected provider, registry, serializer, admission and frozen roster before constructing the catalog. A read acquires non-recovering configured-root Maintenance ownership across the full catalog, requires matching enrolled ownership, and reads the configured member through the coordinated registry. Pending, corrupt or incomplete peers refuse without recovery or mutation. There is no ordinary fallback.

Loaded projection joins the exact current session, context and captured native install projection under the loader's existing context monitor. It enumerates candidates under that context's existing graph-use lease. It returns descriptive references without materializing assemblies. Expired or mismatched sessions refuse before scanning. Ordinary projection keeps its existing path.

## Local evidence and limits

Worker commit `5712ab88180674ac62333d331817bf44219f72a5` passed six focused Runtime cases and two Integration cases without skips. A compiled omission of the active-catalog factory validation caused the admission-only substitution regression to fail; the registry-only case continued to refuse through its existing admission factory. The production file was restored byte-for-byte and all six Runtime cases passed again.

Root integration strengthens the positive two-root case by checking returned install paths against the selected member, and extends the existing strict manifests from 57 to 63 coordinated Runtime cases and from 27 to 29 retained Loading cases. Every prior case remains required; selectors are unchanged. The combined local Runtime gate passed all 63 required cases, and both catalog Integration cases passed without skips.

The affected Loading tests passed 12 cases, including the eight required participant-composition cases. A test-only cleanup correction waits for the ordinary collectible context to be collected before deleting its fixture directory; it shares the existing bounded collection helper. These local results do not establish Windows qualification or acceptance of all 29 retained Loading cases. Fresh exact-head hosted qualification remains pending and is tracked on [issue108](https://github.com/valence-works/nuplane/issues/108) and [PR112](https://github.com/valence-works/nuplane/pull/112).

The original extraction and release delivery is separate. Participant startup recovery, complete driver coverage, quarantine/deletion recovery and public maintenance remain open.
