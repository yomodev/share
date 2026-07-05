using System.Runtime.CompilerServices;

namespace NxtUI.Tests.Integration;

/// <summary>
/// Marks a test class as containing integration tests that require external infrastructure.
/// Tests are skipped unless the environment variable <c>RUN_INTEGRATION_TESTS=1</c> is set.
/// Tests that additionally require a real Kafka broker also check <c>KAFKA_BOOTSTRAP_SERVERS</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class IntegrationTestAttribute : Attribute
{
    public IntegrationTestAttribute() { }
}

/// <summary>
/// xUnit Fact that is automatically skipped when <c>RUN_INTEGRATION_TESTS != "1"</c>.
/// </summary>
public sealed class IntegrationTestFactAttribute : FactAttribute
{
    public IntegrationTestFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (Environment.GetEnvironmentVariable("RUN_INTEGRATION_TESTS") != "1")
            Skip = "Set RUN_INTEGRATION_TESTS=1 to run integration tests.";
    }
}

/// <summary>
/// xUnit Fact that requires both <c>RUN_INTEGRATION_TESTS=1</c> and a reachable Kafka broker
/// pointed to by <c>KAFKA_BOOTSTRAP_SERVERS</c>.
/// </summary>
public sealed class KafkaIntegrationTestFactAttribute : FactAttribute
{
    public KafkaIntegrationTestFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (Environment.GetEnvironmentVariable("RUN_INTEGRATION_TESTS") != "1")
        {
            Skip = "Set RUN_INTEGRATION_TESTS=1 to run integration tests.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS")))
            Skip = "Set KAFKA_BOOTSTRAP_SERVERS=<host:port> to run Kafka integration tests.";
    }
}
