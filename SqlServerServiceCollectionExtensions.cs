using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BulkUploader.SqlServer;

/// <summary>
/// DI registration extensions for the SQL Server bulk uploader factory.
/// </summary>
public static class SqlServerServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ISqlUploaderFactory"/> as a singleton, bound to the
    /// <c>BulkUploader:SqlServer</c> configuration section by default.
    ///
    /// <example>
    /// Minimal registration (reads from appsettings.json):
    /// <code>
    /// services.AddSqlBulkUploader(configuration);
    /// </code>
    ///
    /// With inline override:
    /// <code>
    /// services.AddSqlBulkUploader(configuration, options =>
    /// {
    ///     options.ConnectionString = "Server=.;...";
    ///     options.DefaultTuner.Initial = 5_000;
    /// });
    /// </code>
    /// </example>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The root configuration (used to bind the section).</param>
    /// <param name="configureOptions">Optional inline overrides applied after configuration binding.</param>
    /// <param name="sectionPath">Configuration section path. Default: <c>BulkUploader:SqlServer</c>.</param>
    public static IServiceCollection AddSqlBulkUploader(
        this IServiceCollection    services,
        IConfiguration             configuration,
        Action<SqlUploaderOptions>? configureOptions = null,
        string                     sectionPath       = "BulkUploader:SqlServer")
    {
        // Bind from configuration first, then apply any inline overrides.
        services.Configure<SqlUploaderOptions>(
            configuration.GetSection(sectionPath));

        if (configureOptions is not null)
            services.PostConfigure<SqlUploaderOptions>(configureOptions);

        services.AddSingleton<ISqlUploaderFactory, SqlUploaderFactory>();

        return services;
    }
}
