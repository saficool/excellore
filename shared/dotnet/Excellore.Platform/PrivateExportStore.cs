using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;

namespace Excellore.Platform;

public interface IPrivateExportStore
{
    Task PutAsync(Guid exportId, string part, byte[] data, CancellationToken cancellationToken);
    Task<byte[]?> GetAsync(Guid exportId, string part, CancellationToken cancellationToken);
    Task DeleteAsync(Guid exportId, string part, CancellationToken cancellationToken);
}

/// <summary>Private cross-service export staging. Events carry IDs only, never data or keys.</summary>
public sealed class S3PrivateExportStore : IPrivateExportStore, IDisposable
{
    private const int MaxBytes = 64 * 1024 * 1024;
    private static readonly HashSet<string> Parts =
        ["access", "profile", "complete"];
    private readonly AmazonS3Client client;
    private readonly string bucket;
    private readonly bool localDevelopment;

    public S3PrivateExportStore(IConfiguration configuration)
    {
        var endpoint = configuration["ExportStore:Endpoint"];
        var accessKey = configuration["ExportStore:AccessKey"];
        var secretKey = configuration["ExportStore:SecretKey"];
        bucket = configuration["ExportStore:Bucket"] ?? "excellore-private-exports";
        localDevelopment = string.Equals(configuration["EXCELLORE_INSECURE_LOCAL_DEV"],
            "true", StringComparison.OrdinalIgnoreCase);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(localDevelopment &&
                uri.Scheme == Uri.UriSchemeHttp && uri.Host is "127.0.0.1" or "localhost")) ||
            uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
            throw new ArgumentException("Private export object store configuration is invalid.");
        client = new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey),
            new AmazonS3Config { ServiceURL = uri.GetLeftPart(UriPartial.Authority),
                ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
    }

    public async Task PutAsync(Guid exportId, string part, byte[] data,
        CancellationToken cancellationToken)
    {
        var key = Key(exportId, part);
        if (data.Length is 0 or > MaxBytes)
            throw new InvalidOperationException("Private export size is invalid.");
        using var stream = new MemoryStream(data, writable: false);
        var request = new PutObjectRequest
        {
            BucketName = bucket, Key = key, ContentType = "application/json",
            InputStream = stream
        };
        try { await client.PutObjectAsync(request, cancellationToken); }
        catch (AmazonS3Exception error) when (localDevelopment &&
            error.ErrorCode is "NoSuchBucket" or "NotFound")
        {
            await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket },
                cancellationToken);
            stream.Position = 0;
            await client.PutObjectAsync(request, cancellationToken);
        }
    }

    public async Task<byte[]?> GetAsync(Guid exportId, string part,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetObjectAsync(bucket, Key(exportId, part),
                cancellationToken);
            if (response.ContentLength > MaxBytes) return null;
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken);
            return buffer.Length <= MaxBytes ? buffer.ToArray() : null;
        }
        catch (AmazonS3Exception error) when (error.ErrorCode is "NoSuchKey" or "NotFound")
        { return null; }
    }

    public Task DeleteAsync(Guid exportId, string part, CancellationToken cancellationToken) =>
        client.DeleteObjectAsync(bucket, Key(exportId, part), cancellationToken);

    private static string Key(Guid exportId, string part)
    {
        if (exportId == Guid.Empty || !Parts.Contains(part))
            throw new ArgumentException("Private export key is invalid.");
        return $"exports/{exportId:D}/{part}.json";
    }

    public void Dispose() => client.Dispose();
}
