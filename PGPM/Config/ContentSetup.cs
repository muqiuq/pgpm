using PGPM.Files;
using PGPM.Packages;
using PGPM.Sharing;

namespace PGPM.Config;

/// <summary>
/// Phases 3–5's services (docs/03 §3.1): the configuration's admin endpoints and packages, map sharing, the file pipeline
/// (Files:*) and its background tasks.
/// </summary>
public static class ContentSetup
{
    public static IServiceCollection AddContent(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<FilesOptions>(configuration.GetSection("Files"));
        services.Configure<PackageOptions>(configuration.GetSection("Packages"));
        services.AddSingleton<FileStore>();
        services.AddSingleton<UploadGuard>();
        services.AddScoped<FileService>();
        services.AddScoped<ConfigService>();
        services.AddScoped<MapSharingService>();
        services.AddScoped<PackageImporter>();
        return services;
    }

    /// <summary>The first start's packages: after the migration, before anything else serves.</summary>
    public static IServiceCollection AddPackageSeeder(this IServiceCollection services) => services.AddHostedService<ConfigPackageSeeder>();

    /// <summary>The files' cleanup and check (docs/03 §9), with the other background tasks.</summary>
    public static IServiceCollection AddFileTasks(this IServiceCollection services)
    {
        services.AddHostedService<FileCleanupTask>();
        services.AddHostedService<FileCheckTask>();
        return services;
    }
}
