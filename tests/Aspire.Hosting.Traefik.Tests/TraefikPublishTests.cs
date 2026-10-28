// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Tests;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Traefik.Tests;

public class TraefikPublishTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task PublishWithManagedTraefikWritesLabels()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        // The volume name includes a hash of the AppHost path; pin it so the snapshot is machine-independent.
        builder.Configuration["AppHost:Sha256"] = "0123456789abcdef";

        builder.AddDockerComposeEnvironment("compose");

        var domain = builder.AddParameter("domain");
        var email = builder.AddParameter("acme-email");

        var traefik = builder.AddTraefik("traefik")
            .WithDashboard()
            .WithLetsEncrypt(email)
            .WithHttpsRedirect();

        builder.AddContainer("whoami", "traefik/whoami")
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, ReferenceExpression.Create($"whoami.{domain}"));

        // No target port: the Docker Compose publisher assigns the container port through a `<NAME>_PORT` variable.
        builder.AddContainer("api", "myregistry/api")
            .WithHttpEndpoint(env: "PORT")
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"app.{domain}");
                route.PathPrefix = "/api";
                route.Priority = 100;
            });

        using var app = builder.Build();
        app.Run();

        await VerifyComposeOutput(workspace.Path);
    }

    [Fact]
    public async Task PublishWithParameterizedAcmeSettings()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.Configuration["AppHost:Sha256"] = "0123456789abcdef";

        builder.AddDockerComposeEnvironment("compose")
            .WithDashboard(enabled: false);

        var domain = builder.AddParameter("domain");
        var caServer = builder.AddParameter("acme-ca-server", "https://acme-v02.api.letsencrypt.org/directory");

        var traefik = builder.AddTraefik("traefik")
            .WithLetsEncrypt(ReferenceExpression.Create($"admin@{domain}"))
            .WithAcmeCaServer(caServer);

        builder.AddContainer("whoami", "traefik/whoami")
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, ReferenceExpression.Create($"whoami.{domain}"));

        using var app = builder.Build();
        app.Run();

        await VerifyComposeOutput(workspace.Path);
    }

    [Fact]
    public async Task PublishWithParameterizedExternalTraefik()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("compose")
            .WithDashboard(enabled: false);

        var entryPoint = builder.AddParameter("traefik-entrypoint");
        var certResolver = builder.AddParameter("traefik-certresolver");

        var traefik = builder.AddTraefik("traefik")
            .PublishAsExternal("traefik-public", ReferenceExpression.Create($"{entryPoint}"), ReferenceExpression.Create($"{certResolver}"));

        builder.AddContainer("whoami", "traefik/whoami")
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, "whoami.example.com");

        using var app = builder.Build();
        app.Run();

        await VerifyComposeOutput(workspace.Path);
    }

    [Fact]
    public async Task PublishWithoutComposeDashboardOmitsTelemetry()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("compose")
            .WithDashboard(enabled: false);

        var traefik = builder.AddTraefik("traefik");

        builder.AddContainer("whoami", "traefik/whoami")
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, "whoami.example.com");

        using var app = builder.Build();
        app.Run();

        await VerifyComposeOutput(workspace.Path);
    }

    [Fact]
    public async Task PublishWithExternalTraefikOmitsTraefikAndJoinsNetwork()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("compose");

        var domain = builder.AddParameter("domain");

        var traefik = builder.AddTraefik("traefik")
            .WithDashboard()
            .PublishAsExternal(network: "traefik-public", entryPoint: "websecure", certResolver: "letsencrypt");

        builder.AddContainer("whoami", "traefik/whoami")
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, ReferenceExpression.Create($"whoami.{domain}"));

        using var app = builder.Build();
        app.Run();

        await VerifyComposeOutput(workspace.Path);
    }

    [Fact]
    public async Task PublishWritesMiddlewareLabelsOnServicesThatUseThem()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        builder.AddDockerComposeEnvironment("compose")
            .WithDashboard(enabled: false);

        var origin = builder.AddParameter("cors-origin");

        var traefik = builder.AddTraefik("traefik");

        var headers = traefik.AddMiddleware("sec", "headers")
            .WithOption("frameDeny", true)
            .WithOption("stsSeconds", 31536000)
            .WithOption("accessControlAllowOriginList", [ReferenceExpression.Create($"{origin}")]);
        // `$1` is a regex group reference, which must reach Traefik as is rather than be interpolated by Compose.
        var rewrite = traefik.AddMiddleware("rewrite", "replacePathRegex")
            .WithOption("regex", "^/v1/(.*)")
            .WithOption("replacement", "/$1");
        var compress = traefik.AddMiddleware("compress", "compress");

        builder.AddContainer("whoami", "traefik/whoami")
            .WithHttpEndpoint(targetPort: 80)
            .WithTraefikRoute(traefik, "whoami.example.com")
            .WithTraefikMiddleware(headers);

        builder.AddContainer("api", "myregistry/api")
            .WithHttpEndpoint(targetPort: 8080)
            .WithTraefikRoute(traefik, route =>
            {
                route.Host = ReferenceExpression.Create($"app.example.com");
                route.PathPrefix = "/v1";
                route.Middlewares.Add(rewrite.Resource);
            })
            .WithTraefikMiddleware(headers, compress);

        using var app = builder.Build();
        app.Run();

        await VerifyComposeOutput(workspace.Path);
    }

    private static async Task VerifyComposeOutput(string outputPath)
    {
        var composePath = Path.Combine(outputPath, "docker-compose.yaml");
        var envPath = Path.Combine(outputPath, ".env");

        Assert.True(File.Exists(composePath));

        var verify = Verify(File.ReadAllText(composePath), "yaml");

        // The publisher only writes .env when the model has parameters.
        if (File.Exists(envPath))
        {
            verify = verify.AppendContentAsFile(File.ReadAllText(envPath), "env");
        }

        await verify;
    }
}
