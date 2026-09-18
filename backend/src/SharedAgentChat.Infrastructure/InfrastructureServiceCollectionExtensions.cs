using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SharedAgentChat.Application.Ports;

namespace SharedAgentChat.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddSharedAgentChatInfrastructure(
        this IServiceCollection services,
        IHostEnvironment environment)
    {
        _ = environment;
        services.AddSingleton<InMemorySessionRepository>();
        services.AddSingleton<ISessionRepository>(sp =>
        {
            var mode = sp.GetRequiredService<IOptions<SessionStoreOptions>>().Value.Mode;
            var memory = sp.GetRequiredService<InMemorySessionRepository>();
            if (mode.Equals("Memory", StringComparison.OrdinalIgnoreCase))
            {
                return memory;
            }

            return ActivatorUtilities.CreateInstance<FileSessionRepository>(sp, memory);
        });

        services.AddSingleton<SessionAgentWorkQueue>();
        services.AddSingleton<IAgentWorkQueue>(sp => sp.GetRequiredService<SessionAgentWorkQueue>());
        services.AddHostedService(sp => sp.GetRequiredService<SessionAgentWorkQueue>());

        services.AddHttpClient(ComposerAgentAdapter.HttpClientName, (sp, client) =>
        {
            var composer = sp.GetRequiredService<IOptions<AgentOptions>>().Value.Composer;
            client.BaseAddress = new Uri(composer.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromMinutes(10);
        });
        services.AddSingleton<ComposerAgentAdapter>();
        services.AddSingleton<StubAgentAdapter>();
        services.AddSingleton<IAgentAdapter>(sp =>
        {
            var provider = sp.GetRequiredService<IOptions<AgentOptions>>().Value.Provider;
            if (provider.Equals("Composer", StringComparison.OrdinalIgnoreCase))
            {
                return sp.GetRequiredService<ComposerAgentAdapter>();
            }

            return sp.GetRequiredService<StubAgentAdapter>();
        });

        return services;
    }
}
