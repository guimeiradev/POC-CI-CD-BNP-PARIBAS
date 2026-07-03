namespace CicdPoc.Api.Deployments;

public sealed record DeploymentRecord(
    int Id,
    string ArtifactVersion,
    int BuildNumber,
    string ChecksumSha256,
    DateTime DeployedAtUtc,
    string Status);

public sealed record CreateDeploymentRequest(
    string ArtifactVersion,
    int BuildNumber,
    string ChecksumSha256,
    string Status);
