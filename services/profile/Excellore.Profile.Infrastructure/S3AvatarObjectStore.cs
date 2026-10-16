using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;

namespace Excellore.Profile.Infrastructure;

public interface IAvatarObjectStore
{
    Task PutAsync(string key, byte[] image, CancellationToken cancellationToken);
    Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken);
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

public sealed class S3AvatarObjectStore : IAvatarObjectStore, IDisposable
{
    private readonly AmazonS3Client client;
    private readonly string bucket;
    private readonly bool localDevelopment;

    public S3AvatarObjectStore(IConfiguration configuration)
    {
        var endpoint = configuration["AvatarStore:Endpoint"];
        var accessKey = configuration["AvatarStore:AccessKey"];
        var secretKey = configuration["AvatarStore:SecretKey"];
        bucket = configuration["AvatarStore:Bucket"] ?? "excellore-avatars";
        localDevelopment = string.Equals(configuration["EXCELLORE_INSECURE_LOCAL_DEV"], "true",
            StringComparison.OrdinalIgnoreCase);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps &&
                !(localDevelopment && uri.Scheme == Uri.UriSchemeHttp &&
                  (uri.Host == "127.0.0.1" || uri.Host == "localhost"))) ||
            uri.UserInfo.Length > 0 || !string.IsNullOrEmpty(uri.Query) ||
            string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
            throw new ArgumentException("Avatar object store configuration is invalid.");
        client = new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey),
            new AmazonS3Config { ServiceURL = uri.GetLeftPart(UriPartial.Authority),
                ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
    }

    public async Task PutAsync(string key, byte[] image, CancellationToken ct)
    {
        using var stream = new MemoryStream(image, writable: false);
        var request = new PutObjectRequest
        {
            BucketName = bucket, Key = key, ContentType = "image/webp",
            InputStream = stream
        };
        try
        {
            await client.PutObjectAsync(request, ct);
        }
        catch (AmazonS3Exception error) when (localDevelopment &&
            error.ErrorCode is "NoSuchBucket" or "NotFound")
        {
            await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket }, ct);
            stream.Position = 0;
            await client.PutObjectAsync(request, ct);
        }
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        try
        {
            using var response = await client.GetObjectAsync(bucket, key, ct);
            if (response.ContentLength > 1024 * 1024) return null;
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, ct);
            return buffer.Length <= 1024 * 1024 ? buffer.ToArray() : null;
        }
        catch (AmazonS3Exception error) when (error.ErrorCode is "NoSuchKey" or "NotFound")
        {
            return null;
        }
    }

    public Task DeleteAsync(string key, CancellationToken ct) =>
        client.DeleteObjectAsync(bucket, key, ct);

    public void Dispose() => client.Dispose();
}
