using System.Reflection;
using AgentSharp;
using Click.Agents.Common.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Click.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddClickAgents(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAgentRunner, AgentRunner>();
        services.AddSingleton<IAgentRegistry, AgentRegistry>();

        services.AddSingleton<FileToolHandler>();
        services.AddSingleton<TerminalToolHandler>();
        services.AddSingleton<WebReadToolHandler>();
        services.AddSingleton<SearchToolHandler>(sp =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
            var opt = sp.GetRequiredService<SerperOptions>();
            return new SearchToolHandler(http, opt.ApiKey ?? "");
        });

        var agentTypes = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(t => typeof(IAgent).IsAssignableFrom(t)
                        && !t.IsAbstract
                        && !t.IsInterface);

        foreach (var type in agentTypes)
        {
            services.AddSingleton(typeof(IAgent), type);
        }

        return services;
    }
}
