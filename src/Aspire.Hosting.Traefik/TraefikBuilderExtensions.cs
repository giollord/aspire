// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
using Aspire.Hosting.Docker.Resources.ComposeNodes;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Traefik;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Traefik Proxy to the application model.
/// </summary>
public static class TraefikBuilderExtensions
{
    private const string DockerSocketPath = "/var/run/docker.sock";

    /// <summary>
    /// Adds a Traefik Proxy container to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// The resource has a <c>web</c> entry point for HTTP traffic (container port 80, on a random host port in run
    /// mode and on port 80 at publish time) and a <c>traefik</c> entry point (container port 8080) for the ping
    /// health check and, with <see cref="WithDashboard"/>, the dashboard.
    /// </para>
    /// <para>
    /// Route traffic to other resources with <c>WithTraefikRoute</c> on those resources. In run mode the routes are
    /// written to a file-provider configuration inside the container, and Traefik's traces and metrics are sent to
    /// the Aspire dashboard. At publish time Traefik reads the routes from Docker labels on the routed services,
    /// through the Docker socket, which is mounted read-only.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var traefik = builder.AddTraefik("traefik")
    ///     .WithDashboard();
    ///
    /// builder.AddProject&lt;Projects.Api&gt;("api")
    ///     .WithTraefikRoute(traefik, "api.dev.localhost");
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<TraefikResource> AddTraefik(this IDistributedApplicationBuilder builder, [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var resource = new TraefikResource(name);
        var isPublishMode = builder.ExecutionContext.IsPublishMode;

        builder.Services.TryAddEventingSubscriber<TraefikRoutingSubscriber>();

        var traefik = builder.AddResource(resource)
            .WithImage(TraefikContainerImageTags.Image, TraefikContainerImageTags.Tag)
            .WithImageRegistry(TraefikContainerImageTags.Registry)
            .WithIconName("ArrowRouting")
            // An internet-facing proxy listens on the standard port at publish time. In run mode the host port is
            // random, like any other Aspire endpoint, unless WithHostPort pins it.
            .WithHttpEndpoint(name: TraefikResource.WebEntryPointName, port: isPublishMode ? TraefikResource.WebTargetPort : null, targetPort: TraefikResource.WebTargetPort)
            .WithEndpoint(TraefikResource.WebEntryPointName, endpoint => endpoint.IsExternal = true)
            .WithHttpEndpoint(name: TraefikResource.TraefikEntryPointName, targetPort: TraefikResource.TraefikTargetPort)
            .WithHttpHealthCheck("/ping", endpointName: TraefikResource.TraefikEntryPointName)
            .WithArgs(context => TraefikStaticConfiguration.AddArguments(resource, context));

        if (isPublishMode)
        {
            // The Docker provider discovers routed services through the Docker API. Read-only limits what the
            // proxy can do with the socket, but access to it is still powerful; see the README for alternatives.
            traefik.WithBindMount(DockerSocketPath, DockerSocketPath, isReadOnly: true);

            builder.Eventing.Subscribe<BeforeStartEvent>((@event, _) =>
            {
                // Send telemetry to the dashboard the Docker Compose environment deploys (on by default). The
                // environment only sets OTEL_* variables, which Traefik ignores, so the endpoint becomes Traefik's
                // own flags (see TraefikStaticConfiguration). Environments are usually added after Traefik, so they
                // are looked up once the model is complete. Without an explicit compute environment, only a single
                // environment is unambiguous.
                var environment = resource.GetComputeEnvironment() as DockerComposeEnvironmentResource
                    ?? (@event.Model.Resources.OfType<DockerComposeEnvironmentResource>().ToArray() is [var single] ? single : null);

                if (environment is { DashboardEnabled: true })
                {
                    // The dashboard resource is only reachable through WithDashboard's callback. It keeps the
                    // dashboard enabled, which it already is.
                    builder.CreateResourceBuilder(environment)
                        .WithDashboard(dashboard => resource.PublishOtlpEndpoint = dashboard.Resource.OtlpGrpcEndpoint);
                }

                return Task.CompletedTask;
            });
        }
        else
        {
            var executionContext = builder.ExecutionContext;

            traefik
                .WithOtlpExporter()
                .WithEnvironment(context =>
                {
                    // Reference every backend endpoint from Traefik's environment. Aspire only makes a host
                    // process (project, executable) reachable from the container network when a container
                    // references it in its environment or arguments, and the dynamic configuration file isn't
                    // one of those. The values also show the resolved backend addresses in the dashboard.
                    foreach (var route in resource.Routes)
                    {
                        context.EnvironmentVariables[$"ASPIRE_TRAEFIK_BACKEND_{TraefikNames.ToEnvironmentName(route.RouterName)}"] = route.Endpoint;
                    }

                    // The same applies to middleware options that point at another resource, such as a forward
                    // authentication address.
                    foreach (var middleware in resource.Middlewares)
                    {
                        var index = 0;
                        foreach (var expression in middleware.Options.GetEndpointExpressions())
                        {
                            context.EnvironmentVariables[TraefikDockerLabels.GetMiddlewareValueVariableName(middleware, ++index)] = expression;
                        }
                    }
                })
                .WithContainerFiles(TraefikResource.DynamicConfigurationDirectory, async (_, cancellationToken) =>
                [
                    new ContainerFile
                    {
                        Name = TraefikDynamicConfiguration.FileName,
                        Contents = await TraefikDynamicConfiguration.GenerateAsync(resource, executionContext, cancellationToken).ConfigureAwait(false),
                    },
                ]);
        }

        return traefik;
    }

    /// <summary>
    /// Sets the host port of the <c>web</c> entry point instead of using a randomly assigned port in run mode or port 80
    /// at publish time.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="port">The host port, or <see langword="null"/> to use the default.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// A fixed port keeps public URLs stable between runs, which helps with sign-in redirect URIs. URLs built with
    /// <c>GetTraefikUrl</c> include the port either way.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<TraefikResource> WithHostPort(this IResourceBuilder<TraefikResource> builder, int? port)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEndpoint(TraefikResource.WebEntryPointName, endpoint =>
        {
            endpoint.Port = port ?? (builder.ApplicationBuilder.ExecutionContext.IsPublishMode ? TraefikResource.WebTargetPort : null);
        });
    }

    /// <summary>
    /// Sets the host port of the <c>websecure</c> entry point instead of using a randomly assigned port in run mode or
    /// port 443 at publish time.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="port">The host port, or <see langword="null"/> to use the default.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// The <c>websecure</c> entry point exists once TLS is configured, for example with <c>WithLetsEncrypt</c>. The port
    /// applies whether this method is called before or after that.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<TraefikResource> WithHostHttpsPort(this IResourceBuilder<TraefikResource> builder, int? port)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.HostHttpsPort = port;

        return builder.WithEndpoint(TraefikResource.WebSecureEntryPointName, endpoint =>
        {
            endpoint.Port = port ?? (builder.ApplicationBuilder.ExecutionContext.IsPublishMode ? TraefikResource.WebSecureTargetPort : null);
        }, createIfNotExists: false);
    }

    /// <summary>
    /// Adds a custom entry point (a port Traefik listens on), for example for TCP routes.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="name">The name of the entry point. The resource gets an endpoint with the same name.</param>
    /// <param name="targetPort">The port Traefik listens on inside the container.</param>
    /// <param name="port">The host port. When <see langword="null"/>, a random port is used in run mode and <paramref name="targetPort"/> at publish time.</param>
    /// <param name="scheme">The scheme of the endpoint: <c>tcp</c>, <c>http</c>, or <c>https</c>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// See <see href="https://doc.traefik.io/traefik/reference/install-configuration/entrypoints/"/>.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<TraefikResource> WithEntryPoint(this IResourceBuilder<TraefikResource> builder, string name, int targetPort, int? port = null, string scheme = "tcp")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);

        if (name is TraefikResource.WebEntryPointName or TraefikResource.WebSecureEntryPointName or TraefikResource.TraefikEntryPointName)
        {
            throw new ArgumentException($"The entry point name '{name}' is reserved by the Traefik integration.", nameof(name));
        }

        if (!TraefikNames.IsValidName(name))
        {
            throw new ArgumentException($"The entry point name '{name}' is invalid. Use letters, digits, '-' and '_' only.", nameof(name));
        }

        if (scheme is not ("tcp" or "http" or "https"))
        {
            throw new ArgumentException($"The scheme '{scheme}' is not supported. Use 'tcp', 'http', or 'https'.", nameof(scheme));
        }

        builder.Resource.CustomEntryPoints.Add(new TraefikEntryPoint(name, targetPort));

        return builder.WithEndpoint(
            port: port ?? (builder.ApplicationBuilder.ExecutionContext.IsPublishMode ? targetPort : null),
            targetPort: targetPort,
            scheme: scheme,
            name: name,
            isExternal: true);
    }

    /// <summary>
    /// Enables the Traefik dashboard.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// In run mode the dashboard is served without authentication on the <c>traefik</c> endpoint, and the resource
    /// shows a link to it. At publish time the dashboard is enabled but not exposed, because an unauthenticated
    /// dashboard must not be reachable from the internet.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<TraefikResource> WithDashboard(this IResourceBuilder<TraefikResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.DashboardEnabled = true;

        return builder.WithUrlForEndpoint(TraefikResource.TraefikEntryPointName, url =>
        {
            url.Url = $"{url.Url.TrimEnd('/')}/dashboard/";
            url.DisplayText = "Dashboard";
        });
    }

    /// <summary>
    /// Enables HTTPS with certificates issued by Let's Encrypt.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="email">The email address registered with Let's Encrypt, used for expiry notices.</param>
    /// <param name="resolverName">The name of the Traefik certificate resolver.</param>
    /// <param name="challenge">The ACME challenge used to prove control of the domains.</param>
    /// <param name="staging">Whether to use the Let's Encrypt staging environment, which has higher rate limits but issues untrusted certificates.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// Adds the <c>websecure</c> entry point (container port 443) and makes routes use it with TLS. At publish time
    /// the routes request certificates from the resolver, and the certificates are stored in a volume.
    /// </para>
    /// <para>
    /// Let's Encrypt can't issue certificates for a local run, because it must reach the machine on public ports
    /// 80 and 443 under a public DNS name. In run mode HTTPS still works on <c>websecure</c>, with Traefik's
    /// self-signed default certificate.
    /// </para>
    /// <para>
    /// See <see href="https://doc.traefik.io/traefik/reference/install-configuration/tls/certificate-resolvers/acme/"/>.
    /// </para>
    /// </remarks>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal withLetsEncrypt export, which accepts a string, a parameter, or a reference expression.")]
    public static IResourceBuilder<TraefikResource> WithLetsEncrypt(this IResourceBuilder<TraefikResource> builder, string email, string resolverName = "letsencrypt", TraefikAcmeChallenge challenge = TraefikAcmeChallenge.TlsAlpn, bool staging = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        return ConfigureLetsEncrypt(builder, ReferenceExpression.Create($"{email}"), resolverName, challenge, staging);
    }

    /// <inheritdoc cref="WithLetsEncrypt(IResourceBuilder{TraefikResource}, string, string, TraefikAcmeChallenge, bool)"/>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal withLetsEncrypt export, which accepts a string, a parameter, or a reference expression.")]
    public static IResourceBuilder<TraefikResource> WithLetsEncrypt(this IResourceBuilder<TraefikResource> builder, IResourceBuilder<ParameterResource> email, string resolverName = "letsencrypt", TraefikAcmeChallenge challenge = TraefikAcmeChallenge.TlsAlpn, bool staging = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(email);

        return ConfigureLetsEncrypt(builder, ReferenceExpression.Create($"{email}"), resolverName, challenge, staging);
    }

    /// <inheritdoc cref="WithLetsEncrypt(IResourceBuilder{TraefikResource}, string, string, TraefikAcmeChallenge, bool)"/>
    /// <example>
    /// Build the address from the domain parameter instead of adding a second parameter:
    /// <code lang="csharp">
    /// var domain = builder.AddParameter("domain");
    ///
    /// var traefik = builder.AddTraefik("traefik")
    ///     .WithLetsEncrypt(ReferenceExpression.Create($"admin@{domain}"));
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal withLetsEncrypt export, which accepts a string, a parameter, or a reference expression.")]
    public static IResourceBuilder<TraefikResource> WithLetsEncrypt(this IResourceBuilder<TraefikResource> builder, ReferenceExpression email, string resolverName = "letsencrypt", TraefikAcmeChallenge challenge = TraefikAcmeChallenge.TlsAlpn, bool staging = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(email);

        return ConfigureLetsEncrypt(builder, email, resolverName, challenge, staging);
    }

    /// <summary>
    /// Enables HTTPS with certificates issued by Let's Encrypt.
    /// </summary>
    [AspireExport("withLetsEncrypt")]
    internal static IResourceBuilder<TraefikResource> WithLetsEncryptPolyglot(
        this IResourceBuilder<TraefikResource> builder,
        [AspireUnion(typeof(string), typeof(IResourceBuilder<ParameterResource>), typeof(ReferenceExpression))] object email,
        string resolverName = "letsencrypt",
        TraefikAcmeChallenge challenge = TraefikAcmeChallenge.TlsAlpn,
        bool staging = false)
    {
        return email switch
        {
            string value => builder.WithLetsEncrypt(value, resolverName, challenge, staging),
            IResourceBuilder<ParameterResource> parameter => builder.WithLetsEncrypt(parameter, resolverName, challenge, staging),
            ReferenceExpression expression => builder.WithLetsEncrypt(expression, resolverName, challenge, staging),
            _ => throw new ArgumentException($"Unsupported email type '{email?.GetType().Name}'. Expected a string, a parameter, or a reference expression.", nameof(email)),
        };
    }

    private static IResourceBuilder<TraefikResource> ConfigureLetsEncrypt(IResourceBuilder<TraefikResource> builder, ReferenceExpression email, string resolverName, TraefikAcmeChallenge challenge, bool staging)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolverName);

        if (!TraefikNames.IsValidName(resolverName))
        {
            throw new ArgumentException($"The resolver name '{resolverName}' is invalid. Use letters, digits, '-' and '_' only.", nameof(resolverName));
        }

        var resource = builder.Resource;
        var isPublishMode = builder.ApplicationBuilder.ExecutionContext.IsPublishMode;
        var hadWebSecure = resource.HasWebSecureEntryPoint;

        resource.AcmeResolver = new TraefikAcmeResolver(resolverName, email, challenge, staging);

        if (!hadWebSecure)
        {
            builder.WithEndpoint(
                port: resource.HostHttpsPort ?? (isPublishMode ? TraefikResource.WebSecureTargetPort : null),
                targetPort: TraefikResource.WebSecureTargetPort,
                scheme: "https",
                name: TraefikResource.WebSecureEntryPointName,
                isExternal: true);

            if (isPublishMode)
            {
                // Keep issued certificates across restarts; Let's Encrypt rate-limits repeated issuance.
                builder.WithVolume(VolumeNameGenerator.Generate(builder, "letsencrypt"), TraefikResource.AcmeStorageDirectory);
            }
        }

        return builder;
    }

    /// <summary>
    /// Sets the ACME directory URL that the certificate resolver configured with <c>WithLetsEncrypt</c> requests
    /// certificates from.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="caServer">The ACME directory URL, for example <c>https://acme-staging-v02.api.letsencrypt.org/directory</c>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// Use it instead of the <c>staging</c> argument of <c>WithLetsEncrypt</c> to choose the directory per
    /// deployment, or to use another ACME certificate authority. Like the other ACME settings it only applies at
    /// publish time. See
    /// <see href="https://doc.traefik.io/traefik/reference/install-configuration/tls/certificate-resolvers/acme/"/>.
    /// </remarks>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal withAcmeCaServer export, which accepts a string or a parameter.")]
    public static IResourceBuilder<TraefikResource> WithAcmeCaServer(this IResourceBuilder<TraefikResource> builder, string caServer)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(caServer);

        if (!Uri.TryCreate(caServer, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException($"The ACME directory URL '{caServer}' must be an absolute HTTP or HTTPS URL.", nameof(caServer));
        }

        builder.Resource.AcmeCaServer = ReferenceExpression.Create($"{caServer}");

        return builder;
    }

    /// <inheritdoc cref="WithAcmeCaServer(IResourceBuilder{TraefikResource}, string)"/>
    /// <example>
    /// Default to the production directory and switch a test deployment to staging in its <c>.env</c> file:
    /// <code lang="csharp">
    /// var caServer = builder.AddParameter("acme-ca-server", "https://acme-v02.api.letsencrypt.org/directory");
    ///
    /// var traefik = builder.AddTraefik("traefik")
    ///     .WithLetsEncrypt(email)
    ///     .WithAcmeCaServer(caServer);
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal withAcmeCaServer export, which accepts a string or a parameter.")]
    public static IResourceBuilder<TraefikResource> WithAcmeCaServer(this IResourceBuilder<TraefikResource> builder, IResourceBuilder<ParameterResource> caServer)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(caServer);

        builder.Resource.AcmeCaServer = ReferenceExpression.Create($"{caServer}");

        return builder;
    }

    /// <summary>
    /// Sets the ACME directory URL of the Let's Encrypt certificate resolver.
    /// </summary>
    [AspireExport("withAcmeCaServer")]
    internal static IResourceBuilder<TraefikResource> WithAcmeCaServerPolyglot(
        this IResourceBuilder<TraefikResource> builder,
        [AspireUnion(typeof(string), typeof(IResourceBuilder<ParameterResource>))] object caServer)
    {
        return caServer switch
        {
            string value => builder.WithAcmeCaServer(value),
            IResourceBuilder<ParameterResource> parameter => builder.WithAcmeCaServer(parameter),
            _ => throw new ArgumentException($"Unsupported CA server type '{caServer?.GetType().Name}'. Expected a string or a parameter.", nameof(caServer)),
        };
    }

    /// <summary>
    /// Redirects HTTP requests on the <c>web</c> entry point to HTTPS on the <c>websecure</c> entry point.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// Requires TLS to be configured, for example with <c>WithLetsEncrypt</c>. In run mode the redirect targets the
    /// host port of <c>websecure</c>, so it works with randomly assigned ports.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<TraefikResource> WithHttpsRedirect(this IResourceBuilder<TraefikResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.HttpsRedirectEnabled = true;

        return builder;
    }

    /// <summary>
    /// Uses a Traefik that already runs on the deployment target instead of deploying this container at publish time.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="network">The Docker network the external Traefik is attached to. Routed services join it, and it is declared as an external network.</param>
    /// <param name="entryPoint">The entry point of the external Traefik that routes use.</param>
    /// <param name="certResolver">The certificate resolver defined on the external Traefik that routes use, if any.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// Run mode is unchanged: Aspire still runs the Traefik container. At publish time the container is left out, and
    /// the routed services get labels for the external Traefik, which must use the Docker provider and be attached to
    /// <paramref name="network"/>.
    /// </para>
    /// <para>
    /// Public URLs use HTTPS unless <paramref name="entryPoint"/> is <c>web</c>. When <paramref name="certResolver"/> is not
    /// set, the entry point is expected to configure TLS itself (<c>entrypoints.&lt;name&gt;.http.tls</c>).
    /// </para>
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// var traefik = builder.AddTraefik("traefik")
    ///     .PublishAsExternal(network: "traefik", entryPoint: "websecure", certResolver: "letsencrypt");
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal publishAsExternal export, which accepts a string or a reference expression.")]
    public static IResourceBuilder<TraefikResource> PublishAsExternal(this IResourceBuilder<TraefikResource> builder, string network, string entryPoint = TraefikResource.WebSecureEntryPointName, string? certResolver = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPoint);

        if (certResolver is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(certResolver);
        }

        return builder.PublishAsExternal(
            network,
            ReferenceExpression.Create($"{entryPoint}"),
            certResolver is null ? null : ReferenceExpression.Create($"{certResolver}"));
    }

    /// <summary>
    /// Uses a Traefik that already runs on the deployment target instead of deploying this container at publish time,
    /// with an entry point and certificate resolver that can reference parameters.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="network">The Docker network the external Traefik is attached to. Routed services join it, and it is declared as an external network.</param>
    /// <param name="entryPoint">The entry point of the external Traefik that routes use.</param>
    /// <param name="certResolver">The certificate resolver defined on the external Traefik that routes use, if any.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// The entry point and resolver only appear in <c>traefik.*</c> label values, so parameters in them become
    /// <c>${VAR}</c> placeholders in the generated <c>.env</c> file, and one AppHost can target servers whose Traefik
    /// uses different names. The network name can't be a parameter: Docker Compose doesn't interpolate the YAML keys
    /// it is written to.
    /// </para>
    /// <para>
    /// Public URLs use HTTPS unless <paramref name="entryPoint"/> is the literal <c>web</c> and no resolver is set. A
    /// parameterized entry point is assumed to be HTTPS.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// var entryPoint = builder.AddParameter("traefik-entrypoint");
    /// var certResolver = builder.AddParameter("traefik-certresolver");
    ///
    /// var traefik = builder.AddTraefik("traefik")
    ///     .PublishAsExternal(
    ///         network: "traefik",
    ///         entryPoint: ReferenceExpression.Create($"{entryPoint}"),
    ///         certResolver: ReferenceExpression.Create($"{certResolver}"));
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal publishAsExternal export, which accepts a string or a reference expression.")]
    public static IResourceBuilder<TraefikResource> PublishAsExternal(this IResourceBuilder<TraefikResource> builder, string network, ReferenceExpression entryPoint, ReferenceExpression? certResolver = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(network);
        ArgumentNullException.ThrowIfNull(entryPoint);

        var alreadyExternal = builder.Resource.PublishExternal is not null;
        builder.Resource.PublishExternal = new TraefikExternalSettings(network, entryPoint, certResolver);

        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode || alreadyExternal)
        {
            return builder;
        }

        builder.ExcludeFromManifest();

        var applicationBuilder = builder.ApplicationBuilder;
        var resource = builder.Resource;
        applicationBuilder.Eventing.Subscribe<BeforeStartEvent>((@event, _) =>
        {
            // Compose environments are usually added after Traefik, so they are looked up once the model is
            // complete. The network is read when the compose file is written, so the last PublishAsExternal wins.
            foreach (var environment in @event.Model.Resources.OfType<DockerComposeEnvironmentResource>())
            {
                applicationBuilder.CreateResourceBuilder(environment).ConfigureComposeFile(file =>
                {
                    if (resource.PublishExternal is { } external)
                    {
                        file.AddNetwork(new Network { Name = external.Network, External = true });
                    }
                });
            }

            return Task.CompletedTask;
        });

        return builder;
    }

    /// <summary>
    /// Uses a Traefik that already runs on the deployment target instead of deploying this container at publish time.
    /// </summary>
    [AspireExport("publishAsExternal")]
    internal static IResourceBuilder<TraefikResource> PublishAsExternalPolyglot(
        this IResourceBuilder<TraefikResource> builder,
        string network,
        [AspireUnion(typeof(string), typeof(ReferenceExpression))] object? entryPoint = null,
        [AspireUnion(typeof(string), typeof(ReferenceExpression))] object? certResolver = null)
    {
        // A union parameter can't have a string default, so null stands for the `websecure` default of the C# API.
        return builder.PublishAsExternal(
            network,
            ToExpression(entryPoint, nameof(entryPoint)) ?? ReferenceExpression.Create($"{TraefikResource.WebSecureEntryPointName}"),
            ToExpression(certResolver, nameof(certResolver)));

        static ReferenceExpression? ToExpression(object? value, string parameterName) => value switch
        {
            null => null,
            string text => ReferenceExpression.Create($"{text}"),
            ReferenceExpression expression => expression,
            _ => throw new ArgumentException($"Unsupported type '{value.GetType().Name}'. Expected a string or a reference expression.", parameterName),
        };
    }
}
