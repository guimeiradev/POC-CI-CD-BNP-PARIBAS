using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CicdPoc.Api.Tests;

public class DeploymentEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public DeploymentEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task PostThenGetDeployment_RoundTripsRecord()
    {
        var artifactVersion = $"1.0.0-test.{Guid.NewGuid():N}";
        var payload = new
        {
            artifactVersion,
            buildNumber = 999,
            checksumSha256 = new string('a', 64),
            status = "success"
        };

        var postResponse = await _client.PostAsJsonAsync("/deployments", payload);
        postResponse.EnsureSuccessStatusCode();

        var getResponse = await _client.GetAsync("/deployments");
        getResponse.EnsureSuccessStatusCode();

        var body = await getResponse.Content.ReadAsStringAsync();
        Assert.Contains(artifactVersion, body);
    }
}
