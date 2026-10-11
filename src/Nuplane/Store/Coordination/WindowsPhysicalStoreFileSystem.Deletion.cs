using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.PhysicalFiles.Windows;

namespace Nuplane.Store.Coordination;

internal sealed partial class WindowsPhysicalStoreFileSystem : IPhysicalStoreDeletionFileSystem
{
    /// <inheritdoc />
    public void RemoveFileAt(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity expectedIdentity)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        PhysicalStoreDeletionChecks.RequireSameVolume(InspectHandle(parent).Identity, expectedIdentity);
        RemoveRegularFileAtCore(parent, singleName, expectedIdentity);
    }

    /// <inheritdoc />
    public void RemoveEmptyDirectoryAt(
        PhysicalStoreDirectoryHandle parent,
        string singleName,
        PhysicalFileIdentity expectedIdentity)
    {
        RequireSupportedPlatform();
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        ValidateName(singleName);
        using var parentLease = parent.AcquireScopedSafeHandle(_providerToken);
        var prepared = PhysicalStoreDeletionChecks.PrepareDirectoryRemoval(
            this, this, parent, singleName, expectedIdentity);
        var parentRaw = parentLease.DangerousHandle;
        try
        {
            using (var directory = OpenPublicationDirectory(parentRaw, singleName, requestDelete: true))
            {
                PhysicalStoreDirectoryPublicationChecks.RequireExpectedDirectory(
                    QueryEntry(directory.DangerousGetHandle(), "inspect the empty-directory removal handle"),
                    expectedIdentity);
                PhysicalStoreDeletionChecks.RevalidateDirectoryRemoval(this, this, parent, singleName, prepared);
                RequirePublicationParent(parent, parentRaw, prepared);
                WindowsNative.RemoveEmptyDirectory(directory.DangerousGetHandle());
            }

            RequirePublicationParent(parent, parentRaw, prepared);
        }
        catch (WindowsNativeCallException exception)
        {
            throw NativeFailure("remove an exact empty directory relative to its held parent", exception);
        }

        PhysicalStoreDeletionChecks.VerifyRemoved(this, this, parent, singleName, prepared);
    }
}
