namespace PGPM.Persons.DirectorySync;

/// <summary>The directory sync's services (docs/03 §8): the Graph client, the token cache, the run, the task.</summary>
public static class DirectorySyncSetup
{
    public static IServiceCollection AddDirectorySync(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DirectoryOptions>(configuration.GetSection(DirectoryOptions.Section));
        services.AddHttpClient(GraphDirectoryClient.ClientName, client => client.Timeout = TimeSpan.FromSeconds(60));
        services.AddSingleton<GraphTokenCache>();
        services.AddSingleton<GraphDirectoryClient>();
        services.AddScoped<DirectorySyncService>();
        // A singleton the endpoint signals; hosted with the other background tasks (AddDirectoryTasks).
        services.AddSingleton<DirectorySyncTask>();
        return services;
    }

    public static IServiceCollection AddDirectoryTasks(this IServiceCollection services) =>
        services.AddHostedService(sp => sp.GetRequiredService<DirectorySyncTask>());
}
