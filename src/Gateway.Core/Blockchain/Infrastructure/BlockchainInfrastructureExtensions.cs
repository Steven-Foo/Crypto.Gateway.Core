using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Contracts.Providers;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Addresses;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers;
using CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure.Providers.Tron;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CryptoPaymentEngine.Gateway.Core.Blockchain.Infrastructure;

public static class BlockchainInfrastructureExtensions
{
    /// <summary>
    /// Registers the address-encoding capability the Blockchain module owns (§8): a public key → chain
    /// address. Other modules (e.g. KeyManagement) consume <see cref="IAddressEncoderFactory"/> through
    /// this registration — they must not reference these concrete encoders, so the composition root
    /// wires this alongside the consuming module (§4.5).
    /// </summary>
    public static IServiceCollection AddBlockchainAddressEncoding(this IServiceCollection services)
    {
        // Capability segregation: only the schemes that exist are registered; an unsupported chain is
        // simply absent rather than a throwing adapter.
        services.AddSingleton<IAddressEncoder, EthereumAddressEncoder>();
        services.AddSingleton<IAddressEncoder, TronAddressEncoder>();
        services.AddSingleton<IAddressEncoder, SolanaAddressEncoder>();
        services.AddSingleton<IAddressEncoderFactory, AddressEncoderFactory>();
        return services;
    }

    /// <summary>
    /// Registers the in-memory chain source as the read-only chain capabilities (Development/tests).
    /// Staging/production swap this line for the real per-chain JSON-RPC adapters — no consumer changes,
    /// because everything depends on <see cref="IDepositScanner"/>/<see cref="IChainStatusReader"/>, not
    /// on a concrete provider (§8). One instance backs both ports so a test drives a single chain view.
    /// </summary>
    public static IServiceCollection AddInMemoryChainSource(this IServiceCollection services)
    {
        services.TryAddSingleton<InMemoryChainSource>();
        services.TryAddSingleton<IDepositScanner>(sp => sp.GetRequiredService<InMemoryChainSource>());
        services.TryAddSingleton<IChainStatusReader>(sp => sp.GetRequiredService<InMemoryChainSource>());
        return services;
    }

    /// <summary>
    /// Routes the capability ports to the registered per-chain adapters (the real-provider counterpart
    /// to <see cref="AddInMemoryChainSource"/>). Call this plus one <c>Add…ChainAdapter</c> per chain.
    /// </summary>
    public static IServiceCollection AddJsonRpcChainSources(this IServiceCollection services)
    {
        services.TryAddScoped<RoutingChainSource>();
        services.TryAddScoped<IDepositScanner>(sp => sp.GetRequiredService<RoutingChainSource>());
        services.TryAddScoped<IChainStatusReader>(sp => sp.GetRequiredService<RoutingChainSource>());
        return services;
    }

    /// <summary>
    /// Registers the in-memory transaction builder + broadcaster (Development/tests) — the DI seam the
    /// real per-chain builder/broadcaster replaces. Read/compute + broadcast of already-signed blobs
    /// only; it never signs (§10).
    /// </summary>
    public static IServiceCollection AddInMemoryTransactionEngine(this IServiceCollection services)
    {
        services.TryAddSingleton<InMemoryTransactionEngine>();
        services.TryAddSingleton<ITransactionBuilder>(sp => sp.GetRequiredService<InMemoryTransactionEngine>());
        services.TryAddSingleton<ITransactionBroadcaster>(sp => sp.GetRequiredService<InMemoryTransactionEngine>());
        return services;
    }

    /// <summary>
    /// Registers the TRON read-only adapter over a resilient typed <see cref="System.Net.Http.HttpClient"/>
    /// (TronGrid/full-node HTTP). Config lives under <c>Chains:Tron</c>. Read-only — no keys ever cross it (§10).
    /// </summary>
    public static IServiceCollection AddTronChainAdapter(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(TronOptions.SectionName).Get<TronOptions>() ?? new TronOptions();

        services.AddHttpClient<ITronRpc, TronRpc>(client =>
        {
            var baseUrl = options.RpcBaseUrl.EndsWith('/') ? options.RpcBaseUrl : options.RpcBaseUrl + "/";
            client.BaseAddress = new Uri(baseUrl);
            if (!string.IsNullOrWhiteSpace(options.ApiKey))
                client.DefaultRequestHeaders.Add("TRON-PRO-API-KEY", options.ApiKey);
        }).AddStandardResilienceHandler();

        services.AddScoped<IChainAdapter, TronChainAdapter>();
        return services;
    }
}
