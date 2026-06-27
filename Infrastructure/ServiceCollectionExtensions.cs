using System.Reflection;
using AgentSharp;
using Click.Agents.Common.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Click.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddClickAgents(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAgentRunner, AgentRunner>();
        services.AddSingleton<IAgentRegistry, AgentRegistry>();

        BindOption<AgentRunnerOptions>(services, configuration);
        BindOption<FileToolOptions>(services, configuration);
        BindOption<SearchToolOptions>(services, configuration);
        BindOption<WebReadToolOptions>(services, configuration);
        BindOption<TerminalToolOptions>(services, configuration);
        BindOption<ClickChatOptions>(services, configuration);

        services.AddLogging(b =>
        {
            b.AddConfiguration(configuration.GetSection("Logging"));
            b.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
                o.UseUtcTimestamp = false;
            });
        });

        services.AddSingleton<FileToolHandler>();
        services.AddSingleton<TerminalToolHandler>();
        services.AddSingleton<WebReadToolHandler>();
        services.AddSingleton<SearchToolHandler>(sp =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
            var serperOpt = sp.GetRequiredService<SerperOptions>();
            var logger = sp.GetRequiredService<ILogger<SearchToolHandler>>();
            var options = sp.GetRequiredService<SearchToolOptions>();
            return new SearchToolHandler(http, serperOpt.ApiKey ?? "", logger, options);
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

    private static readonly Dictionary<Type, string> SectionNames = new()
    {
        [typeof(AgentRunnerOptions)] = AgentRunnerOptions.SectionName,
        [typeof(ClickChatOptions)] = ClickChatOptions.SectionName,
        [typeof(FileToolOptions)] = FileToolOptions.SectionName,
        [typeof(SearchToolOptions)] = SearchToolOptions.SectionName,
        [typeof(WebReadToolOptions)] = WebReadToolOptions.SectionName,
        [typeof(TerminalToolOptions)] = TerminalToolOptions.SectionName,
    };

    private static void BindOption<T>(IServiceCollection services, IConfiguration configuration)
        where T : class, new()
    {
        if (!SectionNames.TryGetValue(typeof(T), out var sectionName))
            throw new InvalidOperationException($"No configuration section registered for option type {typeof(T).Name}");
        var section = configuration.GetSection(sectionName);
        services.Configure<T>(section);
        services.AddSingleton(section.Get<T>() ?? new T());
    }
}
