// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Traefik.Tests;

public class AddTraefikTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void AddTraefikAddsContainerWithEntryPoints()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        builder.AddTraefik("traefik");

        var resource = Assert.Single(builder.Resources.OfType<TraefikResource>());

        Assert.True(resource.TryGetLastAnnotation<ContainerImageAnnotation>(out var image));
        Assert.Equal(TraefikContainerImageTags.Registry, image.Registry);
        Assert.Equal(TraefikContainerImageTags.Image, image.Image);
        Assert.Equal(TraefikContainerImageTags.Tag, image.Tag);

        Assert.Collection(resource.Annotations.OfType<EndpointAnnotation>(),
            endpoint =>
            {
                Assert.Equal("web", endpoint.Name);
                Assert.Equal("http", endpoint.UriScheme);
                Assert.Equal(80, endpoint.TargetPort);
                Assert.Null(endpoint.Port);
                Assert.True(endpoint.IsExternal);
            },
            endpoint =>
            {
                Assert.Equal("traefik", endpoint.Name);
                Assert.Equal("http", endpoint.UriScheme);
                Assert.Equal(8080, endpoint.TargetPort);
                Assert.False(endpoint.IsExternal);
            });

        var healthCheck = Assert.Single(resource.Annotations.OfType<HealthCheckAnnotation>());
        Assert.Equal("traefik_traefik_/ping_200_check", healthCheck.Key);
    }

    [Fact]
    public void AddTraefikUsesStandardPortsAtPublishTime()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddTraefik("traefik")
            .WithLetsEncrypt("admin@example.com");

        var resource = Assert.Single(builder.Resources.OfType<TraefikResource>());
        var endpoints = resource.Annotations.OfType<EndpointAnnotation>().ToDictionary(e => e.Name);

        Assert.Equal(80, endpoints["web"].Port);
        Assert.Equal(443, endpoints["websecure"].Port);
        Assert.Equal("https", endpoints["websecure"].UriScheme);

        var mount = Assert.Single(resource.Annotations.OfType<ContainerMountAnnotation>(), m => m.Target == "/var/run/docker.sock");
        Assert.True(mount.IsReadOnly);
    }

    [Fact]
    public void WithHostPortSetsWebAndWebSecurePorts()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        // The HTTPS port is set before the websecure entry point exists, which must still apply.
        builder.AddTraefik("traefik")
            .WithHostPort(8000)
            .WithHostHttpsPort(8443)
            .WithLetsEncrypt("admin@example.com");

        var resource = Assert.Single(builder.Resources.OfType<TraefikResource>());
        var endpoints = resource.Annotations.OfType<EndpointAnnotation>().ToDictionary(e => e.Name);

        Assert.Equal(8000, endpoints["web"].Port);
        Assert.Equal(8443, endpoints["websecure"].Port);
    }

    [Fact]
    public async Task RunModeArguments()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik")
            .WithDashboard()
            .WithLetsEncrypt("admin@example.com")
            .WithHttpsRedirect()
            .WithEntryPoint("amqps", targetPort: 5671);

        EndpointAllocation.Allocate(traefik.Resource, "websecure", hostPort: 8443, containerHost: "traefik", containerPort: 443);

        var args = await ArgumentEvaluator.GetArgumentListAsync(traefik.Resource);

        await Verify(args);
    }

    [Fact]
    public async Task RunModeArgumentsSendTelemetryToDashboard()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.Configuration["AppHost:OtlpApiKey"] = "test-api-key";

        // Stand in for the dashboard resource that `aspire run` adds.
        var dashboard = builder.AddResource(new ContainerResource("aspire-dashboard"))
            .WithEndpoint(name: "otlp-grpc", scheme: "https", targetPort: 18889);
        EndpointAllocation.Allocate(dashboard.Resource, "otlp-grpc", hostPort: 18889, containerHost: "host.docker.internal", containerPort: 18889);

        var traefik = builder.AddTraefik("traefik");

        using var app = builder.Build();

        var args = await ArgumentEvaluator.GetArgumentListAsync(traefik.Resource, app.Services);

        await Verify(args);
    }

    [Fact]
    public async Task HttpsRedirectWithoutTlsThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik")
            .WithHttpsRedirect();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ArgumentEvaluator.GetArgumentListAsync(traefik.Resource));
        Assert.Equal("Traefik resource 'traefik' redirects to HTTPS, but no TLS is configured. Call WithLetsEncrypt before relying on WithHttpsRedirect.", exception.Message);
    }

    [Fact]
    public async Task AcmeCaServerWithoutLetsEncryptThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik")
            .WithAcmeCaServer("https://acme.example.com/directory");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ArgumentEvaluator.GetArgumentListAsync(traefik.Resource));
        Assert.Equal("Traefik resource 'traefik' sets an ACME CA server, but no certificate resolver is configured. Call WithLetsEncrypt.", exception.Message);
    }

    [Fact]
    public async Task AcmeCaServerWithStagingThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik")
            .WithLetsEncrypt("admin@example.com", staging: true)
            .WithAcmeCaServer("https://acme.example.com/directory");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await ArgumentEvaluator.GetArgumentListAsync(traefik.Resource));
        Assert.Equal("Traefik resource 'traefik' sets both an ACME CA server and staging: true. Use one of them.", exception.Message);
    }

    [Theory]
    [InlineData("acme.example.com/directory")]
    [InlineData("ftp://acme.example.com/directory")]
    public void WithAcmeCaServerRejectsInvalidUrls(string caServer)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");

        var exception = Assert.Throws<ArgumentException>(() => traefik.WithAcmeCaServer(caServer));
        Assert.Equal("caServer", exception.ParamName);
    }

    [Theory]
    [InlineData("web")]
    [InlineData("websecure")]
    [InlineData("traefik")]
    [InlineData("bad.name")]
    public void WithEntryPointRejectsReservedAndInvalidNames(string name)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik");

        var exception = Assert.Throws<ArgumentException>(() => traefik.WithEntryPoint(name, targetPort: 9000));
        Assert.Equal("name", exception.ParamName);
    }

    [Fact]
    public void WithDashboardAddsDashboardUrl()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var traefik = builder.AddTraefik("traefik")
            .WithDashboard();

        Assert.True(traefik.Resource.DashboardEnabled);
        Assert.Single(traefik.Resource.Annotations.OfType<ResourceUrlsCallbackAnnotation>());
    }
}
