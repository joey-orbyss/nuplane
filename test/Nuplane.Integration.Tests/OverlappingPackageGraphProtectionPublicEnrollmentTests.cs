using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nuplane.Admin;
using Nuplane.Admin.Api;
using Nuplane.Builder;
using Nuplane.Feeds.Configuration;
using Nuplane.Integration.Tests.Support;
using Nuplane.Store.State;
using Nuplane.Tests.Shared;

namespace Nuplane.Integration.Tests;

public sealed partial class OverlappingPackageGraphProtectionTests
{
    [Fact]
    public async Task PublicEnrollmentApi_QuiescentTwoMemberCutoverCompletesAndEmptyPrunePreservesState()
    {
        using var fixture = new PackageStoreFixture();
        var secondState = fixture.CreateStateSlot("second/store-state.json");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(builder => builder.WithStateFile(fixture.StateFilePath)
            .UseCoordinatedPackageStore([
                new PackageStoreMemberDeclaration("default", "first", fixture.StateFilePath),
                new PackageStoreMemberDeclaration("default", "second", secondState)], true));
        services.Configure<FeedResolutionOptions>(options => options.PackageInstallRoot = fixture.PackageInstallRoot);
        services.AddNuplanePackageStoreMaintenance();
        await using var provider = services.BuildServiceProvider();
        var operations = provider.GetRequiredService<INuplanePackageStoreMaintenanceOperations>();
        var preview = await operations.PreviewEnrollmentAsync();
        Assert.Equal("Unenrolled", preview.Membership);
        Assert.NotNull(preview.RootIdentity);
        Assert.True(preview.IsNonQuiescentObservation);
        Assert.Equal(2, preview.Members.Count);
        Assert.False(File.Exists(fixture.StateFilePath));
        Assert.False(File.Exists(secondState));
        var request = new PackageStoreEnrollmentRequest("default", preview.RootIdentity, 1,
            false, ["first", "second"], []);
        using var app = EndpointRouteTestHarness.CreateApp(registered => registered.AddSingleton(operations),
            endpoints => endpoints.MapNuplanePackageStoreMaintenance());

        var refused = await EndpointRouteTestHarness.InvokeAsync(app, "/nuplane/admin/store/enrollment", "POST", JsonBody(request));
        Assert.Equal(StatusCodes.Status409Conflict, refused.StatusCode);
        Assert.False(File.Exists(fixture.StateFilePath));
        Assert.False(File.Exists(secondState));

        var completed = await EndpointRouteTestHarness.InvokeAsync(app, "/nuplane/admin/store/enrollment", "POST",
            JsonBody(request with { QuiescentCutoverConfirmed = true }));
        using var report = JsonDocument.Parse(completed.Body);
        Assert.Equal(StatusCodes.Status200OK, completed.StatusCode);
        Assert.Equal("Complete", report.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, report.RootElement.GetProperty("enrollmentEpoch").GetInt64());
        var firstBytes = File.ReadAllBytes(fixture.StateFilePath);
        var secondBytes = File.ReadAllBytes(secondState);
        var serializer = new StoreStateSerializer();
        Assert.Equal("first", (await serializer.LoadAsync(fixture.StateFilePath, CancellationToken.None)).ProtectionRecord!.MemberId);
        Assert.Equal("second", (await serializer.LoadAsync(secondState, CancellationToken.None)).ProtectionRecord!.MemberId);

        var plan = await operations.PruneAsync(new PackageStorePruneRequest(Retention: new(0)));
        var noOp = await operations.PruneAsync(Confirm(plan, new(0)));
        Assert.Equal("Completed", noOp.Status);
        Assert.True(noOp.InventoryComplete);
        Assert.Empty(noOp.Candidates);
        Assert.Equal(firstBytes, File.ReadAllBytes(fixture.StateFilePath));
        Assert.Equal(secondBytes, File.ReadAllBytes(secondState));
        var repeat = await operations.EnrollAsync(request with { QuiescentCutoverConfirmed = true });
        Assert.Equal("Refused", repeat.Status);
        Assert.Equal(firstBytes, File.ReadAllBytes(fixture.StateFilePath));
        Assert.Equal(secondBytes, File.ReadAllBytes(secondState));
    }

    [Fact]
    public async Task PublicEnrollmentApi_RecoveryGraphTransportRoundtripsAndRejectsNestedUnknownFields()
    {
        using var fixture = await GraphUseFixture.CreateAsync();
        await using var provider = CreateMaintenanceProvider(fixture);
        var operations = provider.GetRequiredService<INuplanePackageStoreMaintenanceOperations>();
        var preview = await operations.PreviewEnrollmentAsync();
        var observed = Assert.Single(preview.Members, static member => member.MemberId == "member-a");
        var state = await new StoreStateSerializer().LoadAsync(fixture.StatePaths["member-a"], CancellationToken.None);
        var protection = state.ProtectionRecord!;
        var evidence = new PackageStoreRecoveryEvidence("member-a", observed.StateSlot!, observed.StateFileIdentity!,
            observed.StateBodyDigest!, protection.ActiveClosure.Graphs!.Select(Transport).ToArray(),
            protection.RecoverableClosure.Graphs!.Select(Transport).ToArray());
        var request = new PackageStoreEnrollmentRequest("default", fixture.RootIdentity, preview.EnrollmentEpoch!.Value,
            true, ["member-a", "member-b"], ["member-a", "member-b"], RecoveryEvidence: [evidence]);
        var body = JsonBody(request);
        var roundtrip = JsonSerializer.Deserialize<PackageStoreEnrollmentRequest>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var graph = Assert.Single(roundtrip!.RecoveryEvidence![0].ActiveGraphs);
        Assert.NotEmpty(graph.RequestedRoots);
        Assert.NotEmpty(graph.Nodes);
        Assert.Equal(evidence.ActiveGraphs[0].GraphId, graph.GraphId);
        using var app = EndpointRouteTestHarness.CreateApp(registered => registered.AddSingleton(operations),
            endpoints => endpoints.MapNuplanePackageStoreMaintenance());
        var before = CaptureFixtureNativeEvidence(fixture);
        // This root is already Complete: valid transport reaches the core's explicit refusal.
        var response = await EndpointRouteTestHarness.InvokeAsync(app, "/nuplane/admin/store/enrollment/recover", "POST", body);
        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
        var modified = System.Text.Json.Nodes.JsonNode.Parse(body)!;
        modified["recoveryEvidence"]![0]!["activeGraphs"]![0]!["nodes"]![0]!["install"]!["rootPath"] = "/untrusted";
        var rejected = await EndpointRouteTestHarness.InvokeAsync(app, "/nuplane/admin/store/enrollment/recover", "POST", modified.ToJsonString());
        Assert.Equal(StatusCodes.Status400BadRequest, rejected.StatusCode);
        Assert.Equal(before.OrderBy(static pair => pair.Key, StringComparer.Ordinal),
            CaptureFixtureNativeEvidence(fixture).OrderBy(static pair => pair.Key, StringComparer.Ordinal));
    }

    private static string JsonBody(PackageStoreEnrollmentRequest request)
        => JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static PackageStoreRecoveryGraphEvidence Transport(Nuplane.Store.Coordination.ProtectionRecords.ProtectedGraphSnapshot graph)
        => new(graph.SnapshotId, graph.GraphId, graph.GenerationId, graph.Disposition, graph.Roots,
            graph.RequestedRoots.Select(static root => new PackageStoreRecoveryRootSelection(root.Request, root.SelectedNodeId)).ToArray(),
            graph.Nodes.Select(static node => new PackageStoreRecoveryNode(node.NodeId, node.Install)).ToArray(),
            graph.Edges.Select(static edge => new PackageStoreRecoveryEdge(edge.FromNodeId, edge.ToNodeId,
                edge.RequestedPackageId, edge.RequestedVersionRange, edge.TargetFramework, edge.IsOptional)).ToArray(),
            graph.RecoverySelectionEvidence is { } source
                ? new(source.RecoveryPolicyId, source.SourceRevision, source.SelectedRootNodeIds) : null);
}
