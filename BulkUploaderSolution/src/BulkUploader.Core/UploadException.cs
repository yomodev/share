namespace BulkUploader.Core;

/// <summary>
/// Thrown (and set on job <see cref="JobToken.Fault"/>) when a batch permanently
/// fails after all configured retries are exhausted, or when a connection cannot
/// be established.
/// </summary>
public sealed class UploadException : Exception
{
    public string DestinationName { get; }

    public UploadException(string destinationName, string message, Exception innerException)
        : base($"[{destinationName}] {message}", innerException)
    {
        DestinationName = destinationName;
    }
}
