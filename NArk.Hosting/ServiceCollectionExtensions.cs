using Microsoft.Extensions.DependencyInjection;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Fees;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.CoinSelector;
using NArk.Events;
using NArk.Fees;
using NArk.Models.Options;
using NArk.Services;
using NArk.Sweeper;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;
using NArk.Swaps.Policies;
using NArk.Swaps.Services;
using NArk.Transformers;
using NArk.Transport;
using NArk.Transport.GrpcClient;

namespace NArk.Hosting;

/// <summary>
/// Extension methods for registering NArk services with IServiceCollection.
/// Use this when you don't have access to IHostBuilder (e.g., in plugin scenarios).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all NArk services using a fluent configuration builder.
    /// This is the recommended entry point for configuring NArk.
    /// </summary>
    /// <example>
    /// services.AddArkade(ark =>
    /// {
    ///     ark.UseVtxoStorage&lt;MyVtxoStorage&gt;();
    ///     ark.UseContractStorage&lt;MyContractStorage&gt;();
    ///     ark.UseIntentStorage&lt;MyIntentStorage&gt;();
    ///     ark.UseWalletStorage&lt;MyWalletStorage&gt;();
    ///     ark.UseSafetyService&lt;MySafetyService&gt;();
    ///     ark.UseWallet&lt;MyWallet&gt;();
    ///     ark.UseTimeProvider&lt;MyTimeProvider&gt;();
    ///     ark.WithArkServer("https://arkade.computer");
    ///     ark.WithBoltz("https://api.ark.boltz.exchange/");
    /// });
    /// </example>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configuration action.</param>
    /// <param name="validate">If true, validates all required services are registered and throws if any are missing. Default is true.</param>
    public static IServiceCollection AddArkade(this IServiceCollection services, Action<ArkadeConfiguration> configure, bool validate = true)
    {
        var config = new ArkadeConfiguration(services);
        configure(config);

        // Register core services
        services.AddArkCoreServices();

        // Validate all required services are registered (optional)
        if (validate)
        {
            services.EnsureArkRegistrations();
        }

        return services;
    }

    /// <summary>
    /// Registers all NArk core services.
    /// Caller must still register: IVtxoStorage, IContractStorage, IIntentStorage, IWalletStorage,
    /// ISwapStorage, IWallet, ISafetyService, IChainTimeProvider, and IClientTransport.
    /// </summary>
    public static IServiceCollection AddArkCoreServices(this IServiceCollection services)
    {
        services.AddSingleton<ICoinService, CoinService>();
        services.AddTransient<IContractTransformer, PaymentContractTransformer>();
        services.AddTransient<IContractTransformer, HashLockedContractTransformer>();
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

    #region Storage Registration Extensions

    /// <summary>
    /// Registers a VTXO storage implementation.
    /// </summary>
    public static IServiceCollection AddArkVtxoStorage<TStorage>(this IServiceCollection services)
        where TStorage : class, IVtxoStorage
    {
        services.AddSingleton<TStorage>();
        services.AddSingleton<IVtxoStorage>(sp => sp.GetRequiredService<TStorage>());
        return services;
    }

    /// <summary>
    /// Registers a contract storage implementation.
    /// </summary>
    public static IServiceCollection AddArkContractStorage<TStorage>(this IServiceCollection services)
        where TStorage : class, IContractStorage
    {
        services.AddSingleton<TStorage>();
        services.AddSingleton<IContractStorage>(sp => sp.GetRequiredService<TStorage>());
        return services;
    }

    /// <summary>
    /// Registers an intent storage implementation.
    /// </summary>
    public static IServiceCollection AddArkIntentStorage<TStorage>(this IServiceCollection services)
        where TStorage : class, IIntentStorage
    {
        services.AddSingleton<TStorage>();
        services.AddSingleton<IIntentStorage>(sp => sp.GetRequiredService<TStorage>());
        return services;
    }

    /// <summary>
    /// Registers a wallet storage implementation.
    /// </summary>
    public static IServiceCollection AddArkWalletStorage<TStorage>(this IServiceCollection services)
        where TStorage : class, IWalletStorage
    {
        services.AddSingleton<TStorage>();
        services.AddSingleton<IWalletStorage>(sp => sp.GetRequiredService<TStorage>());
        return services;
    }

    /// <summary>
    /// Registers a swap storage implementation.
    /// </summary>
    public static IServiceCollection AddArkSwapStorage<TStorage>(this IServiceCollection services)
        where TStorage : class, ISwapStorage
    {
        services.AddSingleton<TStorage>();
        services.AddSingleton<ISwapStorage>(sp => sp.GetRequiredService<TStorage>());
        return services;
    }

    /// <summary>
    /// Registers a key storage implementation.
    /// </summary>
    public static IServiceCollection AddArkKeyStorage<TStorage>(this IServiceCollection services)
        where TStorage : class, IKeyStorage
    {
        services.AddSingleton<TStorage>();
        services.AddSingleton<IKeyStorage>(sp => sp.GetRequiredService<TStorage>());
        return services;
    }

    #endregion

    #region Service Registration Extensions

    /// <summary>
    /// Registers a safety service implementation.
    /// </summary>
    public static IServiceCollection AddArkSafetyService<TSafety>(this IServiceCollection services)
        where TSafety : class, ISafetyService
    {
        services.AddSingleton<TSafety>();
        services.AddSingleton<ISafetyService>(sp => sp.GetRequiredService<TSafety>());
        return services;
    }

    /// <summary>
    /// Registers a wallet implementation.
    /// </summary>
    public static IServiceCollection AddArkWallet<TWallet>(this IServiceCollection services)
        where TWallet : class, IWallet
    {
        services.AddSingleton<TWallet>();
        services.AddSingleton<IWallet>(sp => sp.GetRequiredService<TWallet>());
        return services;
    }

    /// <summary>
    /// Registers an intent scheduler implementation.
    /// </summary>
    public static IServiceCollection AddArkIntentScheduler<TScheduler>(this IServiceCollection services)
        where TScheduler : class, IIntentScheduler
    {
        services.AddSingleton<TScheduler>();
        services.AddSingleton<IIntentScheduler>(sp => sp.GetRequiredService<TScheduler>());
        return services;
    }

    /// <summary>
    /// Registers a chain time provider implementation.
    /// </summary>
    public static IServiceCollection AddArkTimeProvider<TTime>(this IServiceCollection services)
        where TTime : class, IChainTimeProvider
    {
        services.AddSingleton<TTime>();
        services.AddSingleton<IChainTimeProvider>(sp => sp.GetRequiredService<TTime>());
        return services;
    }

    /// <summary>
    /// Registers a sweep policy implementation.
    /// Multiple sweep policies can be registered.
    /// </summary>
    public static IServiceCollection AddArkSweepPolicy<TPolicy>(this IServiceCollection services)
        where TPolicy : class, ISweepPolicy
    {
        services.AddSingleton<ISweepPolicy, TPolicy>();
        return services;
    }

    /// <summary>
    /// Registers an event handler for NArk events.
    /// </summary>
    public static IServiceCollection AddArkEventHandler<TEvent, THandler>(this IServiceCollection services)
        where TEvent : class
        where THandler : class, IEventHandler<TEvent>
    {
        services.AddTransient<IEventHandler<TEvent>, THandler>();
        return services;
    }

    #endregion

    #region Transport Configuration Extensions

    /// <summary>
    /// Configures NArk transport and Boltz services using the provided network configuration.
    /// This is the primary method for configuring network connectivity.
    /// </summary>
    public static IServiceCollection AddArkNetwork(this IServiceCollection services, ArkNetworkConfig config)
    {
        // Register the config itself for other services to access
        services.AddSingleton(config);

        // Register transport
        services.AddSingleton<IClientTransport>(_ => new GrpcClientTransport(config.ArkUri));

        // Configure Boltz if URL provided
        if (!string.IsNullOrWhiteSpace(config.BoltzUri))
        {
            services.Configure<BoltzClientOptions>(b =>
            {
                b.BoltzUrl = config.BoltzUri;
                b.WebsocketUrl = config.BoltzUri;
            });
        }

        return services;
    }

    /// <summary>
    /// Configures NArk to connect to mainnet Arkade server.
    /// </summary>
    public static IServiceCollection AddArkMainnet(this IServiceCollection services)
        => services.AddArkNetwork(ArkNetworkConfig.Mainnet);

    /// <summary>
    /// Configures NArk to connect to regtest Arkade server.
    /// </summary>
    public static IServiceCollection AddArkRegtest(this IServiceCollection services)
        => services.AddArkNetwork(ArkNetworkConfig.Regtest);

    /// <summary>
    /// Configures NArk to connect to Mutinynet Arkade server.
    /// </summary>
    public static IServiceCollection AddArkMutinynet(this IServiceCollection services)
        => services.AddArkNetwork(ArkNetworkConfig.Mutinynet);

    /// <summary>
    /// Configures NArk to connect to a custom Ark gRPC server.
    /// </summary>
    public static IServiceCollection AddArkCustomGrpc(this IServiceCollection services, string arkUri)
        => services.AddArkNetwork(new ArkNetworkConfig(arkUri));

    /// <summary>
    /// Configures NArk to use a custom Boltz server.
    /// </summary>
    public static IServiceCollection AddArkCustomBoltz(this IServiceCollection services, string boltzUrl, string? websocketUrl = null)
    {
        services.Configure<BoltzClientOptions>(b =>
        {
            b.BoltzUrl = boltzUrl;
            b.WebsocketUrl = websocketUrl ?? boltzUrl;
        });
        return services;
    }

    /// <summary>
    /// Enables Boltz swap services with optional custom configuration.
    /// </summary>
    public static IServiceCollection AddArkSwaps(this IServiceCollection services, Action<BoltzClientOptions>? configure = null)
    {
        if (configure != null)
        {
            services.Configure(configure);
        }

        services.AddHttpClient<BoltzClient>();
        services.AddSingleton<SwapsManagementService>();
        services.AddSingleton<ISweepPolicy, SwapSweepPolicy>();

        return services;
    }

    #endregion

    #region Options Configuration Extensions

    /// <summary>
    /// Configures the sweeper service force refresh interval.
    /// </summary>
    public static IServiceCollection ConfigureArkSweeperInterval(this IServiceCollection services, TimeSpan interval)
    {
        services.Configure<SweeperServiceOptions>(o => o.ForceRefreshInterval = interval);
        return services;
    }

    /// <summary>
    /// Configures the simple intent scheduler options.
    /// </summary>
    public static IServiceCollection ConfigureArkIntentScheduler(this IServiceCollection services, Action<SimpleIntentSchedulerOptions> configure)
    {
        services.Configure(configure);
        return services;
    }

    #endregion

    #region Validation

    /// <summary>
    /// Required services that must be registered for NArk to function.
    /// </summary>
    private static readonly Type[] RequiredServices =
    [
        typeof(IVtxoStorage),
        typeof(IContractStorage),
        typeof(IIntentStorage),
        typeof(IWalletStorage),
        typeof(IWallet),
        typeof(ISafetyService),
        typeof(IChainTimeProvider),
        typeof(IClientTransport)
    ];

    /// <summary>
    /// Optional services that may be registered for additional functionality.
    /// </summary>
    private static readonly Type[] OptionalServices =
    [
        typeof(ISwapStorage),
        typeof(IKeyStorage),
        typeof(IIntentScheduler),
        typeof(SwapsManagementService)
    ];

    /// <summary>
    /// Validates that all required NArk services are registered.
    /// Call this after all registrations to ensure nothing is missing.
    /// </summary>
    /// <param name="services">The service collection to validate.</param>
    /// <returns>A validation result indicating success or listing missing services.</returns>
    public static ArkRegistrationValidationResult ValidateArkRegistrations(this IServiceCollection services)
    {
        var registeredTypes = services
            .Select(sd => sd.ServiceType)
            .ToHashSet();

        var missingRequired = RequiredServices
            .Where(t => !registeredTypes.Contains(t))
            .ToList();

        var missingOptional = OptionalServices
            .Where(t => !registeredTypes.Contains(t))
            .ToList();

        return new ArkRegistrationValidationResult(
            missingRequired.Count == 0,
            missingRequired,
            missingOptional);
    }

    /// <summary>
    /// Validates that all required NArk services are registered and throws if any are missing.
    /// Call this after all registrations to ensure nothing is missing.
    /// </summary>
    /// <param name="services">The service collection to validate.</param>
    /// <exception cref="InvalidOperationException">Thrown when required services are missing.</exception>
    public static IServiceCollection EnsureArkRegistrations(this IServiceCollection services)
    {
        var result = services.ValidateArkRegistrations();
        if (!result.IsValid)
        {
            var missing = string.Join(", ", result.MissingRequired.Select(t => t.Name));
            throw new InvalidOperationException(
                $"Missing required NArk service registrations: {missing}. " +
                $"Use the AddArk* extension methods to register these services.");
        }

        return services;
    }

    #endregion
}

/// <summary>
/// Result of validating NArk service registrations.
/// </summary>
public sealed class ArkRegistrationValidationResult
{
    public bool IsValid { get; }
    public IReadOnlyList<Type> MissingRequired { get; }
    public IReadOnlyList<Type> MissingOptional { get; }

    internal ArkRegistrationValidationResult(
        bool isValid,
        IReadOnlyList<Type> missingRequired,
        IReadOnlyList<Type> missingOptional)
    {
        IsValid = isValid;
        MissingRequired = missingRequired;
        MissingOptional = missingOptional;
    }

    public override string ToString()
    {
        if (IsValid && MissingOptional.Count == 0)
            return "All NArk services registered.";

        var parts = new List<string>();

        if (!IsValid)
            parts.Add($"Missing required: {string.Join(", ", MissingRequired.Select(t => t.Name))}");

        if (MissingOptional.Count > 0)
            parts.Add($"Missing optional: {string.Join(", ", MissingOptional.Select(t => t.Name))}");

        return string.Join("; ", parts);
    }
}

/// <summary>
/// Network configuration for connecting to Ark and Boltz services.
/// Use preset configurations (Mainnet, Mutinynet, Regtest) or create custom ones.
/// Can be deserialized from JSON configuration files.
/// </summary>
public record ArkNetworkConfig(
    [property: System.Text.Json.Serialization.JsonPropertyName("ark")]
    string ArkUri,

    [property: System.Text.Json.Serialization.JsonPropertyName("arkade-wallet")]
    string? ArkadeWalletUri = null,

    [property: System.Text.Json.Serialization.JsonPropertyName("boltz")]
    string? BoltzUri = null)
{
    /// <summary>
    /// Mainnet configuration
    /// </summary>
    public static readonly ArkNetworkConfig Mainnet = new(
        ArkUri: "https://arkade.computer",
        ArkadeWalletUri: "https://arkade.money",
        BoltzUri: "https://api.ark.boltz.exchange/");

    /// <summary>
    /// Mutinynet configuration
    /// </summary>
    public static readonly ArkNetworkConfig Mutinynet = new(
        ArkUri: "https://mutinynet.arkade.sh",
        ArkadeWalletUri: "https://mutinynet.arkade.money",
        BoltzUri: "https://api.boltz.mutinynet.arkade.sh/");

    /// <summary>
    /// Regtest configuration
    /// </summary>
    public static readonly ArkNetworkConfig Regtest = new(
        ArkUri: "http://localhost:7070",
        ArkadeWalletUri: "http://localhost:3002",
        BoltzUri: "http://localhost:9001/");
}

/// <summary>
/// Fluent configuration builder for NArk services.
/// Use with AddArkade() to configure all required services.
/// </summary>
public sealed class ArkadeConfiguration
{
    private readonly IServiceCollection _services;

    internal ArkadeConfiguration(IServiceCollection services)
    {
        _services = services;
    }

    #region Required Storage

    /// <summary>
    /// Registers a VTXO storage implementation. Required.
    /// </summary>
    public ArkadeConfiguration UseVtxoStorage<TStorage>() where TStorage : class, IVtxoStorage
    {
        _services.AddArkVtxoStorage<TStorage>();
        return this;
    }

    /// <summary>
    /// Registers a contract storage implementation. Required.
    /// </summary>
    public ArkadeConfiguration UseContractStorage<TStorage>() where TStorage : class, IContractStorage
    {
        _services.AddArkContractStorage<TStorage>();
        return this;
    }

    /// <summary>
    /// Registers an intent storage implementation. Required.
    /// </summary>
    public ArkadeConfiguration UseIntentStorage<TStorage>() where TStorage : class, IIntentStorage
    {
        _services.AddArkIntentStorage<TStorage>();
        return this;
    }

    /// <summary>
    /// Registers a wallet storage implementation. Required.
    /// </summary>
    public ArkadeConfiguration UseWalletStorage<TStorage>() where TStorage : class, IWalletStorage
    {
        _services.AddArkWalletStorage<TStorage>();
        return this;
    }

    /// <summary>
    /// Registers a swap storage implementation. Optional (required if using swaps).
    /// </summary>
    public ArkadeConfiguration UseSwapStorage<TStorage>() where TStorage : class, ISwapStorage
    {
        _services.AddArkSwapStorage<TStorage>();
        return this;
    }

    /// <summary>
    /// Registers a key storage implementation. Optional.
    /// </summary>
    public ArkadeConfiguration UseKeyStorage<TStorage>() where TStorage : class, IKeyStorage
    {
        _services.AddArkKeyStorage<TStorage>();
        return this;
    }

    #endregion

    #region Required Services

    /// <summary>
    /// Registers a safety service implementation. Required.
    /// </summary>
    public ArkadeConfiguration UseSafetyService<TSafety>() where TSafety : class, ISafetyService
    {
        _services.AddArkSafetyService<TSafety>();
        return this;
    }

    /// <summary>
    /// Registers a wallet implementation. Required.
    /// </summary>
    public ArkadeConfiguration UseWallet<TWallet>() where TWallet : class, IWallet
    {
        _services.AddArkWallet<TWallet>();
        return this;
    }

    /// <summary>
    /// Registers a chain time provider implementation. Required.
    /// </summary>
    public ArkadeConfiguration UseTimeProvider<TTime>() where TTime : class, IChainTimeProvider
    {
        _services.AddArkTimeProvider<TTime>();
        return this;
    }

    #endregion

    #region Optional Services

    /// <summary>
    /// Registers an intent scheduler implementation. Optional.
    /// </summary>
    public ArkadeConfiguration UseIntentScheduler<TScheduler>() where TScheduler : class, IIntentScheduler
    {
        _services.AddArkIntentScheduler<TScheduler>();
        return this;
    }

    /// <summary>
    /// Configures the intent scheduler options. Optional.
    /// </summary>
    public ArkadeConfiguration ConfigureIntentScheduler(Action<SimpleIntentSchedulerOptions> configure)
    {
        _services.ConfigureArkIntentScheduler(configure);
        return this;
    }

    /// <summary>
    /// Adds a sweep policy. Multiple policies can be added. Optional.
    /// </summary>
    public ArkadeConfiguration AddSweepPolicy<TPolicy>() where TPolicy : class, ISweepPolicy
    {
        _services.AddArkSweepPolicy<TPolicy>();
        return this;
    }

    /// <summary>
    /// Registers an event handler. Optional.
    /// </summary>
    public ArkadeConfiguration AddEventHandler<TEvent, THandler>()
        where TEvent : class
        where THandler : class, IEventHandler<TEvent>
    {
        _services.AddArkEventHandler<TEvent, THandler>();
        return this;
    }

    /// <summary>
    /// Configures the sweeper force refresh interval. Optional.
    /// </summary>
    public ArkadeConfiguration ConfigureSweeperInterval(TimeSpan interval)
    {
        _services.ConfigureArkSweeperInterval(interval);
        return this;
    }

    #endregion

    #region Transport Configuration

    /// <summary>
    /// Configures network connectivity using the provided configuration.
    /// This is the primary method for configuring transport and Boltz URLs.
    /// </summary>
    public ArkadeConfiguration WithNetwork(ArkNetworkConfig config)
    {
        _services.AddArkNetwork(config);
        return this;
    }

    /// <summary>
    /// Configures connection to a custom Ark gRPC server. Required.
    /// </summary>
    public ArkadeConfiguration WithArkServer(string arkUri)
        => WithNetwork(new ArkNetworkConfig(arkUri));

    /// <summary>
    /// Configures connection to mainnet Arkade server.
    /// </summary>
    public ArkadeConfiguration WithMainnet()
        => WithNetwork(ArkNetworkConfig.Mainnet);

    /// <summary>
    /// Configures connection to regtest Arkade server.
    /// </summary>
    public ArkadeConfiguration WithRegtest()
        => WithNetwork(ArkNetworkConfig.Regtest);

    /// <summary>
    /// Configures connection to Mutinynet Arkade server.
    /// </summary>
    public ArkadeConfiguration WithMutinynet()
        => WithNetwork(ArkNetworkConfig.Mutinynet);

    #endregion

    #region Boltz / Swaps Configuration

    /// <summary>
    /// Enables Boltz swap services with the specified URL.
    /// </summary>
    public ArkadeConfiguration WithBoltz(string boltzUrl, string? websocketUrl = null)
    {
        _services.AddArkCustomBoltz(boltzUrl, websocketUrl);
        _services.AddArkSwaps();
        return this;
    }

    /// <summary>
    /// Enables Boltz swap services with custom configuration.
    /// </summary>
    public ArkadeConfiguration WithSwaps(Action<BoltzClientOptions>? configure = null)
    {
        _services.AddArkSwaps(configure);
        return this;
    }

    #endregion

    #region Direct Service Access

    /// <summary>
    /// Provides direct access to the underlying IServiceCollection for advanced scenarios.
    /// </summary>
    public ArkadeConfiguration ConfigureServices(Action<IServiceCollection> configure)
    {
        configure(_services);
        return this;
    }

    #endregion
}
