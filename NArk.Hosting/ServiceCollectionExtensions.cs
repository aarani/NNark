using Microsoft.Extensions.DependencyInjection;
using NArk.Abstractions.Fees;
using NArk.Abstractions.Intents;
using NArk.CoinSelector;
using NArk.Fees;
using NArk.Services;
using NArk.Sweeper;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Policies;
using NArk.Swaps.Services;

namespace NArk.Hosting;

/// <summary>
/// Extension methods for registering NArk services with IServiceCollection.
/// Use this when you don't have access to IHostBuilder (e.g., in plugin scenarios).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all NArk core services.
    /// Caller must still register: IVtxoStorage, IContractStorage, IIntentStorage, IWalletStorage,
    /// ISwapStorage, IWallet, ISafetyService, IChainTimeProvider, and IClientTransport.
    /// </summary>
    public static IServiceCollection AddArkCoreServices(this IServiceCollection services)
    {
        services.AddSingleton<SpendingService>();
        services.AddSingleton<ISpendingService>(s => s.GetRequiredService<SpendingService>());
        services.AddSingleton<ISigningService, SigningService>();
        services.AddSingleton<IContractService, ContractService>();
        services.AddSingleton<VtxoSynchronizationService>();
        services.AddSingleton<IntentGenerationService>();
        services.AddSingleton<IIntentGenerationService>(s => s.GetRequiredService<IntentGenerationService>());
        services.AddSingleton<IntentSynchronizationService>();
        services.AddSingleton<BatchManagementService>();
        services.AddSingleton<IOnchainService, OnchainService>();
        services.AddSingleton<SweeperService>();
        services.AddSingleton<IFeeEstimator, DefaultFeeEstimator>();
        services.AddSingleton<ICoinSelector, DefaultCoinSelector>();
        services.AddHostedService<ArkHostedLifecycle>();

        return services;
    }

    /// <summary>
    /// Registers NArk swap services (Boltz integration).
    /// Caller must configure BoltzClient's HttpClient base address.
    /// </summary>
    public static IServiceCollection AddArkSwapServices(this IServiceCollection services)
    {
        services.AddSingleton<SwapsManagementService>();
        services.AddSingleton<ISweepPolicy, SwapSweepPolicy>();

        return services;
    }
}
