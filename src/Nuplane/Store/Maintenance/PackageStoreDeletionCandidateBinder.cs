using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.DeletionRecords;
using Nuplane.Store.Coordination.PhysicalFiles;

namespace Nuplane.Store.Maintenance;

/// <summary>Binds one current eligible row to exact native ancestry and a bounded install baseline.</summary>
/// <remarks>The caller must already hold the live full-catalog execution operation.</remarks>
internal sealed class PackageStoreDeletionCandidateBinder
{
    private readonly PackageInstallIdentityReader _identities;
    private readonly PhysicalStoreDeletionManifestReader _manifests;

    internal PackageStoreDeletionCandidateBinder(IPhysicalStoreFileSystem files)
    {
        ArgumentNullException.ThrowIfNull(files);
        _identities = new PackageInstallIdentityReader(files);
        _manifests = new PhysicalStoreDeletionManifestReader(files);
    }

    internal PackageStoreDeletionCandidate Bind(
        PackageStoreRetentionPlanEntry planEntry,
        PhysicalRootIdentity expectedRoot,
        PhysicalStoreDirectoryHandle rootHandle,
        Action<CancellationToken> revalidateOperation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(planEntry);
        ArgumentNullException.ThrowIfNull(expectedRoot);
        ArgumentNullException.ThrowIfNull(rootHandle);
        ArgumentNullException.ThrowIfNull(revalidateOperation);
        cancellationToken.ThrowIfCancellationRequested();
        revalidateOperation(cancellationToken);

        var expectedInstall = planEntry.Install;
        if (expectedInstall.Root != expectedRoot)
            throw Refuse(PackageStoreAdmissionReason.RootMismatch,
                "The current retention row belongs to another physical root.", expectedRoot);

        var observation = _identities.Observe(rootHandle, expectedRoot,
            expectedInstall.RootRelativeInstallPath,
            expectedInstall.PackageId,
            expectedInstall.Version,
            expectedInstall.VerifiedArchiveHash);
        try
        {
            var observedInstall = observation.InstallIdentity;
            if (observedInstall != expectedInstall)
                throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "The native completed install no longer matches the exact current retention row.", expectedRoot);

            var location = observation.ObserveCanonicalDeletionLocation();
            var manifest = _manifests.Capture(
                observation.InstallDirectory,
                observedInstall,
                location.PackageParentHandle,
                location.PackageParentIdentity,
                location.PackageParentSemantics,
                location.OriginalBasename,
                location.InstallDirectorySemantics,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            revalidateOperation(cancellationToken);
            var finalLocation = observation.ObserveCanonicalDeletionLocation();
            if (location != finalLocation || manifest.InstallDirectoryIdentity != finalLocation.InstallDirectoryIdentity)
                throw Refuse(PackageStoreAdmissionReason.UnknownAuthority,
                    "The canonical install location changed during candidate binding.", expectedRoot);

            return new PackageStoreDeletionCandidate(planEntry, observedInstall, manifest, observation, location,
                revalidateOperation);
        }
        catch
        {
            observation.Dispose();
            throw;
        }
    }

    private static PackageStoreAdmissionException Refuse(
        PackageStoreAdmissionReason reason,
        string message,
        PhysicalRootIdentity root)
        => new(reason, message, root);
}

/// <summary>Call-scoped exact binding for one freshly planned eligible completed install.</summary>
/// <remarks>Its manifest is descriptive; no property or method performs a namespace mutation.</remarks>
internal sealed class PackageStoreDeletionCandidate : IDisposable
{
    private readonly PackageInstallIdentityReader.PackageInstallIdentityObservation _observation;
    private readonly PackageInstallIdentityReader.PackageInstallDeletionLocation _location;
    private readonly Action<CancellationToken> _revalidateOperation;
    private bool _preparedForMove;
    private bool _disposed;

    internal PackageStoreDeletionCandidate(
        PackageStoreRetentionPlanEntry planEntry,
        PackageInstallIdentity installIdentity,
        PhysicalStoreDeletionManifest manifest,
        PackageInstallIdentityReader.PackageInstallIdentityObservation observation,
        PackageInstallIdentityReader.PackageInstallDeletionLocation location,
        Action<CancellationToken> revalidateOperation)
    {
        PlanEntry = planEntry;
        InstallIdentity = installIdentity;
        Manifest = manifest;
        _observation = observation;
        _location = location;
        _revalidateOperation = revalidateOperation;
    }

    internal PackageStoreRetentionPlanEntry PlanEntry { get; }

    internal PackageInstallIdentity InstallIdentity { get; }

    internal PhysicalStoreDeletionManifest Manifest { get; }

    internal void Revalidate(CancellationToken cancellationToken)
    {
        EnsureLive(cancellationToken);
        if (_preparedForMove)
            _observation.RevalidateDeletionParent();
        else if (_observation.ObserveCanonicalDeletionLocation() != _location)
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnknownAuthority,
                "The deletion candidate's canonical install location changed.", InstallIdentity.Root);
    }

    /// <summary>Closes the install leaf and marker, retaining a revalidatable exact package-parent binding.</summary>
    /// <remarks>The returned binding does not authorize a move; a future executor must gate it on durable Prepared evidence.</remarks>
    internal PackageStoreDeletionParentBinding PrepareForMove(CancellationToken cancellationToken)
    {
        EnsureLive(cancellationToken);
        if (_preparedForMove)
            throw Expired("The candidate leaf was already closed for its parent binding.");

        var current = _observation.PrepareForMove(_location);
        if (current != _location)
            throw new PackageStoreAdmissionException(PackageStoreAdmissionReason.UnknownAuthority,
                "The candidate's canonical parent binding changed before leaf closure.", InstallIdentity.Root);
        _preparedForMove = true;
        return new PackageStoreDeletionParentBinding(_observation, _location, EnsureLive);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _observation.Dispose();
    }

    private void EnsureLive(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed)
            throw Expired("The deletion candidate has been disposed.");
        _revalidateOperation(cancellationToken);
    }

    private PackageStoreAdmissionException Expired(string message)
        => new(PackageStoreAdmissionReason.ExpiredScope, message, InstallIdentity.Root);
}

/// <summary>Revalidatable held root/feed/package ancestry after the install leaf has closed.</summary>
/// <remarks>This is a native observation only; it has no publication, rename or removal method.</remarks>
internal sealed class PackageStoreDeletionParentBinding
{
    private readonly PackageInstallIdentityReader.PackageInstallIdentityObservation _observation;
    private readonly PackageInstallIdentityReader.PackageInstallDeletionLocation _location;
    private readonly Action<CancellationToken> _ensureCandidateLive;

    internal PackageStoreDeletionParentBinding(
        PackageInstallIdentityReader.PackageInstallIdentityObservation observation,
        PackageInstallIdentityReader.PackageInstallDeletionLocation location,
        Action<CancellationToken> ensureCandidateLive)
    {
        _observation = observation;
        _location = location;
        _ensureCandidateLive = ensureCandidateLive;
    }

    internal PhysicalStoreDirectoryHandle PackageParentHandle
    {
        get
        {
            Revalidate(CancellationToken.None);
            return _location.PackageParentHandle;
        }
    }

    internal PhysicalFileIdentity PackageParentIdentity => _location.PackageParentIdentity;

    internal PhysicalStoreNameSemantics PackageParentSemantics => _location.PackageParentSemantics;

    internal string OriginalBasename => _location.OriginalBasename;

    internal PhysicalFileIdentity InstallDirectoryIdentity => _location.InstallDirectoryIdentity;

    internal PhysicalStoreNameSemantics InstallDirectorySemantics => _location.InstallDirectorySemantics;

    internal PhysicalFileIdentity CompletionMarkerIdentity => _location.CompletionMarkerIdentity;

    internal PackageInstallIdentity InstallIdentity => _location.InstallIdentity;

    internal void Revalidate(CancellationToken cancellationToken)
    {
        _ensureCandidateLive(cancellationToken);
        _observation.RevalidateDeletionParent();
    }
}
