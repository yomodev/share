using BulkUploader.Core;

namespace BulkUploader.MongoDb;

/// <summary>
/// Factory for creating <see cref="MongoCollectionUploader{T}"/> instances.
///
/// Inject this interface wherever you need to produce documents for a MongoDB
/// collection. The factory owns the connection string, database name, and
/// default parameters — callers only supply the collection name and record type.
///
/// <example>
/// <code>
/// public class EventProcessor(IMongoUploaderFactory factory)
/// {
///     private readonly MongoCollectionUploader&lt;EventDocument&gt; _uploader =
///         factory.Create&lt;EventDocument&gt;("events");
/// }
/// </code>
/// </example>
/// </summary>
public interface IMongoUploaderFactory
{
    /// <summary>
    /// Creates and starts a new <see cref="MongoCollectionUploader{T}"/> for the
    /// given collection.
    ///
    /// The returned uploader is immediately active (background tasks running).
    /// The caller is responsible for disposing it.
    /// </summary>
    /// <typeparam name="T">Document type to insert.</typeparam>
    /// <param name="collectionName">MongoDB collection name.</param>
    /// <param name="databaseName">
    ///   Optional database name override. <c>null</c> uses the factory's configured
    ///   default from <see cref="MongoUploaderOptions.DatabaseName"/>.
    /// </param>
    /// <param name="parameters">
    ///   Optional per-collection parameter overrides. <c>null</c> uses defaults.
    /// </param>
    /// <param name="tuner">Optional per-collection tuner. <c>null</c> uses defaults.</param>
    MongoCollectionUploader<T> Create<T>(
        string              collectionName,
        string?             databaseName = null,
        UploaderParameters? parameters   = null,
        BatchSizeTuner?     tuner        = null);
}
