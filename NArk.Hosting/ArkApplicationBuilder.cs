using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Models.Options;
using NArk.Services;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz.Client;
using NArk.Swaps.Boltz.Models;
using NArk.Swaps.Policies;
using NArk.Swaps.Services;
using NArk.Sweeper;
using NArk.Transport;
using NArk.Transport.GrpcClient;

namespace NArk.Hosting;

public static class AppExtensions
{
    public static ArkApplicationBuilder AddArk(this IHostBuilder builder)
    {
        return new ArkApplicationBuilder(builder);
    }

    public class ArkApplicationBuilder : IHostBuilder
    {
        private readonly IHostBuilder _hostBuilder;

        internal ArkApplicationBuilder(IHostBuilder hostBuilder)
        {
            hostBuilder.ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton<ISpendingService, SpendingService>();
                services.AddSingleton<ISigningService, SigningService>();
                services.AddSingleton<IContractService, ContractService>();
                services.AddSingleton<VtxoSynchronizationService>();
                services.AddSingleton<IntentGenerationService>();
                services.AddSingleton<IntentSynchronizationService>();
                services.AddSingleton<BatchManagementService>();
                services.AddSingleton<SweeperService>();
                services.AddHostedService<ArkHostedLifecycle>();
            });
            _hostBuilder = hostBuilder;
        }

        public ArkApplicationBuilder WithSweeperForceRefreshInterval(TimeSpan interval)
        {
            _hostBuilder.ConfigureServices(services =>
                services.Configure<SweeperServiceOptions>(o => { o.ForceRefreshInterval = interval; }));

            return this;
        }

        public ArkApplicationBuilder WithSafetyService<TSafety>() where TSafety : class, ISafetyService
        {
            _hostBuilder.ConfigureServices(services => { services.AddSingleton<ISafetyService, TSafety>(); });
            return this;
        }

        public ArkApplicationBuilder WithVtxoStorage<TStorage>() where TStorage : class, IVtxoStorage
        {
            _hostBuilder.ConfigureServices(services => { services.AddSingleton<IVtxoStorage, TStorage>(); });
            return this;
        }

        public ArkApplicationBuilder WithWalletStorage<TStorage>() where TStorage : class, IWalletStorage
        {
            _hostBuilder.ConfigureServices(services => { services.AddSingleton<IWalletStorage, TStorage>(); });
            return this;
        }

        public ArkApplicationBuilder WithIntentStorage<TStorage>() where TStorage : class, IIntentStorage
        {
            _hostBuilder.ConfigureServices(services => { services.AddSingleton<IIntentStorage, TStorage>(); });
            return this;
        }

        public ArkApplicationBuilder WithSwapStorage<TStorage>() where TStorage : class, ISwapStorage
        {
            _hostBuilder.ConfigureServices(services => { services.AddSingleton<ISwapStorage, TStorage>(); });
            return this;
        }

        public ArkApplicationBuilder WithContractStorage<TStorage>() where TStorage : class, IContractStorage
        {
            _hostBuilder.ConfigureServices(services => { services.AddSingleton<IContractStorage, TStorage>(); });
            return this;
        }

        public ArkApplicationBuilder WithWallet<TWallet>() where TWallet : class, IWallet
        {
            _hostBuilder.ConfigureServices(services => { services.AddSingleton<IWallet, TWallet>(); });
            return this;
        }

        public ArkApplicationBuilder WithIntentScheduler<TScheduler>() where TScheduler : class, IIntentScheduler
        {
            _hostBuilder.ConfigureServices(services => { services.AddSingleton<IIntentScheduler, TScheduler>(); });
            return this;
        }

        public ArkApplicationBuilder WithTimeProvider<TTime>() where TTime : class, IChainTimeProvider
        {
            _hostBuilder.ConfigureServices(services => { services.AddSingleton<IChainTimeProvider, TTime>(); });
            return this;
        }

        public ArkApplicationBuilder OnMainnet()
        {
            _hostBuilder.ConfigureServices(services =>
            {
                services.AddSingleton<IClientTransport, GrpcClientTransport>(_ =>
                    new GrpcClientTransport("https://arkade.computer")
                );
                services.Configure<BoltzClientOptions>(b =>
                {
                    b.BoltzUrl = "https://api.ark.boltz.exchange/";
                    b.WebsocketUrl = "https://api.ark.boltz.exchange/";
                });
            });

            return this;
        }

        public ArkApplicationBuilder OnRegtest()
        {
            _hostBuilder.ConfigureServices(services =>
            {
                services.AddSingleton<IClientTransport, GrpcClientTransport>(_ =>
                    new GrpcClientTransport("http://localhost:7070")
                );
                services.Configure<BoltzClientOptions>(b =>
                {
                    b.BoltzUrl = "http://localhost:9001/";
                    b.WebsocketUrl = "http://localhost:9001/";
                });
            });
            return this;
        }

        public ArkApplicationBuilder OnCustomGrpcArk(string arkUrl)
        {
            _hostBuilder.ConfigureServices(services =>
            {
                services.AddSingleton<IClientTransport, GrpcClientTransport>(_ =>
                    new GrpcClientTransport(arkUrl)
                );
            });

            return this;
        }

        public ArkApplicationBuilder OnCustomBoltz(string boltzUrl, string? websocketUrl)
        {
            _hostBuilder.ConfigureServices(services =>
            {
                services.Configure<BoltzClientOptions>(b =>
                {
                    b.BoltzUrl = boltzUrl;
                    b.WebsocketUrl = websocketUrl ?? boltzUrl;
                });
            });

            return EnableSwaps();
        }

        public ArkApplicationBuilder OnMutinynet()
        {
            _hostBuilder.ConfigureServices(services =>
            {
                services.AddSingleton<IClientTransport, GrpcClientTransport>(_ =>
                    new GrpcClientTransport("https://mutinynet.arkade.money")
                );
                services.Configure<BoltzClientOptions>(b =>
                {
                    b.BoltzUrl = "https://api.boltz.mutinynet.arkade.sh/";
                    b.WebsocketUrl = "https://api.boltz.mutinynet.arkade.sh/";
                });
            });
            return this;
        }

        public ArkApplicationBuilder EnableSwaps(Action<BoltzClientOptions>? boltzOptionsConfigure = null)
        {
            _hostBuilder.ConfigureServices(services =>
            {
                if (boltzOptionsConfigure != null)
                {
                    services.Configure(boltzOptionsConfigure);
                }

                services
                    .AddHttpClient<BoltzClient>()
                    .Services.AddSingleton<SwapsManagementService>();
                services.AddSingleton<ISweepPolicy, SwapSweepPolicy>();
            });
            return this;
        }

        public IHostBuilder ConfigureHostConfiguration(Action<IConfigurationBuilder> configureDelegate)
        {
            return _hostBuilder.ConfigureHostConfiguration(configureDelegate);
        }

        public IHostBuilder ConfigureAppConfiguration(
            Action<HostBuilderContext, IConfigurationBuilder> configureDelegate)
        {
            return _hostBuilder.ConfigureAppConfiguration(configureDelegate);
        }

        public IHostBuilder ConfigureServices(Action<HostBuilderContext, IServiceCollection> configureDelegate)
        {
            return _hostBuilder.ConfigureServices(configureDelegate);
        }

        public IHostBuilder UseServiceProviderFactory<TContainerBuilder>(
            IServiceProviderFactory<TContainerBuilder> factory) where TContainerBuilder : notnull
        {
            return _hostBuilder.UseServiceProviderFactory(factory);
        }

        public IHostBuilder UseServiceProviderFactory<TContainerBuilder>(
            Func<HostBuilderContext, IServiceProviderFactory<TContainerBuilder>> factory)
            where TContainerBuilder : notnull
        {
            return _hostBuilder.UseServiceProviderFactory(factory);
        }

        public IHostBuilder ConfigureContainer<TContainerBuilder>(
            Action<HostBuilderContext, TContainerBuilder> configureDelegate)
        {
            return _hostBuilder.ConfigureContainer(configureDelegate);
        }

        public IHost Build()
        {
            return _hostBuilder.Build();
        }

        public IDictionary<object, object> Properties => _hostBuilder.Properties;
    }
}