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
    private readonly PackageStoreDeletionCandidateBinder _candidateBinder = new(registry.Files);

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

            var context = new Context(registry, borrow, operation, configuredRoot, snapshot, _candidateBinder);
            TResult result;
            try
            {
                context.Revalidate(cancellationToken);
                result = await candidateWork(context, cancellationToken).ConfigureAwait(false);
                context.Revalidate(cancellationToken);
                await registry.VerifyCatalogMemberProtectionUnderOperationAsync(borrow, operation, cancellationToken)
                    .ConfigureAwait(false);
                context.Revalidate(cancellationToken);
            }
            catch (Exception candidateFailure)
            {
                try
                {
                    context.Dispose();
                }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException(
                        "Execution candidate work failed and native candidate cleanup also failed.",
                        candidateFailure,
                        cleanupFailure);
                }
                throw;
            }
            context.Dispose();
            return result;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Live planning scope; escaped snapshots remain descriptive and native access expires with the operation.</summary>
    internal sealed class Context(
        RootMembershipRegistry registry,
        RootMembershipRegistry.NativeCatalogOwnerBorrow borrow,
        RootMembershipRegistry.NativeCatalogOwnerBorrow.Operation operation,
        TrustedPackageStoreRoot configuredRoot,
        PackageStoreInspectionSnapshot snapshot,
        PackageStoreDeletionCandidateBinder candidateBinder) : IDisposable
    {
        private readonly PackageStoreDeletionCandidateBinder _candidateBinder = candidateBinder;
        private readonly object _gate = new();
        private readonly List<PackageStoreDeletionCandidate> _candidates = [];
        private readonly HashSet<PackageStoreRetentionPlanEntry> _boundRows = new(ReferenceEqualityComparer.Instance);
        private bool _disposed;

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
            lock (_gate)
            {
                if (_disposed)
                    throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.ExpiredScope,
                        "The execution context has ended.", Snapshot.Root);
            }
            cancellationToken.ThrowIfCancellationRequested();
            borrow.RequireOperation(operation, registry);
            borrow.Owner.RevalidateRetainedEvidence();
        }

        /// <summary>Binds only the exact Eligible row in this execution's fresh plan to native install evidence.</summary>
        /// <remarks>The returned observation is owned by this context and expires when the operation ends.</remarks>
        internal PackageStoreDeletionCandidate BindEligibleCandidate(
            PackageStoreRetentionPlanEntry exactRow,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(exactRow);
            Revalidate(cancellationToken);

            var plan = Snapshot.RetentionPlan
                ?? throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "Candidate binding requires the current execution retention plan.");
            if (Snapshot.Root is null || plan.Root != Snapshot.Root ||
                Snapshot.EnrollmentEpoch != plan.EnrollmentEpoch ||
                Snapshot.AdmissionStatus != PackageStoreAdmissionStatus.Enrolled ||
                plan.InventoryStatus != PackageStoreRetentionInventoryStatus.Complete ||
                plan.ProtectionKnowledge != PackageStoreRetentionProtectionKnowledge.Known ||
                plan.HasUnresolvedEvidence)
            {
                throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "Candidate binding requires a complete, known, unresolved-free plan for the enrolled root.");
            }

            if (plan.Entries.Count(entry => ReferenceEquals(entry, exactRow)) != 1)
                throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "Candidate binding requires the exact row object from this execution's current plan.");
            if (exactRow.Classification != PackageStoreRetentionClassification.Eligible)
                throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "Only a currently Eligible plan row can be bound to native install evidence.");

            lock (_gate)
            {
                if (_disposed)
                    throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.ExpiredScope,
                        "The execution context has ended.", Snapshot.Root);
                if (!_boundRows.Add(exactRow))
                    throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                        "One execution context can bind an exact retention row only once.");
            }

            var candidate = _candidateBinder.Bind(exactRow, Snapshot.Root, RootHandle, Revalidate, cancellationToken);
            lock (_gate)
            {
                if (_disposed)
                {
                    candidate.Dispose();
                    throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.ExpiredScope,
                        "The execution context ended during native candidate binding.", Snapshot.Root);
                }
                _candidates.Add(candidate);
            }
            return candidate;
        }

        public void Dispose()
        {
            PackageStoreDeletionCandidate[] candidates;
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                candidates = _candidates.ToArray();
                _candidates.Clear();
            }

            List<Exception>? failures = null;
            for (var index = candidates.Length - 1; index >= 0; index--)
            {
                try
                {
                    candidates[index].Dispose();
                }
                catch (Exception exception)
                {
                    (failures ??= []).Add(exception);
                }
            }

            if (failures is { Count: 1 })
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures is { Count: > 1 })
                throw new AggregateException("Multiple native deletion candidate observations failed to close.", failures);
        }

        private static PackageStoreAdmissionException Refuse(PackageStoreAdmissionReason reason, string message)
            => new(reason, message);
    }
}
