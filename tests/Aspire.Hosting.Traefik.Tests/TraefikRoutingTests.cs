// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Traefik.Tests;

public class TraefikRoutingTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task RunModeDynamicConfigurationRoutesToContainersAndHostProcesses()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var domain = builder.AddParameter("domain", "dev.localhost");
        var traefik = builder.AddTraefik("traefik");

        var whoami = builder.AddContainer("whoami", "traefik/whoami")
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, "whoami.dev.localhost");

        var api = builder.AddExecutable("api", "dotnet", ".")
            .WithHttpEndpoint(port: 5123)
            .WithTraefikRoute(traefik, ReferenceExpression.Create($"api.{domain}"))
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"app.{domain}");
                route.PathPrefix = "/api";
                route.Priority = 100;
                route.RouterName = "app-api";
            });

        using var app = builder.Build();

        EndpointAllocation.Allocate(whoami.Resource, "http", hostPort: 50001, containerHost: "whoami", containerPort: 80);
        EndpointAllocation.Allocate(api.Resource, "http", hostPort: 5123, containerHost: "host.docker.internal", containerPort: 5123);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        TraefikRoutingSubscriber.ApplyRoutes(model, isPublishMode: false);

        var executionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();
        var yaml = await TraefikDynamicConfiguration.GenerateAsync(traefik.Resource, executionContext, CancellationToken.None);

        await Verify(yaml, "yaml");
    }

    [Fact]
    public async Task RunModeDynamicConfigurationUsesWebSecureWhenTlsIsConfigured()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik")
            .WithLetsEncrypt("admin@example.com");

        var whoami = builder.AddContainer("whoami", "traefik/whoami")
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, "whoami.dev.localhost");

        using var app = builder.Build();

        EndpointAllocation.Allocate(whoami.Resource, "http", hostPort: 50001, containerHost: "whoami", containerPort: 80);

        TraefikRoutingSubscriber.ApplyRoutes(app.Services.GetRequiredService<DistributedApplicationModel>(), isPublishMode: false);

        var executionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();
        var yaml = await TraefikDynamicConfiguration.GenerateAsync(traefik.Resource, executionContext, CancellationToken.None);

        await Verify(yaml, "yaml");
    }

    [Fact]
    public async Task RunModeEnvironmentReferencesEveryBackend()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");

        var whoami = builder.AddContainer("whoami", "traefik/whoami")
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, "whoami.dev.localhost");

        var api = builder.AddExecutable("api", "dotnet", ".")
            .WithHttpEndpoint(port: 5123)
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"api.dev.localhost");
                route.RouterName = "api-v1";
            });

        using var app = builder.Build();

        EndpointAllocation.Allocate(whoami.Resource, "http", hostPort: 50001, containerHost: "whoami", containerPort: 80);
        EndpointAllocation.Allocate(api.Resource, "http", hostPort: 5123, containerHost: "host.docker.internal", containerPort: 5123);

        TraefikRoutingSubscriber.ApplyRoutes(app.Services.GetRequiredService<DistributedApplicationModel>(), isPublishMode: false);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(traefik.Resource, DistributedApplicationOperation.Run, app.Services);

        Assert.Equal("http://whoami:80", env["ASPIRE_TRAEFIK_BACKEND_WHOAMI"]);
        Assert.Equal("http://host.docker.internal:5123", env["ASPIRE_TRAEFIK_BACKEND_API_V1"]);
    }

    [Fact]
    public async Task GetTraefikUrlIncludesHostPortInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");

        var api = builder.AddContainer("api", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"app.dev.localhost");
                route.PathPrefix = "/api";
            });

        EndpointAllocation.Allocate(traefik.Resource, "web", hostPort: 8000, containerHost: "traefik", containerPort: 80);

        var url = await api.GetTraefikUrl().GetValueAsync(CancellationToken.None);

        Assert.Equal("http://app.dev.localhost:8000/api", url);
    }

    [Fact]
    public async Task GetTraefikUrlUsesHttpsWithLetsEncryptInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik")
            .WithLetsEncrypt("admin@example.com");

        var api = builder.AddContainer("api", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, "api.dev.localhost");

        EndpointAllocation.Allocate(traefik.Resource, "websecure", hostPort: 8443, containerHost: "traefik", containerPort: 443);

        var url = await api.GetTraefikUrl().GetValueAsync(CancellationToken.None);

        Assert.Equal("https://api.dev.localhost:8443", url);
    }

    [Theory]
    [InlineData(false, null, "https://api.example.com")]
    [InlineData(true, "web", "http://api.example.com")]
    [InlineData(true, "websecure", "https://api.example.com")]
    public async Task GetTraefikUrlAtPublishTime(bool external, string? externalEntryPoint, string expected)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var traefik = builder.AddTraefik("traefik");
        if (external)
        {
            traefik.PublishAsExternal("traefik-public", externalEntryPoint!);
        }
        else
        {
            traefik.WithLetsEncrypt("admin@example.com");
        }

        var api = builder.AddContainer("api", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, "api.example.com");

        var url = await api.GetTraefikUrl().GetValueAsync(CancellationToken.None);

        Assert.Equal(expected, url);
    }

    [Fact]
    public async Task GetTraefikUrlAssumesHttpsForParameterizedExternalEntryPoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var entryPoint = builder.AddParameter("traefik-entrypoint");
        var traefik = builder.AddTraefik("traefik")
            .PublishAsExternal("traefik-public", ReferenceExpression.Create($"{entryPoint}"));

        var api = builder.AddContainer("api", "image")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, "api.example.com");

        var url = await api.GetTraefikUrl().GetValueAsync(CancellationToken.None);

        Assert.Equal("https://api.example.com", url);
    }

    [Fact]
    public void GetTraefikUrlWithoutRouteThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var api = builder.AddContainer("api", "image")
            .WithHttpEndpoint(targetPort: 8080);

        var exception = Assert.Throws<InvalidOperationException>(() => api.GetTraefikUrl());
        Assert.Equal("Resource 'api' has no Traefik route. Call WithTraefikRoute first.", exception.Message);
    }

    [Fact]
    public void DuplicateRouterNamesThrow()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");

        builder.AddContainer("a", "image")
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"a.dev.localhost");
                route.RouterName = "shared";
            });

        builder.AddContainer("b", "image")
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"b.dev.localhost");
                route.RouterName = "Shared";
            });

        using var app = builder.Build();

        var exception = Assert.Throws<InvalidOperationException>(() => TraefikRoutingSubscriber.ApplyRoutes(app.Services.GetRequiredService<DistributedApplicationModel>(), isPublishMode: false));
        Assert.Equal("The Traefik router name 'Shared' is used by both 'a' and 'b'. Set TraefikRouteOptions.RouterName to make it unique.", exception.Message);
    }

    [Fact]
    public void SecondRouteOfResourceGetsNumberedRouterName()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");

        builder.AddContainer("api", "image")
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, "api.dev.localhost")
            .WithTraefikRoute(traefik, "api2.dev.localhost");

        using var app = builder.Build();

        TraefikRoutingSubscriber.ApplyRoutes(app.Services.GetRequiredService<DistributedApplicationModel>(), isPublishMode: false);

        Assert.Collection(traefik.Resource.Routes,
            route => Assert.Equal("api", route.RouterName),
            route => Assert.Equal("api-2", route.RouterName));
    }

    [Fact]
    public void RouteToResourceWithSeveralUnnamedHttpEndpointsThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");

        builder.AddContainer("api", "image")
            .WithHttpEndpoint(name: "public", targetPort: 80)
            .WithHttpEndpoint(name: "admin", targetPort: 81)
            .WithTraefikRoute(traefik, "api.dev.localhost");

        using var app = builder.Build();

        var exception = Assert.Throws<InvalidOperationException>(() => TraefikRoutingSubscriber.ApplyRoutes(app.Services.GetRequiredService<DistributedApplicationModel>(), isPublishMode: false));
        Assert.Equal("Resource 'api' has several HTTP endpoints and none is named 'http'. Set TraefikRouteOptions.EndpointName to choose one.", exception.Message);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "api")]
    [InlineData("api.dev.localhost", "/a`b")]
    public void InvalidRouteOptionsThrow(string? host, string? pathPrefix)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");
        var api = builder.AddContainer("api", "image").WithHttpEndpoint(targetPort: 80);

        var exception = Assert.Throws<ArgumentException>(() => api.WithTraefikRoute(traefik, route =>
        {
            route.Host = host is null ? null : ReferenceExpression.Create($"{host}");
            route.PathPrefix = pathPrefix;
        }));
        Assert.Equal("configure", exception.ParamName);
    }
}
