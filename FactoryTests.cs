using System.Data;
using BulkUploader.Core;
using BulkUploader.MongoDb;
using BulkUploader.SqlServer;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BulkUploader.Tests;

public sealed class SqlUploaderFactoryTests
{
    private static ISqlUploaderFactory BuildFactory(
        string connectionString = "Server=.;Database=Test;Trusted_Connection=True;",
        Action<SqlUploaderOptions>? configure = null)
    {
        var options = new SqlUploaderOptions { ConnectionString = connectionString };
        configure?.Invoke(options);
        return new SqlUploaderFactory(
            Options.Create(options),
            NullLoggerFactory.Instance);
    }

    private static DataTable MakeSchema(params (string name, Type type)[] columns)
    {
        var dt = new DataTable();
        foreach (var (name, type) in columns) dt.Columns.Add(name, type);
        return dt;
    }

    // ── Construction ──────────────────────────────────────────────────────────

    [Fact]
    public void Create_ReturnsUploaderWithCorrectDestinationName()
    {
        var factory  = BuildFactory();
        var schema   = MakeSchema(("Id", typeof(int)));
        using var up = factory.Create<int>("dbo.Orders", schema, (r, row) => { row["Id"] = r; return row; });

        up.DestinationName.Should().Be("SQL:dbo.Orders");
    }

    [Fact]
    public void Create_DifferentTables_ReturnDistinctInstances()
    {
        var factory  = BuildFactory();
        var schema   = MakeSchema(("Id", typeof(int)));
        Func<int, DataRow, DataRow> mapper = (r, row) => { row["Id"] = r; return row; };

        using var up1 = factory.Create<int>("dbo.TableA", schema, mapper);
        using var up2 = factory.Create<int>("dbo.TableB", schema, mapper);

        up1.Should().NotBeSameAs(up2);
        up1.DestinationName.Should().Be("SQL:dbo.TableA");
        up2.DestinationName.Should().Be("SQL:dbo.TableB");
    }

    [Fact]
    public void Create_WithPerTableTunerOverride_UsesThatTuner()
    {
        var factory     = BuildFactory();
        var schema      = MakeSchema(("Id", typeof(int)));
        var customTuner = new BatchSizeTuner(initial: 9999, min: 9999, max: 10000);

        using var up = factory.Create<int>(
            "dbo.Orders", schema,
            (r, row) => { row["Id"] = r; return row; },
            tuner: customTuner);

        up.Tuner.BatchSize.Should().Be(9999);
    }

    [Fact]
    public void Create_WithPerTableParametersOverride_UsesThoseParameters()
    {
        var factory      = BuildFactory();
        var schema       = MakeSchema(("Id", typeof(int)));
        var customParams = new UploaderParameters(maxRetries: 99);

        using var up = factory.Create<int>(
            "dbo.Orders", schema,
            (r, row) => { row["Id"] = r; return row; },
            parameters: customParams);

        up.Parameters.MaxRetries.Should().Be(99);
    }

    [Fact]
    public void Create_UsesDefaultsFromOptions_WhenNoOverrides()
    {
        var factory = BuildFactory(configure: o =>
        {
            o.DefaultParameters.MaxRetries       = 7;
            o.DefaultTuner.Initial               = 4321;
        });

        var schema = MakeSchema(("Id", typeof(int)));
        using var up = factory.Create<int>("dbo.X", schema, (r, row) => { row["Id"] = r; return row; });

        up.Parameters.MaxRetries.Should().Be(7);
        up.Tuner.BatchSize.Should().Be(4321);
    }

    // ── Validation ────────────────────────────────────────────────────────────

    [Fact]
    public void Factory_ThrowsOnEmptyConnectionString()
    {
        var act = () => BuildFactory(connectionString: "");
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*ConnectionString*");
    }

    [Fact]
    public void Create_ThrowsOnNullOrEmptyTableName()
    {
        var factory = BuildFactory();
        var schema  = MakeSchema(("Id", typeof(int)));
        Func<int, DataRow, DataRow> mapper = (r, row) => row;

        var actNull  = () => factory.Create<int>(null!,  schema, mapper);
        var actEmpty = () => factory.Create<int>("",     schema, mapper);
        var actBlank = () => factory.Create<int>("  ",   schema, mapper);

        actNull.Should().Throw<ArgumentNullException>();
        actEmpty.Should().Throw<ArgumentException>();
        actBlank.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_ThrowsOnNullSchema()
    {
        var factory = BuildFactory();
        var act = () => factory.Create<int>("dbo.X", null!, (r, row) => row);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Create_ThrowsOnNullRowMapper()
    {
        var factory = BuildFactory();
        var schema  = MakeSchema(("Id", typeof(int)));
        var act = () => factory.Create<int>("dbo.X", schema, null!);
        act.Should().Throw<ArgumentNullException>();
    }
}

public sealed class MongoUploaderFactoryTests
{
    private static IMongoUploaderFactory BuildFactory(
        string connectionString = "mongodb://localhost:27017",
        string databaseName     = "testdb",
        Action<MongoUploaderOptions>? configure = null)
    {
        var options = new MongoUploaderOptions
        {
            ConnectionString = connectionString,
            DatabaseName     = databaseName,
        };
        configure?.Invoke(options);
        return new MongoUploaderFactory(
            Options.Create(options),
            NullLoggerFactory.Instance);
    }

    // ── Construction ──────────────────────────────────────────────────────────

    [Fact]
    public void Create_ReturnsUploaderWithCorrectDestinationName()
    {
        var factory = BuildFactory();
        using var up = factory.Create<object>("events");

        up.DestinationName.Should().Be("Mongo:testdb/events");
    }

    [Fact]
    public void Create_WithDatabaseOverride_UsesOverride()
    {
        var factory = BuildFactory();
        using var up = factory.Create<object>("metrics", databaseName: "metrics_db");

        up.DestinationName.Should().Be("Mongo:metrics_db/metrics");
    }

    [Fact]
    public void Create_DifferentCollections_ReturnDistinctInstances()
    {
        var factory = BuildFactory();
        using var up1 = factory.Create<object>("col_a");
        using var up2 = factory.Create<object>("col_b");

        up1.Should().NotBeSameAs(up2);
        up1.DestinationName.Should().Be("Mongo:testdb/col_a");
        up2.DestinationName.Should().Be("Mongo:testdb/col_b");
    }

    [Fact]
    public void Create_WithPerCollectionOverrides_UsesThoseValues()
    {
        var factory     = BuildFactory();
        var customTuner = new BatchSizeTuner(initial: 1234, min: 1234, max: 2000);
        var customParams = new UploaderParameters(maxRetries: 42);

        using var up = factory.Create<object>("col", tuner: customTuner, parameters: customParams);

        up.Tuner.BatchSize.Should().Be(1234);
        up.Parameters.MaxRetries.Should().Be(42);
    }

    [Fact]
    public void Create_UsesDefaultsFromOptions_WhenNoOverrides()
    {
        var factory = BuildFactory(configure: o =>
        {
            o.DefaultParameters.FlushAfterIdleMs = 777;
            o.DefaultTuner.Initial               = 888;
        });

        using var up = factory.Create<object>("col");

        up.Parameters.FlushAfterIdleMs.Should().Be(777);
        up.Tuner.BatchSize.Should().Be(888);
    }

    // ── Validation ────────────────────────────────────────────────────────────

    [Fact]
    public void Factory_ThrowsOnEmptyConnectionString()
    {
        var act = () => BuildFactory(connectionString: "");
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*ConnectionString*");
    }

    [Fact]
    public void Create_ThrowsOnEmptyCollectionName()
    {
        var factory = BuildFactory();
        var act = () => factory.Create<object>("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_ThrowsWhenNoDatabaseNameAvailable()
    {
        var factory = BuildFactory(databaseName: ""); // no default
        var act = () => factory.Create<object>("col"); // no override either
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*database name*");
    }
}
