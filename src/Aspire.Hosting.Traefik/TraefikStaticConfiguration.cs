// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// Renders Traefik's static (install) configuration as command-line arguments.
/// </summary>
/// <remarks>
/// <para>
/// Arguments rather than a static configuration file because arguments accept <see cref="ReferenceExpression"/>
/// values (parameters, endpoint ports) that resolve at run time and become placeholders at publish time, and
/// because they show up in the dashboard's resource details.
/// </para>
/// <para>
/// Traefik reads static configuration from exactly one source (file, arguments, or <c>TRAEFIK_*</c> environment
/// variables), so everything has to be expressed as arguments once any argument is used. Output looks like:
/// <code>
/// --entrypoints.web.address=:80
/// --entrypoints.traefik.address=:8080
/// --ping=true
/// --providers.file.directory=/etc/traefik/dynamic
/// </code>
/// See <see href="https://doc.traefik.io/traefik/reference/install-configuration/configuration-options/"/>.
/// </para>
/// </remarks>
internal static class TraefikStaticConfiguration
{
    // The OTLP API key setting the dashboard and Aspire's own OTLP exporter wiring use.
    private const string OtlpApiKeyConfigName = "AppHost:OtlpApiKey";

    private const string LetsEncryptStagingDirectory = "https://acme-staging-v02.api.letsencrypt.org/directory";

    public static void AddArguments(TraefikResource traefik, CommandLineArgsCallbackContext context)
    {
        var isPublishMode = context.ExecutionContext.IsPublishMode;
        var args = context.Args;

        AddEntryPoints(traefik, args, isPublishMode);

        // `/ping` backs the resource health check in run mode and is cheap to keep at publish time, where it
        // serves `traefik healthcheck` and external monitoring.
        args.Add("--ping=true");

        AddProviders(args, isPublishMode);

        if (traefik.DashboardEnabled)
        {
            args.Add("--api.dashboard=true");

            if (!isPublishMode)
            {
                // `api.insecure` serves the dashboard on the `traefik` entry point without a router or
                // authentication. That is only acceptable on a developer machine; at publish time the dashboard
                // stays unreachable until a secured router to `api@internal` is configured.
                args.Add("--api.insecure=true");
            }
        }

        // Checked in both modes so a misconfiguration shows up on `aspire run`, not only on the first deployment.
        if (traefik.AcmeCaServer is not null)
        {
            if (traefik.AcmeResolver is not { } resolver)
            {
                throw new InvalidOperationException($"Traefik resource '{traefik.Name}' sets an ACME CA server, but no certificate resolver is configured. Call WithLetsEncrypt.");
            }

            if (resolver.Staging)
            {
                throw new InvalidOperationException($"Traefik resource '{traefik.Name}' sets both an ACME CA server and staging: true. Use one of them.");
            }
        }

        if (isPublishMode && traefik.AcmeResolver is { } acme)
        {
            AddAcmeResolver(acme, traefik.AcmeCaServer, args);
        }

        if (!isPublishMode)
        {
            AddOtlpExporter(traefik, context);
        }
        else if (traefik.PublishOtlpEndpoint is { } publishOtlpEndpoint)
        {
            // The compose dashboard accepts OTLP without an API key on its internal network.
            AddOtlpArguments(traefik, args, publishOtlpEndpoint, apiKey: null);
        }
    }

    private static void AddEntryPoints(TraefikResource traefik, IList<object> args, bool isPublishMode)
    {
        args.Add(FormattableString.Invariant($"--entrypoints.{TraefikResource.WebEntryPointName}.address=:{TraefikResource.WebTargetPort}"));

        if (traefik.HasWebSecureEntryPoint)
        {
            args.Add(FormattableString.Invariant($"--entrypoints.{TraefikResource.WebSecureEntryPointName}.address=:{TraefikResource.WebSecureTargetPort}"));
        }

        args.Add(FormattableString.Invariant($"--entrypoints.{TraefikResource.TraefikEntryPointName}.address=:{TraefikResource.TraefikTargetPort}"));

        foreach (var entryPoint in traefik.CustomEntryPoints)
        {
            args.Add(FormattableString.Invariant($"--entrypoints.{entryPoint.Name}.address=:{entryPoint.TargetPort}"));
        }

        if (traefik.HttpsRedirectEnabled)
        {
            if (!traefik.HasWebSecureEntryPoint)
            {
                throw new InvalidOperationException($"Traefik resource '{traefik.Name}' redirects to HTTPS, but no TLS is configured. Call WithLetsEncrypt before relying on WithHttpsRedirect.");
            }

            // `redirections.entrypoint.to` accepts an entry point name or a port. At publish time the entry point
            // name works because clients reach `websecure` on its own port (443). In run mode the container port is
            // mapped to a random host port, so the redirect has to name the host port the browser can reach.
            // https://doc.traefik.io/traefik/reference/install-configuration/entrypoints/#redirection
            const string redirectTo = $"--entrypoints.{TraefikResource.WebEntryPointName}.http.redirections.entrypoint.to=";
            args.Add(isPublishMode
                ? $"{redirectTo}{TraefikResource.WebSecureEntryPointName}"
                : ReferenceExpression.Create($"{redirectTo}:{GetHostEndpoint(traefik, TraefikResource.WebSecureEntryPointName).Property(EndpointProperty.Port)}"));
            args.Add($"--entrypoints.{TraefikResource.WebEntryPointName}.http.redirections.entrypoint.scheme=https");
        }
    }

    private static void AddProviders(IList<object> args, bool isPublishMode)
    {
        if (isPublishMode)
        {
            // At publish time routes are labels on the routed services, which only the Docker provider reads.
            // `exposedbydefault=false` keeps services without `traefik.enable=true` off the proxy.
            // https://doc.traefik.io/traefik/reference/install-configuration/providers/docker/
            args.Add("--providers.docker=true");
            args.Add("--providers.docker.exposedbydefault=false");
        }
        else
        {
            // In run mode the backends include projects and executables on the host, which have no labels, so the
            // routes are written to a file-provider directory instead (see TraefikDynamicConfiguration).
            // https://doc.traefik.io/traefik/reference/install-configuration/providers/others/file/
            args.Add($"--providers.file.directory={TraefikResource.DynamicConfigurationDirectory}");
        }
    }

    private static void AddAcmeResolver(TraefikAcmeResolver acme, ReferenceExpression? caServer, IList<object> args)
    {
        // https://doc.traefik.io/traefik/reference/install-configuration/tls/certificate-resolvers/acme/
        var prefix = $"--certificatesresolvers.{acme.Name}.acme";

        args.Add(ReferenceExpression.Create($"{prefix}.email={acme.Email}"));

        // Traefik refuses an acme.json readable by group or others. The file is created by Traefik itself inside
        // the volume, so it gets mode 600; a bind-mounted file created by someone else would not.
        args.Add($"{prefix}.storage={TraefikResource.AcmeStorageDirectory}/acme.json");

        switch (acme.Challenge)
        {
            case TraefikAcmeChallenge.TlsAlpn:
                args.Add($"{prefix}.tlschallenge=true");
                break;
            case TraefikAcmeChallenge.Http:
                args.Add($"{prefix}.httpchallenge=true");
                args.Add($"{prefix}.httpchallenge.entrypoint={TraefikResource.WebEntryPointName}");
                break;
        }

        if (caServer is not null)
        {
            args.Add(ReferenceExpression.Create($"{prefix}.caserver={caServer}"));
        }
        else if (acme.Staging)
        {
            args.Add($"{prefix}.caserver={LetsEncryptStagingDirectory}");
        }
    }

    private static void AddOtlpExporter(TraefikResource traefik, CommandLineArgsCallbackContext context)
    {
        // Traefik doesn't read OTEL_EXPORTER_OTLP_ENDPOINT/HEADERS (only OTEL_RESOURCE_ATTRIBUTES, which
        // WithOtlpExporter sets so the dashboard can match the telemetry to this resource), so the dashboard's
        // OTLP endpoint and API key have to be passed as Traefik's own flags.
        // https://doc.traefik.io/traefik/reference/install-configuration/observability/tracing/
        // https://doc.traefik.io/traefik/reference/install-configuration/observability/metrics/
        IServiceProvider services;
        try
        {
            services = context.ExecutionContext.Services;
        }
        catch (InvalidOperationException)
        {
            // The service provider isn't available when arguments are evaluated before the app is built (for
            // example, in tests). Aspire's own OTLP wiring skips the exporter in that case too.
            return;
        }

        if (services.GetService<DistributedApplicationModel>() is not { } model ||
            !model.Resources.TryGetByName(KnownResourceNames.AspireDashboard, out var dashboard) ||
            dashboard is not IResourceWithEndpoints dashboardWithEndpoints)
        {
            // No dashboard (for example, the dashboard is disabled or the app runs in a test host): nothing to
            // send telemetry to.
            return;
        }

        var otlpGrpc = dashboardWithEndpoints.GetEndpoint(KnownEndpointNames.OtlpGrpcEndpointName);
        if (!otlpGrpc.Exists)
        {
            return;
        }

        // The endpoint is resolved from the container's point of view, for example
        // `host.docker.internal:21000`, and the dashboard's development certificate is trusted inside the
        // container through SSL_CERT_DIR (Aspire's default certificate trust for containers, which Go honors).
        var apiKey = services.GetService<IConfiguration>()?[OtlpApiKeyConfigName];
        var args = context.Args;

        AddOtlpArguments(traefik, args, otlpGrpc, apiKey);

        // Match the short export interval Aspire uses for other resources in development, so dashboard metrics
        // update promptly instead of every 10 seconds.
        args.Add("--metrics.otlp.pushinterval=1s");
    }

    private static void AddOtlpArguments(TraefikResource traefik, IList<object> args, EndpointReference otlpGrpc, string? apiKey)
    {
        foreach (var prefix in (string[])["--tracing.otlp", "--metrics.otlp"])
        {
            args.Add($"{prefix}=true");
            args.Add($"{prefix}.grpc=true");
            args.Add(ReferenceExpression.Create($"{prefix}.grpc.endpoint={otlpGrpc.Property(EndpointProperty.HostAndPort)}"));

            if (!otlpGrpc.IsHttps)
            {
                // The run-mode dashboard runs without TLS when ASPIRE_ALLOW_UNSECURED_TRANSPORT is set; the compose
                // dashboard's OTLP endpoint is always plain gRPC on the internal network.
                args.Add($"{prefix}.grpc.insecure=true");
            }

            if (!string.IsNullOrEmpty(apiKey))
            {
                // Traefik only accepts OTLP headers as static configuration, and static configuration can't mix
                // arguments with environment variables, so the development-only API key is visible in the
                // resource's arguments. It is generated per run and only grants writing telemetry locally.
                args.Add($"{prefix}.grpc.headers.x-otlp-api-key={apiKey}");
            }
        }

        args.Add($"--tracing.servicename={traefik.Name}");
        args.Add($"--metrics.otlp.servicename={traefik.Name}");
        args.Add("--metrics.otlp.addrouterslabels=true");
    }

    internal static EndpointReference GetHostEndpoint(TraefikResource traefik, string entryPointName) =>
        // Pin the localhost network so the value is the host port even when the expression is evaluated for a
        // container (Traefik's own arguments, or another container's environment).
        new(traefik, entryPointName, KnownNetworkIdentifiers.LocalhostNetwork);
}
