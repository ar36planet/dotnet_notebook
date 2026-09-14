using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace UserApi.Tests;

public sealed class AuthorizationTests(
    WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Anonymous_request_returns_401()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/users/00000000-0000-0000-0000-000000000001");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_user_without_permission_returns_403()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/users/active");
        request.Headers.Add("X-Demo-User", "ada");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task User_with_read_permission_reaches_endpoint()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/users/active");
        request.Headers.Add("X-Demo-User", "ada");
        request.Headers.Add("X-Demo-Permission", "users.read");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "Ada",
            await response.Content.ReadAsStringAsync());
    }
}
