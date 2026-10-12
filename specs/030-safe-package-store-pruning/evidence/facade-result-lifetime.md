# Public facade result lifetime

The participant assembly catalog and type finder already materialize through the scoped loader/provider. The actual load context owns a counted graph-use lease. Returned assemblies and types retain that context, so requesting unload cannot release native graph protection while the caller can still use those results. No additional lifetime type or production behavior change is required for this increment.

## Local proof

Two tests call the real `IPackageAssemblyCatalog.GetPackagedAssembliesAsync` and `IPackageTypeFinder.FindTypesAsync<object>` over a participant-loaded graph. Each verifies that the returned Assembly/Type belongs to the context carrying the exact graph lease. The matching native use record is bound to the same graph/generation/install nodes. After disposing the caller owner and requesting unload, the test retains only the facade result, forces collection, verifies the result is usable and its context is alive, and confirms that the same use record remains Live and its sentinel remains locked.

The result is then released. After weak-context death, the test probes that exact native sentinel directly until it can acquire the lock. No new Maintenance admission or inspection occurs between dropping the result and observing sentinel release. A later non-recovering inspection observes the same record as Stale. Record bytes, both member state payloads and install identities/payloads remain unchanged. Native locked-then-unlocked probes provide the held/released causal comparison. The ordinary fixture release waiter is only failure cleanup.

Root focused tests passed 2/2, followed by the combined required retained Loading gate passing 32/32, with no failures or skips. The 32-name manifest preserves all prior 30 names and appends the two actual public-facade cases. Root and independent review found no retention or teardown flaw. The existing internal, unregistered `PackageTypeScanner` async path consumes the same catalog references; its synchronous exact-version path consumes the same context-bound provider. The public TypeFinder result is the composed production type-scanning surface.

Fresh exact-head hosted qualification is pending; T094/T096 remain unchecked until that proof is accepted. This is collectible facade lifetime evidence. Admitted host-integrated loading and its noncollectible process lifetime remain separate T097/T099 work. Complete T100/T101, recursive deletion and public pruning activation remain unfinished.
