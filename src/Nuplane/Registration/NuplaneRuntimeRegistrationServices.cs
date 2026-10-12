using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Nuplane.Abstractions;
using Nuplane.Abstractions.PackageStoreProtection;
using Nuplane.Capabilities;
using Nuplane.Events;
using Nuplane.Feeds.Configuration;
using Nuplane.Health;
using Nuplane.Hosting;
using Nuplane.Observability;
using Nuplane.Operational;
using Nuplane.Reconciliation;
using Nuplane.Reconciliation.Configuration;
using Nuplane.Sources;
using Nuplane.Store.Coordination;
using Nuplane.Store.Cleanup;
using Nuplane.Store.State;
using Polly;

namespace Nuplane.Registration;

/// <summary>
/// Provides extension methods for registering Nuplane Runtime services, including configuration validators and other runtime-specific dependencies.
/// </summary>
public static class NuplaneRuntimeRegistrationServices
{
    /// <summary>
    /// Registers Nuplane Runtime services, including the composite validator for feed resolution options that ensures feed credentials are valid according to the configured trust policies and source trust settings.
    /// </summary>
    public static IServiceCollection RegisterRuntime(this IServiceCollection services)
    {
        services.RegisterCoreServices();
        services.RegisterSecretReferences();
        services.RegisterDesiredStateAggregationAndDryRunPlanning();
        services.RegisterPolicyAndVersioning();
        services.RegisterLockingAndCleanup();
        services.RegisterTelemetryAndObservers();
        services.RegisterPackageResolution();
        services.RegisterStorePersistence();
        services.AddTriggerIngressServices();
        return services;
    }

    private static void RegisterCoreServices(this IServiceCollection services)
    {
        services.AddSingleton<IValidateOptions<FeedResolutionOptions>, FeedCredentialCompositeValidator>();
        services.AddSingleton<FailureRecorder>();
        services.AddSingleton<IFailureRecorder>(sp => sp.GetRequiredService<FailureRecorder>());
        services.AddResiliencePipeline(ReconciliationRetryPolicy.PipelineName, static (builder, context) =>
        {
            var options = context.ServiceProvider.GetRequiredService<IOptions<ReconciliationOptions>>().Value;
            if (options.MaxRetryAttempts == 0)
            {
                return;
            }

            builder.AddRetry(ReconciliationRetryPolicy.CreateRetryOptions(options));
        });
        services.AddSingleton<ReconciliationRetryPolicy>();
        services.AddSingleton<IReconciliationRetryPolicy>(sp => sp.GetRequiredService<ReconciliationRetryPolicy>());
        services.TryAddSingleton<ObservationDegradationTracker>();
        services.TryAddSingleton<StartupRecoveryState>();
        // Explicit factory, not TryAddSingleton<TService, TImplementation>: the constructor DI would
        // pick automatically resolves IStoreLock only by argument-type matching against whichever
        // constructor the container prefers, and a future constructor added to this type could
        // silently change which one that is. Naming the store-lock-aware constructor here means a
        // registration-order change can never drop the lock unnoticed.
        services.TryAddSingleton<ILastKnownGoodStartupRecoveryService>(sp =>
        {
            var composition = sp.GetRequiredService<PackageStoreCompositionSnapshot>();
            return new LastKnownGoodStartupRecoveryService(
                sp.GetRequiredService<IStoreRegistry>(),
                sp.GetRequiredService<IObserverEventDispatcher>(),
                sp.GetRequiredService<StartupRecoveryState>(),
                sp.GetServices<ICycleFailureContributor>(),
                sp.GetRequiredService<IOptions<ReconciliationOptions>>(),
                sp.GetService<IStoreLock>(),
                composition.IsParticipant ? sp.GetRequiredService<IPackageStoreAdmission>() : null,
                coordinatedParticipantMode: composition.IsParticipant);
        });
        services.AddSingleton<ActivePackageCatalog>(sp =>
        {
            var composition = sp.GetRequiredService<PackageStoreCompositionSnapshot>();
            var storeRegistry = sp.GetRequiredService<IStoreRegistry>();
            var logger = sp.GetRequiredService<IReconciliationLogger>();
            var metrics = sp.GetRequiredService<ReconciliationMetrics>();
            var admission = composition.IsParticipant
                ? RequireValidatedParticipantAdmission(sp, composition)
                : null;
            return composition.IsParticipant
                ? new ActivePackageCatalog(storeRegistry, logger, metrics, admission)
                : new ActivePackageCatalog(storeRegistry, logger, metrics);
        });
        services.AddSingleton<IActivePackageCatalog>(sp => sp.GetRequiredService<ActivePackageCatalog>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOperationalStateContributor, PackageCatalogOperationalStateContributor>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOperationalStateContributor, StartupRecoveryOperationalStateContributor>());
        services.AddSingleton(sp =>
        {
            var composition = sp.GetRequiredService<PackageStoreCompositionSnapshot>();
            var loadingObserver = sp.GetService<ILeasedPackageGraphLoadingObserver>();
            var packageResolver = sp.GetRequiredService<IPackageResolver>();
            var capabilityContributor = sp.GetRequiredService<CapabilityDesiredStateContributor>();
            var admission = composition.IsParticipant
                ? RequireValidatedParticipantAdmission(sp, composition)
                : null;
            if (composition.IsParticipant)
            {
                PackageStoreCompositionValidator.RequireSupportedParticipantExecutionRoute(
                    composition,
                    packageResolver,
                    capabilityContributor.MetadataReader);
            }
            return new ReconciliationService(
            sp.GetServices<IDesiredPackageSource>(),
            sp.GetRequiredService<IDesiredStateAggregator>(),
            sp.GetRequiredService<IDesiredActualDiffEngine>(),
            packageResolver,
            sp.GetRequiredService<IStoreRegistry>(),
            sp.GetRequiredService<IOptions<ReconciliationOptions>>(),
            sp.GetRequiredService<IObserverEventDispatcher>(),
            sp.GetRequiredService<IReconciliationHealthEvaluator>(),
            sp.GetRequiredService<IReconciliationLogger>(),
            sp.GetRequiredService<ReconciliationMetrics>(),
            sp.GetRequiredService<IOptions<FeedResolutionOptions>>(),
            sp.GetRequiredService<ILockFileCoordinator>(),
            sp.GetRequiredService<IOptions<CleanupPolicyOptions>>(),
            sp.GetRequiredService<IReconciliationRetryPolicy>(),
            sp.GetRequiredService<IDryRunPlanner>(),
            sp.GetRequiredService<IPackageCleanupService>(),
            sp.GetRequiredService<IFailureRecorder>(),
            sp.GetRequiredService<ObservationDegradationTracker>(),
            sp.GetService<ICycleFailureContributor>(),
            sp.GetService<StartupRecoveryState>(),
            sp.GetService<IStoreLock>(),
            sp.GetServices<IDesiredStateContributor>(),
            sp.GetRequiredService<IOptions<HostProvidedPackagesOptions>>(),
            admission,
            composition.IsParticipant && loadingObserver is not null
                ? sp.GetService<IResolvedPackageGraphUseLeaseAcquisition>()
                : null,
            composition.IsParticipant ? loadingObserver : null,
            composition);
        });
        services.AddSingleton<IReconciliationService>(sp => sp.GetRequiredService<ReconciliationService>());
    }

    private static IPackageStoreAdmission RequireValidatedParticipantAdmission(
        IServiceProvider services,
        PackageStoreCompositionSnapshot composition)
    {
        var admission = services.GetRequiredService<IPackageStoreAdmission>();
        PackageStoreCompositionValidator.RequireSupportedParticipantSelection(
            composition,
            services.GetRequiredService<IPhysicalStoreFileSystem>(),
            services.GetRequiredService<IStoreRegistry>(),
            services.GetRequiredService<IStoreStateSerializer>(),
            admission,
            services.GetRequiredService<ITrustedPackageStoreRootCatalog>(),
            services.GetRequiredService<ITrustedPackageStoreMemberDeclarationCatalog>(),
            services.GetRequiredService<IOptions<FeedResolutionOptions>>().Value);
        return admission;
    }

    private static void AddTriggerIngressServices(this IServiceCollection services)
    {
        services.AddSingleton<ReconciliationTriggerQueue>();
        services.AddSingleton<IReconciliationTriggerIngress>(sp => sp.GetRequiredService<ReconciliationTriggerQueue>());
        services.AddHostedService<ReconciliationTriggerDispatcherHostedService>();
        services.AddHostedService<NuplaneStartupHostedService>();
    }
}
