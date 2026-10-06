using System.Net;

namespace MMW.Metadata.Http;

/// <summary>A provider endpoint answered with a non-success HTTP status.</summary>
public sealed class ProviderHttpException : HttpRequestException
{
    /// <summary>Creates the exception.</summary>
    public ProviderHttpException(string message, HttpStatusCode statusCode)
        : base(message, null, statusCode)
    {
    }

    /// <summary>Creates the exception.</summary>
    public ProviderHttpException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public ProviderHttpException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public ProviderHttpException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
