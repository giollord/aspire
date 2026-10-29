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

        // Traefik checks for new versions over the internet on every start unless told not to, and anonymous usage
        // statistics are off by default but stay off explicitly. An application's proxy shouldn't call home; the
        // image version is pinned by the AppHost anyway.
        // https://doc.traefik.io/traefik/contributing/data-collection/
        args.Add("--global.checknewversion=false");
        args.Add("--global.sendanonymoususage=false");

        AddLogs(traefik, args);

        // `/ping` backs the resource health check in run mode and is cheap to keep at publish time, where it
        // serves `traefik healthcheck` and external monitoring.
        args.Add("--ping=true");

        AddProviders(args, isPublishMode, traefik.HasPublishFileConfiguration);

        if (isPublishMode && traefik.DockerSocketProxy is { } socketProxy)
        {
            // The proxy forwards only the read-only container endpoints the Docker provider needs, instead of the whole
            // Docker API. https://doc.traefik.io/traefik/reference/install-configuration/providers/docker/#endpoint
            args.Add(ReferenceExpression.Create($"--providers.docker.endpoint=tcp://{socketProxy.GetEndpoint(TraefikBuilderExtensions.SocketProxyEndpointName).Property(EndpointProperty.HostAndPort)}"));
        }

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
            if (traefik.AcmeResolvers.Count == 0)
            {
                throw new InvalidOperationException($"Traefik resource '{traefik.Name}' sets an ACME CA server, but no certificate resolver is configured. Call WithLetsEncrypt.");
            }

            if (traefik.AcmeResolvers.FirstOrDefault(r => r.Staging) is { } staging)
            {
                throw new InvalidOperationException($"Traefik resource '{traefik.Name}' sets an ACME CA server, and its resolver '{staging.Name}' sets staging: true. Use one of them.");
            }
        }

        ValidateTlsDomains(traefik);

        if (isPublishMode)
        {
            foreach (var acme in traefik.AcmeResolvers)
            {
                AddAcmeResolver(acme, traefik.AcmeCaServer, args);
            }
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
            // A UDP entry point is written `:5353/udp`; without a suffix it listens for TCP.
            // https://doc.traefik.io/traefik/reference/install-configuration/entrypoints/#address
            args.Add(FormattableString.Invariant($"--entrypoints.{entryPoint.Name}.address=:{entryPoint.TargetPort}{(entryPoint.IsUdp ? "/udp" : "")}"));
        }

        if (traefik.HasWebSecureEntryPoint)
        {
            // TLS by default for every router on `websecure`, including routers that don't come from Aspire routes,
            // such as one added by hand to a provider. Aspire's own routes still set TLS on the router as well. As for
            // the routes, run mode uses the default certificate and publish time the ACME resolver.
            // https://doc.traefik.io/traefik/reference/install-configuration/entrypoints/#http-tls
            args.Add(isPublishMode
                ? $"--entrypoints.{TraefikResource.WebSecureEntryPointName}.http.tls.certresolver={traefik.DefaultAcmeResolver!.Name}"
                : $"--entrypoints.{TraefikResource.WebSecureEntryPointName}.http.tls=true");

            if (isPublishMode)
            {
                AddTlsDomains(traefik, args);
            }
        }

        AddEntryPointMiddlewares(traefik, args);

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

    private static void AddLogs(TraefikResource traefik, IList<object> args)
    {
        // Both go to stdout, which Aspire shows as the resource's console logs.
        // https://doc.traefik.io/traefik/reference/install-configuration/observability/logs-and-accesslogs/
        if (traefik.LogLevel is { } level)
        {
            args.Add($"--log.level={level.ToString().ToUpperInvariant()}");
        }

        if (traefik.AccessLogs is not { } accessLogs)
        {
            return;
        }

        args.Add("--accesslog=true");

        if (accessLogs.Format is TraefikAccessLogFormat.Json)
        {
            args.Add("--accesslog.format=json");
        }

        if (accessLogs.StatusCodes.Count > 0)
        {
            args.Add($"--accesslog.filters.statuscodes={string.Join(",", accessLogs.StatusCodes)}");
        }

        if (accessLogs.RetryAttempts)
        {
            args.Add("--accesslog.filters.retryattempts=true");
        }

        if (accessLogs.MinDuration is { } minDuration)
        {
            args.Add($"--accesslog.filters.minduration={TraefikNames.ToDuration(minDuration, "configure")}");
        }

        foreach (var header in accessLogs.KeepHeaders)
        {
            args.Add($"--accesslog.fields.headers.names.{header}=keep");
        }
    }

    private static void AddEntryPointMiddlewares(TraefikResource traefik, IList<object> args)
    {
        foreach (var (entryPoint, middlewares) in traefik.EntryPointMiddlewares)
        {
            var exists = entryPoint switch
            {
                TraefikResource.WebEntryPointName => true,
                TraefikResource.WebSecureEntryPointName => traefik.HasWebSecureEntryPoint,
                _ => traefik.CustomEntryPoints.Any(e => e.Name == entryPoint),
            };

            if (!exists)
            {
                throw new InvalidOperationException($"Traefik resource '{traefik.Name}' has middlewares for the entry point '{entryPoint}', which doesn't exist. Add it with WithEntryPoint, or WithLetsEncrypt for 'websecure'.");
            }

            // Static configuration references dynamic configuration, so the middlewares need their provider suffix.
            // They are defined in the file provider in both modes (see TraefikDynamicConfiguration).
            // https://doc.traefik.io/traefik/reference/install-configuration/entrypoints/#http-middlewares
            args.Add($"--entrypoints.{entryPoint}.http.middlewares={string.Join(",", middlewares.Select(m => $"{m.Name}@file"))}");
        }
    }

    private static void AddProviders(IList<object> args, bool isPublishMode, bool hasPublishFileConfiguration)
    {
        if (isPublishMode)
        {
            // At publish time routes are labels on the routed services, which only the Docker provider reads.
            // `exposedbydefault=false` keeps services without `traefik.enable=true` off the proxy.
            // https://doc.traefik.io/traefik/reference/install-configuration/providers/docker/
            args.Add("--providers.docker=true");
            args.Add("--providers.docker.exposedbydefault=false");
        }

        if (!isPublishMode || hasPublishFileConfiguration)
        {
            // In run mode the backends include projects and executables on the host, which have no labels, so the
            // routes are written to a file-provider directory instead (see TraefikDynamicConfiguration). At publish
            // time the directory holds Traefik's own configuration, such as entry-point middlewares, when there is any.
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

        if (acme.DnsChallenge is { } dns)
        {
            // The provider's credentials are environment variables of the Traefik container (see AddTraefik), which
            // lego reads; only the provider name is static configuration.
            args.Add($"{prefix}.dnschallenge.provider={dns.Provider}");

            if (dns.Resolvers.Count > 0)
            {
                args.Add($"{prefix}.dnschallenge.resolvers={string.Join(",", dns.Resolvers)}");
            }

            if (dns.DelayBeforeChecks is { } delay)
            {
                // `propagation.delayBeforeChecks` replaced `delayBeforeCheck` in Traefik v3.3.
                args.Add($"{prefix}.dnschallenge.propagation.delaybeforechecks={delay}");
            }
        }
        else
        {
            AddChallenge(acme, prefix, args);
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

    private static void AddChallenge(TraefikAcmeResolver acme, string prefix, IList<object> args)
    {
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
    }

    private static void ValidateTlsDomains(TraefikResource traefik)
    {
        // Checked in both modes, although the domains are only used at publish time, so a mistake shows up on
        // `aspire run` already.
        if (traefik.TlsDomains.Count == 0)
        {
            return;
        }

        if (traefik.DefaultAcmeResolver is not { } resolver)
        {
            throw new InvalidOperationException($"Traefik resource '{traefik.Name}' has TLS domains, but no certificate resolver is configured. Call WithLetsEncrypt.");
        }

        if (resolver.DnsChallenge is null && traefik.TlsDomains.Any(d => d.HasWildcard))
        {
            // https://letsencrypt.org/docs/challenge-types/#dns-01-challenge
            throw new InvalidOperationException($"Traefik resource '{traefik.Name}' has a wildcard TLS domain, which Let's Encrypt only issues with the DNS-01 challenge. Call WithDnsChallenge for the resolver '{resolver.Name}'.");
        }
    }

    private static void AddTlsDomains(TraefikResource traefik, IList<object> args)
    {
        // The default resolver of the entry point requests one certificate per domain, covering its SANs, instead of
        // one per router host. That is how a wildcard certificate is requested.
        // https://doc.traefik.io/traefik/reference/install-configuration/entrypoints/#http-tls
        for (var i = 0; i < traefik.TlsDomains.Count; i++)
        {
            var domain = traefik.TlsDomains[i];
            var prefix = FormattableString.Invariant($"--entrypoints.{TraefikResource.WebSecureEntryPointName}.http.tls.domains[{i}]");

            args.Add(ReferenceExpression.Create($"{prefix}.main={domain.Main}"));

            if (domain.Sans.Count > 0)
            {
                var sans = new ReferenceExpressionBuilder();
                sans.AppendLiteral($"{prefix}.sans=");
                for (var j = 0; j < domain.Sans.Count; j++)
                {
                    if (j > 0)
                    {
                        sans.AppendLiteral(",");
                    }

                    sans.AppendValueProvider(domain.Sans[j]);
                }

                args.Add(sans.Build());
            }
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
