// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

// All public host names hang off one parameter, so the same AppHost publishes with a ${DOMAIN} placeholder.
// Browsers resolve *.localhost to loopback, so dev.localhost needs no hosts-file entries.
var domain = builder.AddParameter("domain");

builder.AddDockerComposeEnvironment("compose");

var traefik = builder.AddTraefik("traefik")
    .WithDashboard();

if (builder.ExecutionContext.IsRunMode)
{
    // Pin the host ports so the URLs in the README stay stable. Nothing below depends on them: public URLs come
    // from GetTraefikUrl(), which includes whatever port is allocated.
    traefik
        .WithHostPort(builder.Configuration.GetValue<int?>("Traefik:WebPort"))
        .WithHostHttpsPort(builder.Configuration.GetValue<int?>("Traefik:WebSecurePort"));
}

if (builder.Configuration.GetValue<bool>("Traefik:EnableHttps"))
{
    // In run mode this serves HTTPS with Traefik's self-signed certificate; Let's Encrypt is only used when published.
    traefik
        .WithLetsEncrypt(builder.AddParameter("acme-email"))
        .WithHttpsRedirect();
}

// A container backend, reached by its name on the container network.
builder.AddContainer("whoami", "traefik/whoami", "v1.11")
    .WithHttpEndpoint(targetPort: 80)
    .WithTraefikRoute(traefik, ReferenceExpression.Create($"whoami.{domain}"));

// A project backend, running on the host and reached from the Traefik container.
var api = builder.AddProject<Projects.Traefik_Api>("api")
    .WithTraefikRoute(traefik, ReferenceExpression.Create($"api.{domain}"))
    .WithTraefikRoute(traefik, route =>
    {
        // A second, path-based route to the same project: app.<domain>/v1/... (the prefix is forwarded as is).
        route.Host = ReferenceExpression.Create($"app.{domain}");
        route.PathPrefix = "/v1";
        route.RouterName = "api-v1";
    });

// The API reports its own public URL, as an OAuth redirect URI or a link in an email would need.
api.WithEnvironment("PUBLIC_URL", api.GetTraefikUrl());

builder.Build().Run();
