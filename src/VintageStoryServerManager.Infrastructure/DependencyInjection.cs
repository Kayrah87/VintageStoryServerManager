using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VintageStoryServerManager.Core.Configuration;

namespace VintageStoryServerManager.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers infrastructure services: file-system layout now, and later the
    /// server process supervisor, mod scanner, ModDB client and backup service.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DataPathOptions>()
            .Bind(configuration.GetSection(DataPathOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.Root), "DataPaths:Root must be set.")
            .ValidateOnStart();

        return services;
    }
}
