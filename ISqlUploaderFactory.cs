using System.Data;
using BulkUploader.Core;

namespace BulkUploader.SqlServer;

/// <summary>
/// Factory for creating <see cref="SqlTableUploader{T}"/> instances.
///
/// Inject this interface wherever you need to produce data for a SQL Server table.
/// The factory owns the connection string and default parameters — callers only
/// supply what varies per table: name, schema, and row mapper.
///
/// <example>
/// <code>
/// public class OrderProcessor(ISqlUploaderFactory factory)
/// {
///     private readonly SqlTableUploader&lt;OrderRecord&gt; _uploader =
///         factory.Create&lt;OrderRecord&gt;(
///             tableName:  "dbo.Orders",
///             schema:     OrderSchema.DataTable,
///             rowMapper:  (rec, row) => { row["Id"] = rec.Id; return row; });
/// }
/// </code>
/// </example>
/// </summary>
public interface ISqlUploaderFactory
{
    /// <summary>
    /// Creates and starts a new <see cref="SqlTableUploader{T}"/> for the given table.
    ///
    /// The returned uploader is immediately active (background tasks running).
    /// The caller is responsible for disposing it — typically via
    /// <c>await using</c> or by registering with a container lifetime.
    /// </summary>
    /// <typeparam name="T">Record type to upload.</typeparam>
    /// <param name="tableName">
    ///   Fully-qualified SQL table name, e.g. <c>dbo.Orders</c>.
    /// </param>
    /// <param name="schema">
    ///   A <see cref="DataTable"/> describing the target table's columns (schema only;
    ///   rows are ignored). Column names must match the destination table exactly.
    /// </param>
    /// <param name="rowMapper">
    ///   Populates a <see cref="DataRow"/> from a record. Called once per record
    ///   inside the uploader pipeline — always on the single uploader task.
    /// </param>
    /// <param name="parameters">
    ///   Optional per-table parameter overrides. <c>null</c> uses the factory's
    ///   configured defaults from <see cref="SqlUploaderOptions"/>.
    /// </param>
    /// <param name="tuner">
    ///   Optional per-table tuner. <c>null</c> uses the factory's configured defaults.
    /// </param>
    SqlTableUploader<T> Create<T>(
        string                    tableName,
        DataTable                 schema,
        Func<T, DataRow, DataRow> rowMapper,
        UploaderParameters?       parameters = null,
        BatchSizeTuner?           tuner      = null);
}
