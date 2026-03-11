using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BulkUploader.MongoDb;

/// <summary>
/// DI registration extensions for the MongoDB bulk uploader factory.
/// </summary>
public static class MongoDbServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IMongoUploaderFactory"/> as a singleton, bound to the
    /// <c>BulkUploader:MongoDb</c> configuration section by default.
    ///
    /// <example>
    /// Minimal registration (reads from appsettings.json):
    /// <code>
    /// services.AddMongoBulkUploader(configuration);
    /// </code>
    ///
    /// With inline override:
    /// <code>
    /// services.AddMongoBulkUploader(configuration, options =>
    /// {
    ///     options.ConnectionString = "mongodb://localhost:27017";
    ///     options.DatabaseName     = "mydb";
    /// });
    /// </code>
    /// </example>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The root configuration (used to bind the section).</param>
    /// <param name="configureOptions">Optional inline overrides applied after configuration binding.</param>
    /// <param name="sectionPath">Configuration section path. Default: <c>BulkUploader:MongoDb</c>.</param>
    public static IServiceCollection AddMongoBulkUploader(
        this IServiceCollection      services,
        IConfiguration               configuration,
        Action<MongoUploaderOptions>? configureOptions = null,
        string                       sectionPath       = "BulkUploader:MongoDb")
    {
        services.Configure<MongoUploaderOptions>(
            configuration.GetSection(sectionPath));

        if (configureOptions is not null)
            services.PostConfigure<MongoUploaderOptions>(configureOptions);

        services.AddSingleton<IMongoUploaderFactory, MongoUploaderFactory>();

        return services;
    }
}
