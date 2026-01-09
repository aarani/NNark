using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Events;
using NArk.Models.Options;
using NArk.Swaps.Abstractions;
using NArk.Swaps.Boltz.Models;

namespace NArk.Hosting;

public static class AppExtensions
{
    public static ArkApplicationBuilder AddArk(this IHostBuilder builder)
    {
        return new ArkApplicationBuilder(builder);
    }

    /// <summary>
    /// Fluent builder for configuring NArk services with IHostBuilder.
    /// For IServiceCollection-only scenarios, use the extension methods in ServiceCollectionExtensions directly.
    /// </summary>
    public class ArkApplicationBuilder : IHostBuilder
    {
        private readonly IHostBuilder _hostBuilder;

        internal ArkApplicationBuilder(IHostBuilder hostBuilder)
        {
            hostBuilder.ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddArkCoreServices();
            });
            _hostBuilder = hostBuilder;
        }

        public ArkApplicationBuilder WithSweeperForceRefreshInterval(TimeSpan interval)
        {
            _hostBuilder.ConfigureServices(services =>
                services.ConfigureArkSweeperInterval(interval));
            return this;
        }

        public ArkApplicationBuilder WithSafetyService<TSafety>() where TSafety : class, ISafetyService
        {
            _hostBuilder.ConfigureServices(services =>
                services.AddArkSafetyService<TSafety>());
            return this;
        }

        public ArkApplicationBuilder WithVtxoStorage<TStorage>() where TStorage : class, IVtxoStorage
        {
            _hostBuilder.ConfigureServices(services =>
                services.AddArkVtxoStorage<TStorage>());
            return this;
        }

        public ArkApplicationBuilder WithKeyStorage<TStorage>() where TStorage : class, IKeyStorage
        {
            _hostBuilder.ConfigureServices(services =>
                services.AddArkKeyStorage<TStorage>());
            return this;
        }

        public ArkApplicationBuilder WithWalletStorage<TStorage>() where TStorage : class, IWalletStorage
        {
            _hostBuilder.ConfigureServices(services =>
                services.AddArkWalletStorage<TStorage>());
            return this;
        }

        public ArkApplicationBuilder WithIntentStorage<TStorage>() where TStorage : class, IIntentStorage
        {
            _hostBuilder.ConfigureServices(services =>
                services.AddArkIntentStorage<TStorage>());
            return this;
        }

        public ArkApplicationBuilder WithSwapStorage<TStorage>() where TStorage : class, ISwapStorage
        {
            _hostBuilder.ConfigureServices(services =>
                services.AddArkSwapStorage<TStorage>());
            return this;
        }

        public ArkApplicationBuilder WithContractStorage<TStorage>() where TStorage : class, IContractStorage
        {
            _hostBuilder.ConfigureServices(services =>
                services.AddArkContractStorage<TStorage>());
            return this;
        }

        public ArkApplicationBuilder WithWallet<TWallet>() where TWallet : class, IWallet
        {
            _hostBuilder.ConfigureServices(services =>
                services.AddArkWallet<TWallet>());
            return this;
        }

        public ArkApplicationBuilder WithIntentScheduler<TScheduler>() where TScheduler : class, IIntentScheduler
        {
            _hostBuilder.ConfigureServices(services =>
                services.AddArkIntentScheduler<TScheduler>());
            return this;
        }

        public ArkApplicationBuilder WithTimeProvider<TTime>() where TTime : class, IChainTimeProvider
        {
            _hostBuilder.ConfigureServices(services =>
                services.AddArkTimeProvider<TTime>());
            return this;
        }

        public ArkApplicationBuilder WithEventHandler<TEvent, THandler>()
            where TEvent : class
            where THandler : class, IEventHandler<TEvent>
        {
            _hostBuilder.ConfigureServices(services =>
                services.AddArkEventHandler<TEvent, THandler>());
            return this;
        }

        public ArkApplicationBuilder OnMainnet()
        {
            _hostBuilder.ConfigureServices(services =>
                services.AddArkMainnet());
            return this;
        }

        public ArkApplicationBuilder OnRegtest()
        {
            _hostBuilder.ConfigureServices(services =>
                services.AddArkRegtest());
            return this;
        }

        public ArkApplicationBuilder OnCustomGrpcArk(string arkUrl)
        {
            _hostBuilder.ConfigureServices(services =>
                services.AddArkCustomGrpc(arkUrl));
            return this;
        }

        public ArkApplicationBuilder OnCustomBoltz(string boltzUrl, string? websocketUrl)
        {
            _hostBuilder.ConfigureServices(services =>
            {
                services.AddArkCustomBoltz(boltzUrl, websocketUrl);
                services.AddArkSwaps();
            });
            return this;
        }

        public ArkApplicationBuilder OnMutinynet()
        {
            _hostBuilder.ConfigureServices(services =>
                services.AddArkMutinynet());
            return this;
        }

        public ArkApplicationBuilder EnableSwaps(Action<BoltzClientOptions>? boltzOptionsConfigure = null)
        {
            _hostBuilder.ConfigureServices(services =>
                services.AddArkSwaps(boltzOptionsConfigure));
            return this;
        }

        #region IHostBuilder Implementation

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

        #endregion
    }
}
