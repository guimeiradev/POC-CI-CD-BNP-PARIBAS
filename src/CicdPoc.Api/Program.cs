using Dapper;
using CicdPoc.Api.Data;
using CicdPoc.Api.Deployments;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("CicdPocDb")
    ?? throw new InvalidOperationException("ConnectionStrings:CicdPocDb is not configured.");
builder.Services.AddSingleton<IDbConnectionFactory>(new SqlConnectionFactory(connectionString));

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapPost("/deployments", async (CreateDeploymentRequest request, IDbConnectionFactory connectionFactory) =>
{
    if (string.IsNullOrWhiteSpace(request.ArtifactVersion) ||
        string.IsNullOrWhiteSpace(request.ChecksumSha256) ||
        string.IsNullOrWhiteSpace(request.Status))
    {
        return Results.BadRequest(new { error = "artifactVersion, checksumSha256 and status are required." });
    }

    using var connection = connectionFactory.Create();
    const string sql = """
        INSERT INTO deployment_record (artifact_version, build_number, checksum_sha256, deployed_at_utc, status)
        OUTPUT INSERTED.id
        VALUES (@ArtifactVersion, @BuildNumber, @ChecksumSha256, SYSUTCDATETIME(), @Status);
        """;
    var id = await connection.ExecuteScalarAsync<int>(sql, request);
    return Results.Created($"/deployments/{id}", new { id });
});

app.MapGet("/deployments", async (IDbConnectionFactory connectionFactory) =>
{
    using var connection = connectionFactory.Create();
    const string sql = """
        SELECT id AS Id, artifact_version AS ArtifactVersion, build_number AS BuildNumber,
               checksum_sha256 AS ChecksumSha256, deployed_at_utc AS DeployedAtUtc, status AS Status
        FROM deployment_record
        ORDER BY deployed_at_utc DESC;
        """;
    var records = await connection.QueryAsync<DeploymentRecord>(sql);
    return Results.Ok(records);
});

app.Run();

public partial class Program {}
