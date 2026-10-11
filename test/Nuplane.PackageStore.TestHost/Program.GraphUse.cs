using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nuplane.Abstractions;
using Nuplane;
using Nuplane.Feeds;
using Nuplane.Feeds.Configuration;
using Nuplane.Loading;
using Nuplane.Loading.Hosting.Builder;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.LockFile;
using Nuplane.Reconciliation.Models;

namespace Nuplane.PackageStore.TestHost;

internal static partial class Program
{
    private static async Task<int> RunGraphUseIdleAsync(string requestPath)
    {
        var request = await ReadGraphUseRequestAsync(requestPath).ConfigureAwait(false);
        await WriteJsonLineAsync(new StartedMessage("started", request.OperationId, Environment.ProcessId)).ConfigureAwait(false);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNuplane(nuplane =>
        {
            nuplane.WithStateFile(request.StateFilePath);
            nuplane.AutoloadPackages(loading => loading.WithDefaultLoadMode(PackageLoadMode.HostIntegrated));
        });
        services.AddSingleton<IDesiredPackageSource>(new GraphUseDesiredSource(request.DesiredRequests));
        services.Configure<FeedResolutionOptions>(options =>
        {
            options.PackageInstallRoot = request.RootPath;
            options.Feeds.Add(new FeedDefinition("feed", new Uri("https://unused.invalid/v3/index.json")));
        });
        services.Configure<LockFileOptions>(options =>
        {
            options.Mode = LockFileMode.Enforce;
            options.Path = Path.Combine(Path.GetDirectoryName(request.StateFilePath)!, "nuplane.lock.json");
        });

        // Keep this provider alive through process exit. Disposing host services after the idle
        // response would introduce a second lifetime boundary into the child proof.
        var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<IReconciliationService>()
            .TriggerAsync(ReconciliationTrigger.Manual("graph-use-process-exit"), CancellationToken.None)
            .ConfigureAwait(false);
        if (result.Skipped || result.IsDegraded || result.FailedPackages.Count != 0)
            throw new InvalidOperationException(
                $"Host-integrated reconciliation did not complete cleanly (skipped={result.Skipped}, degraded={result.IsDegraded}, failures={string.Join(",", result.FailedPackages)}).");

        var requiredAssemblies = new[] { "Root.First", "Shared.Dependency" };
        var matchingContexts = AssemblyLoadContext.All
            .Where(context => !context.IsCollectible && requiredAssemblies.All(expected =>
                context.Assemblies.Any(assembly => string.Equals(assembly.GetName().Name, expected, StringComparison.OrdinalIgnoreCase))))
            .ToArray();
        if (matchingContexts.Length != 1)
            throw new InvalidOperationException(
                $"Expected one noncollectible package graph context containing {string.Join(" and ", requiredAssemblies)}, found {matchingContexts.Length}.");

        var context = matchingContexts[0];
        var contextIdentity = $"{context.Name ?? "unnamed"}:{RuntimeHelpers.GetHashCode(context):x8}";
        var assemblyNames = context.Assemblies.Select(static assembly => assembly.GetName().Name!)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        await WriteJsonLineAsync(new LoadedIdleMessage("loaded-idle", request.OperationId, Environment.ProcessId,
            contextIdentity, context.Name, context.IsCollectible, assemblyNames)).ConfigureAwait(false);

        var input = await Console.In.ReadLineAsync().ConfigureAwait(false);
        if (input is null)
            return PrematureEofExitCode;
        GraphUseReleaseCommand? command;
        try
        {
            command = JsonSerializer.Deserialize<GraphUseReleaseCommand>(input, JsonOptions);
        }
        catch (JsonException)
        {
            await WriteDiagnosticAsync("Expected one graph-use release command.").ConfigureAwait(false);
            return InvalidProtocolExitCode;
        }

        if (command is null || command.Command != "release" || command.OperationId != request.OperationId)
        {
            await WriteDiagnosticAsync("The graph-use release command did not match the operation.").ConfigureAwait(false);
            return InvalidProtocolExitCode;
        }

        await WriteJsonLineAsync(new ExitingMessage("exiting", request.OperationId, Environment.ProcessId)).ConfigureAwait(false);
        return 0;
    }

    private static async Task<GraphUseProcessRequest> ReadGraphUseRequestAsync(string path)
    {
        var bytes = await ReadRequestBytesAsync(path).ConfigureAwait(false);
        var request = JsonSerializer.Deserialize<GraphUseProcessRequest>(bytes, JsonOptions)
            ?? throw new InvalidDataException("The graph-use process request was empty.");
        if (!Guid.TryParseExact(request.OperationId, "N", out _) ||
            string.IsNullOrWhiteSpace(request.RootPath) || !Path.IsPathFullyQualified(request.RootPath) ||
            string.IsNullOrWhiteSpace(request.StateFilePath) || !Path.IsPathFullyQualified(request.StateFilePath) ||
            request.DesiredRequests is null || request.DesiredRequests.Length is < 1 or > 8 ||
            request.DesiredRequests.Any(static desired => desired is null ||
                string.IsNullOrWhiteSpace(desired.Id) || string.IsNullOrWhiteSpace(desired.VersionRange) ||
                string.IsNullOrWhiteSpace(desired.FeedName) || string.IsNullOrWhiteSpace(desired.SourceName)))
        {
            throw new InvalidDataException("The graph-use process request fields were invalid.");
        }

        return request;
    }

    private sealed record GraphUseProcessRequest(
        string OperationId,
        string RootPath,
        string StateFilePath,
        PackageRequest[] DesiredRequests);

    private sealed record GraphUseReleaseCommand(string? Command, string? OperationId);
    private sealed record LoadedIdleMessage(
        string Kind,
        string OperationId,
        int ProcessId,
        string ContextIdentity,
        string? ContextName,
        bool IsCollectible,
        string[] AssemblyNames);
    private sealed record ExitingMessage(string Kind, string OperationId, int ProcessId);

    private sealed class GraphUseDesiredSource(IReadOnlyList<PackageRequest> requests) : IDesiredPackageSource
    {
        public Task<IReadOnlyList<PackageRequest>> GetDesiredAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(requests);
        }
    }
}
