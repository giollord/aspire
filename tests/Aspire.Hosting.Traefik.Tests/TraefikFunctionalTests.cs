// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;

namespace Aspire.Hosting.Traefik.Tests;

public class TraefikFunctionalTests(ITestOutputHelper testOutputHelper)
{
    // A tiny server that echoes the request, so the test can see which backend answered and with which headers.
    private const string WhoamiImage = "docker.io/traefik/whoami:v1.11";

    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public async Task RoutesRequestsByHostToContainer()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");

        var whoami = builder.AddContainer("whoami", WhoamiImage)
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, "whoami.dev.localhost");

        await using var app = builder.Build();
        await app.StartAsync(cts.Token);

        await app.ResourceNotifications.WaitForResourceHealthyAsync(traefik.Resource.Name, cts.Token);
        await app.ResourceNotifications.WaitForResourceAsync(whoami.Resource.Name, KnownResourceStates.Running, cts.Token);

        using var httpClient = app.CreateHttpClient(traefik.Resource.Name, "web");

        // The route matches on the Host header, so send it explicitly rather than relying on *.localhost
        // resolving to loopback, which not every resolver does.
        using var routed = await SendWithRetryAsync(httpClient, "whoami.dev.localhost", cts.Token);
        Assert.Equal(HttpStatusCode.OK, routed.StatusCode);
        var body = await routed.Content.ReadAsStringAsync(cts.Token);
        Assert.Contains("Host: whoami.dev.localhost", body);

        using var unrouted = new HttpRequestMessage(HttpMethod.Get, "/");
        unrouted.Headers.Host = "other.dev.localhost";
        using var notFound = await httpClient.SendAsync(unrouted, cts.Token);
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);

        await app.StopAsync(cts.Token);
    }

    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public async Task AppliesMiddlewaresToRoutes()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");

        var headers = traefik.AddMiddleware("tag", "headers")
            .WithOption("customRequestHeaders.X-Aspire-Middleware", "applied");
        var strip = traefik.AddMiddleware("strip", "stripPrefix")
            .WithOption("prefixes", ["/v1"]);

        var whoami = builder.AddContainer("whoami", WhoamiImage)
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"whoami.dev.localhost");
                route.PathPrefix = "/v1";
                route.Middlewares.Add(strip.Resource);
            })
            .WithTraefikMiddleware(headers);

        await using var app = builder.Build();
        await app.StartAsync(cts.Token);

        await app.ResourceNotifications.WaitForResourceHealthyAsync(traefik.Resource.Name, cts.Token);
        await app.ResourceNotifications.WaitForResourceAsync(whoami.Resource.Name, KnownResourceStates.Running, cts.Token);

        using var httpClient = app.CreateHttpClient(traefik.Resource.Name, "web");

        using var response = await SendWithRetryAsync(httpClient, "whoami.dev.localhost", cts.Token, "/v1/test");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // whoami prints the request line and headers it received: the prefix is gone and the header was added.
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        Assert.Contains("GET /test HTTP/1.1", body);
        Assert.Contains("X-Aspire-Middleware: applied", body);

        await app.StopAsync(cts.Token);
    }

    private static async Task<HttpResponseMessage> SendWithRetryAsync(HttpClient httpClient, string host, CancellationToken cancellationToken, string path = "/")
    {
        // Traefik reports healthy before the file provider has necessarily loaded the dynamic configuration, so
        // the first requests can still get 404.
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Host = host;

            var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                return response;
            }

            response.Dispose();
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }
}
