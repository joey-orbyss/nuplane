using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Store.Coordination;
using Nuplane.Store.Coordination.PhysicalFiles;
using Nuplane.Store.Coordination.ProtectionRecords;
using Nuplane.Store.Maintenance;

namespace Nuplane.Admin;

internal sealed partial class PackageStoreMaintenanceService
{
    public Task<PackageStoreEnrollmentResult> EnrollAsync(
        PackageStoreEnrollmentRequest request, CancellationToken cancellationToken = default)
        => RunEnrollmentAsync(request, recover: false, cancellationToken);

    public Task<PackageStoreEnrollmentResult> RecoverEnrollmentAsync(
        PackageStoreEnrollmentRequest request, CancellationToken cancellationToken = default)
        => RunEnrollmentAsync(request, recover: true, cancellationToken);

    private async Task<PackageStoreEnrollmentResult> RunEnrollmentAsync(
        PackageStoreEnrollmentRequest request, bool recover, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var candidate = new PackageStoreEnrollmentOperationRequest(request.RootLabel,
            request.ExpectedPhysicalRootIdentity, request.ExpectedEnrollmentEpoch, request.QuiescentCutoverConfirmed,
            request.IntendedMemberIds, request.ExpectedCurrentMemberIds, request.RetireMemberIds,
            request.RecoveryEvidence?.Select(Evidence));
        try
        {
            var result = recover
                ? await enrollment.RecoverEnrollmentAsync(candidate, cancellationToken).ConfigureAwait(false)
                : await enrollment.EnrollAsync(candidate, cancellationToken).ConfigureAwait(false);
            return new(result.RootLabel, result.RootIdentity, result.EnrollmentEpoch, result.Status.ToString(),
                result.Members.Select(static member => new PackageStoreEnrollmentMemberResult(
                    member.MemberId, member.Disposition.ToString(), member.Diagnostics)).ToArray(),
                result.Diagnostics.Select(static message => new PackageStoreMaintenanceDiagnostic("Enrollment", message)).ToArray());
        }
        catch (PackageStoreAdmissionException exception)
        {
            return new(request.RootLabel, exception.Root, null, "Refused", [], [Diagnostic(exception)]);
        }
    }

    private static PackageStoreMemberRecoveryEvidence Evidence(PackageStoreRecoveryEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(evidence.ExpectedStateSlot);
        ArgumentNullException.ThrowIfNull(evidence.ActiveGraphs);
        ArgumentNullException.ThrowIfNull(evidence.RecoverableGraphs);
        var slot = evidence.ExpectedStateSlot;
        if (!Enum.TryParse<PhysicalStoreNameEncoding>(slot.NameEncoding, out var encoding) || !Enum.IsDefined(encoding))
            throw new ArgumentException("The observed state-slot name encoding is unsupported.");
        return new(evidence.MemberId, new StateSlotIdentity(slot.ParentIdentity,
                new PhysicalStoreNameSemantics(slot.NameProfile, encoding, slot.CaseSensitive, slot.NormalizationInsensitive),
                slot.CanonicalBasename), evidence.ExpectedStateFileIdentity, evidence.ExpectedStateBodyDigest,
            evidence.ActiveGraphs.Select(Graph), evidence.RecoverableGraphs.Select(Graph));
    }

    private static ProtectedGraphSnapshot Graph(PackageStoreRecoveryGraphEvidence graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(graph.RequestedRoots);
        ArgumentNullException.ThrowIfNull(graph.Nodes);
        ArgumentNullException.ThrowIfNull(graph.Edges);
        if (graph.RequestedRoots.Any(static root => root is null) || graph.Nodes.Any(static node => node is null) ||
            graph.Edges.Any(static edge => edge is null))
            throw new ArgumentException("Graph evidence cannot contain null entries.");
        var selection = graph.RecoverySelection is { } source
            ? new ProtectedGraphRecoverySelectionEvidence(source.RecoveryPolicyId, source.SourceRevision, source.SelectedRootNodeIds)
            : null;
        return PackageStoreRecoveryGraphFactory.Create(graph.SnapshotId, graph.GraphId, graph.GenerationId, graph.Disposition,
            graph.Roots, graph.RequestedRoots.Select(static root => (root.Request, root.SelectedNodeId)),
            graph.Nodes.Select(static node => (node.NodeId, node.Install)),
            graph.Edges.Select(static edge => (edge.FromNodeId, edge.ToNodeId, edge.RequestedPackageId,
                edge.RequestedVersionRange, edge.TargetFramework, edge.IsOptional)), selection);
    }
}
