# Public manual maintenance boundary

`AddNuplanePackageStoreMaintenance` explicitly registers the optional
`INuplanePackageStoreMaintenanceOperations` surface. Ordinary `AddNuplane` and
`AddNuplaneAdmin` do not register it; existing `INuplaneAdminOperations` remains
unchanged. `MapNuplanePackageStoreMaintenance` separately maps enrollment preview,
inspection, explicit enrollment/recovery and prune endpoints and returns the route group for the host to apply
its authorization policy. No maintenance timer or automatic endpoint mapping is
introduced.

Prune requests select a configured label and an optional nonnegative retention
policy. Preview is the default; omitted policy retains every install, including
when Execute is explicitly selected. Execute confirmation must match the label,
physical root, enrollment epoch and exact retention request. The core reopens
native authority and freshly plans while holding all required member ownership;
a preview never supplies deletion candidates or grants a reusable capability.

Results distinguish preview eligibility from actual attempts, physical outcomes,
partial removal and journal state. Cancellation after physical work begins
retains observed outcomes. Repetition safely recognizes prior removals. Request
DTOs reject unknown JSON properties instead of accepting arbitrary root or
candidate paths. Enrollment observations expose detached native identities,
state-slot facts and state digests as descriptive input for later explicit
cutover verification; they do not declare quiescence.

## Qualification state

The ten public native/API cases cover preview preservation, missing-policy
retention, protection added after preview, confirmation refusal, actual endpoint
deletions, repeat execution, partial cancellation, opt-in routing, blank labels
arbitrary-path rejection, a real two-member initial cutover and recovery-evidence JSON transport. Their names are included in the existing strict
retained-loading manifest for every supported native lane.

Local attempt 002 passed six cases and failed two JSON endpoint cases because
the test harness omitted `IHttpRequestBodyDetectionFeature`. The harness now
models readable request bodies. Attempt 003 stopped at compilation because the
new enrollment DTO exposed an internal state-slot type. That projection now uses
a detached public value. Neither failed attempt is product acceptance. Fresh
hosted qualification of this corrected candidate is required. Core initial enrollment/recovery is integrated through configured declarations and a retained native owner. Migration orchestration is still being integrated; this boundary
alone does not complete Spec 030 or authorize a release.
