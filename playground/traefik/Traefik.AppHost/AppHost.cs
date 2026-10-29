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

// Middlewares shared by several routes: security headers and compression, applied as one chain.
var defaults = traefik.AddChain("defaults",
    traefik.AddSecurityHeaders("security-headers"),
    traefik.AddCompress("compress"));

// Basic authentication. A plain password can only be hashed in run mode; a published app takes an htpasswd entry
// (`htpasswd -nbB admin <password>`), which also keeps the password out of the deployment.
var adminAuth = builder.ExecutionContext.IsPublishMode
    ? traefik.AddBasicAuth("admin-auth", builder.AddParameter("admin-htpasswd", secret: true))
    : traefik.AddBasicAuth("admin-auth", "admin", builder.AddParameter("admin-password", secret: true));

// The dashboard on traefik.<domain>, behind the same basic authentication. In run mode it is also served without
// authentication on the traefik endpoint.
traefik.WithDashboard(ReferenceExpression.Create($"traefik.{domain}"), adminAuth);

// An entry-point middleware applies to every router on the entry point, including the dashboard and sign-in routes.
var enableHttps = builder.Configuration.GetValue<bool>("Traefik:EnableHttps");
traefik.WithEntryPointMiddleware(enableHttps ? "websecure" : "web",
    traefik.AddHeaders("served-by", headers => headers.CustomResponseHeaders["X-Served-By"] = "aspire-traefik"));

// A container backend, reached by its name on the container network. admin.<domain> is the same backend behind
// basic authentication.
builder.AddContainer("whoami", "traefik/whoami", "v1.11")
    .WithHttpEndpoint(targetPort: 80)
    .WithTraefikRoute(traefik, ReferenceExpression.Create($"whoami.{domain}"))
    .WithTraefikRoute(traefik, ReferenceExpression.Create($"admin.{domain}"), adminAuth)
    .WithTraefikMiddleware(defaults);

// Single sign-on: Keycloak behind Traefik at auth.<domain>, and secure.<domain> only for users signed in to its realm.
// AddKeycloakForwardAuth runs oauth2-proxy between them and sets Keycloak's hostname to its public URL.
var clientSecret = builder.AddParameter("sso-client-secret", secret: true);

#pragma warning disable ASPIRECERTIFICATES001 // WithoutHttpsCertificate is experimental.
var keycloak = builder.AddKeycloak("keycloak")
    // Plain HTTP inside the application: Traefik is the only entry point.
    .WithoutHttpsCertificate()
    .WithRealmImport("./Realms")
    .WithTraefikRoute(traefik, ReferenceExpression.Create($"auth.{domain}"));
#pragma warning restore ASPIRECERTIFICATES001

var sso = traefik.AddKeycloakForwardAuth("sso", keycloak, realm: "traefik-demo", clientId: "oauth2-proxy", clientSecret);

var secure = builder.AddContainer("secure", "traefik/whoami", "v1.11")
    .WithHttpEndpoint(targetPort: 80)
    .WithTraefikRoute(traefik, ReferenceExpression.Create($"secure.{domain}"))
    .WithTraefikMiddleware(sso);

// Placeholders resolved by Keycloak while importing Realms/traefik-demo-realm.json.
keycloak
    .WithEnvironment("TRAEFIK_DEMO_CLIENT_SECRET", clientSecret)
    .WithEnvironment("TRAEFIK_DEMO_REDIRECT_URI", ReferenceExpression.Create($"{secure.GetTraefikUrl()}/oauth2/callback"));

// A project backend, running on the host and reached from the Traefik container.
var api = builder.AddProject<Projects.Traefik_Api>("api")
    .WithTraefikRoute(traefik, ReferenceExpression.Create($"api.{domain}"))
    .WithTraefikRoute(traefik, route =>
    {
        // A second, path-based route to the same project: app.<domain>/v1/... (the prefix is forwarded as is).
        route.Host = ReferenceExpression.Create($"app.{domain}");
        route.PathPrefix = "/v1";
        route.RouterName = "api-v1";
    })
    .WithTraefikRoute(traefik, route =>
    {
        // app.<domain>/api/... reaches the API without the prefix; Traefik passes it in X-Forwarded-Prefix.
        route.Host = ReferenceExpression.Create($"app.{domain}");
        route.PathPrefix = "/api";
        route.StripPathPrefix = true;
        route.RouterName = "api-stripped";
    })
    .WithTraefikMiddleware(traefik.AddRateLimit("api-rate-limit", average: 20, burst: 40));

// The API reports its own public URL, as an OAuth redirect URI or a link in an email would need.
api.WithEnvironment("PUBLIC_URL", api.GetTraefikUrl());

builder.Build().Run();
