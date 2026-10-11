using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Maintenance;

/// <summary>Replans under retained full-catalog ownership before invoking internal candidate work.</summary>
/// <remarks>
/// This internal boundary accepts explicit epoch and retention inputs, never a preview as authority.
/// Candidate work must independently bind and revalidate native install evidence before any mutation.
/// Quarantine recovery and public execution are not implemented by this boundary.
/// </remarks>
internal sealed class PackageStoreExecutionService(
    RootMembershipRegistry registry,
    PackageStoreInspectionService inspection)
{
    internal async Task<TResult> PlanAndRunAsync<TResult>(
        string? rootLabel,
        long expectedEnrollmentEpoch,
        int keepNewestInactiveVersionsPerPackage,
        Func<Context, CancellationToken, Task<TResult>> candidateWork,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidateWork);
        if (expectedEnrollmentEpoch <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedEnrollmentEpoch));
        if (keepNewestInactiveVersionsPerPackage < 0)
            throw new ArgumentOutOfRangeException(nameof(keepNewestInactiveVersionsPerPackage));

        return await inspection.RunUnderCatalogOwnershipAsync(rootLabel, async (owner, borrow, configuredRoot) =>
        {
            using var operation = borrow.EnterOperation(registry);
            var selectedRoot = owner.ResolveConfiguredRoot(borrow, configuredRoot)
                ?? throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.IncompleteEnrollment,
                    "Execution requires a positively enrolled configured root.");
            if (selectedRoot.EnrollmentEpoch != expectedEnrollmentEpoch)
                throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.RootMismatch,
                    "Execution confirmation does not match the current enrollment epoch.", selectedRoot.RootIdentity);

            var snapshot = await inspection.InspectUnderCatalogOwnershipAsync(owner, borrow, configuredRoot,
                keepNewestInactiveVersionsPerPackage, cancellationToken, operation).ConfigureAwait(false);
            var plan = snapshot.RetentionPlan
                ?? throw new InvalidOperationException("An enrolled execution plan is required.");
            if (plan.Root != selectedRoot.RootIdentity || plan.EnrollmentEpoch != expectedEnrollmentEpoch ||
                plan.KeepNewestInactiveVersionsPerPackage != keepNewestInactiveVersionsPerPackage ||
                plan.InventoryStatus != PackageStoreRetentionInventoryStatus.Complete ||
                plan.ProtectionKnowledge != PackageStoreRetentionProtectionKnowledge.Known || plan.HasUnresolvedEvidence)
                throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnknownAuthority,
                    "Execution requires a fresh complete retention plan with known protection and exact confirmation.",
                    selectedRoot.RootIdentity);

            var context = new Context(registry, borrow, operation, configuredRoot, snapshot);
            context.Revalidate(cancellationToken);
            var result = await candidateWork(context, cancellationToken).ConfigureAwait(false);
            context.Revalidate(cancellationToken);
            await registry.VerifyCatalogMemberProtectionUnderOperationAsync(borrow, operation, cancellationToken)
                .ConfigureAwait(false);
            context.Revalidate(cancellationToken);
            return result;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Live planning scope; escaped snapshots remain descriptive and native access expires with the operation.</summary>
    internal sealed class Context(
        RootMembershipRegistry registry,
        RootMembershipRegistry.NativeCatalogOwnerBorrow borrow,
        RootMembershipRegistry.NativeCatalogOwnerBorrow.Operation operation,
        TrustedPackageStoreRoot configuredRoot,
        PackageStoreInspectionSnapshot snapshot)
    {
        internal PackageStoreInspectionSnapshot Snapshot { get; } = snapshot;

        internal PhysicalStoreDirectoryHandle RootHandle
        {
            get
            {
                Revalidate(CancellationToken.None);
                return borrow.Owner.ResolveConfiguredRoot(borrow, configuredRoot)!.PrimaryResolution.AuthorityRoot
                    ?? throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnsupportedParticipant,
                        "Execution requires a retained native root handle.", Snapshot.Root);
            }
        }

        internal void Revalidate(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            borrow.RequireOperation(operation, registry);
            borrow.Owner.RevalidateRetainedEvidence();
        }
    }
}
