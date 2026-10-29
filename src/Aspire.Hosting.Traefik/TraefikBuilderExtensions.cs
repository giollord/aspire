// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
using Aspire.Hosting.Docker.Resources.ComposeNodes;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Traefik;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Traefik Proxy to the application model.
/// </summary>
public static partial class TraefikBuilderExtensions
{
    private const string DockerSocketPath = "/var/run/docker.sock";

    internal const string SocketProxyEndpointName = "docker";

    // The port docker-socket-proxy serves the filtered Docker API on.
    private const int SocketProxyPort = 2375;

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
    /// health check and, with <see cref="WithDashboard(IResourceBuilder{TraefikResource})"/>, the dashboard.
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

            // DNS provider credentials of DNS-01 resolvers. Only at publish time, because run mode doesn't request
            // certificates, so `aspire run` doesn't ask for the secrets. lego reads them from the environment.
            // https://go-acme.github.io/lego/dns/
            traefik.WithEnvironment(context =>
            {
                foreach (var dns in resource.AcmeResolvers.Select(r => r.DnsChallenge).OfType<TraefikDnsChallenge>())
                {
                    foreach (var (name, parameter) in dns.Credentials)
                    {
                        context.EnvironmentVariables[name] = parameter;
                    }
                }
            });

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

                // Traefik's own dynamic configuration (entry-point middlewares, the dashboard router) is generated with
                // its compose service (see TraefikRoutingSubscriber), but configs are top-level entries of the compose
                // file, which only the environment's ConfigureComposeFile can add.
                foreach (var composeEnvironment in @event.Model.Resources.OfType<DockerComposeEnvironmentResource>())
                {
                    builder.CreateResourceBuilder(composeEnvironment).ConfigureComposeFile(file =>
                    {
                        if (resource.PublishFileConfiguration is { } configuration && configuration.Environment == composeEnvironment)
                        {
                            file.AddConfig(new Config { Name = configuration.Name, Content = configuration.Content });
                        }
                    });
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
    /// dashboard must not be reachable from the internet. To expose it behind authentication, use the overload that
    /// takes a host and middlewares.
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
    /// Enables the Traefik dashboard and routes it on a host, behind middlewares such as authentication.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="host">The host the dashboard is served on, for example <c>traefik.example.com</c>.</param>
    /// <param name="middlewares">The middlewares that protect the dashboard, in the order Traefik runs them. At least one is required.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// The dashboard router uses the default entry point of the routes, with TLS when <c>WithLetsEncrypt</c> is used, and
    /// is served at <c>/dashboard/</c> on the host. It is written to Traefik's own dynamic configuration in both modes,
    /// so it is published as a Docker Compose config of the Traefik service. In run mode the unauthenticated dashboard
    /// on the <c>traefik</c> endpoint stays available too.
    /// </para>
    /// <para>
    /// The middlewares must belong to this Traefik. Single sign-on with <c>AddOAuth2ProxyForwardAuth</c> works: the host
    /// gets a sign-in route like any protected route. With <c>PublishAsExternal</c>, the dashboard of the external Traefik
    /// is its own, so nothing is published for it.
    /// </para>
    /// <para>
    /// See <see href="https://doc.traefik.io/traefik/reference/install-configuration/api-dashboard/"/>.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// var htpasswd = builder.AddParameter("dashboard-htpasswd", secret: true);
    ///
    /// var traefik = builder.AddTraefik("traefik")
    ///     .WithLetsEncrypt("admin@example.com");
    ///
    /// var auth = traefik.AddBasicAuth("dashboard-auth", htpasswd);
    /// traefik.WithDashboard("traefik.example.com", auth);
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal withDashboardRoute export, which accepts a string or a reference expression.")]
    public static IResourceBuilder<TraefikResource> WithDashboard(this IResourceBuilder<TraefikResource> builder, string host, params IResourceBuilder<TraefikMiddlewareResource>[] middlewares)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        return builder.WithDashboard(ReferenceExpression.Create($"{host}"), middlewares);
    }

    /// <summary>
    /// Enables the Traefik dashboard and routes it on a host that can reference parameters, behind middlewares such as
    /// authentication.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="host">The host the dashboard is served on, for example <c>traefik.{domain}</c>.</param>
    /// <param name="middlewares">The middlewares that protect the dashboard, in the order Traefik runs them. At least one is required.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// See <see cref="WithDashboard(IResourceBuilder{TraefikResource}, string, IResourceBuilder{TraefikMiddlewareResource}[])"/>.
    /// </remarks>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal withDashboardRoute export, which accepts a string or a reference expression.")]
    public static IResourceBuilder<TraefikResource> WithDashboard(this IResourceBuilder<TraefikResource> builder, ReferenceExpression host, params IResourceBuilder<TraefikMiddlewareResource>[] middlewares)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(middlewares);

        if (middlewares.Length == 0)
        {
            // A dashboard on a public host without authentication exposes the whole routing configuration.
            throw new ArgumentException("A routed dashboard needs at least one middleware, such as authentication.", nameof(middlewares));
        }

        var resources = GetOwnMiddlewares(builder, middlewares, nameof(middlewares));
        var isFirstCall = builder.Resource.DashboardHost is null;

        builder.Resource.DashboardHost = host;
        builder.Resource.DashboardMiddlewares.Clear();
        builder.Resource.DashboardMiddlewares.AddRange(resources);
        builder.ApplicationBuilder.Services.TryAddEventingSubscriber<TraefikRoutingSubscriber>();
        builder.WithDashboard();

        if (isFirstCall && !builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            var resource = builder.Resource;
            builder.WithUrls(async context =>
            {
                if (resource.DashboardHost is not { } dashboardHost)
                {
                    return;
                }

                var url = TraefikUrls.Build(resource, new TraefikRouteOptions { Host = dashboardHost, PathPrefix = "/dashboard/" }, isPublishMode: false);
                if (await url.GetValueAsync(context.CancellationToken).ConfigureAwait(false) is { Length: > 0 } value)
                {
                    context.Urls.Add(new ResourceUrlAnnotation { Url = value, DisplayText = "Dashboard (routed)" });
                }
            });
        }

        return builder;
    }

    /// <summary>
    /// Enables the Traefik dashboard and routes it on a host, behind middlewares such as authentication.
    /// </summary>
    [AspireExport("withDashboardRoute")]
    internal static IResourceBuilder<TraefikResource> WithDashboardPolyglot(
        this IResourceBuilder<TraefikResource> builder,
        [AspireUnion(typeof(string), typeof(ReferenceExpression))] object host,
        IResourceBuilder<TraefikMiddlewareResource>[] middlewares)
    {
        return host switch
        {
            string text => builder.WithDashboard(text, middlewares),
            ReferenceExpression expression => builder.WithDashboard(expression, middlewares),
            _ => throw new ArgumentException($"Unsupported type '{host?.GetType().Name}'. Expected a string or a reference expression.", nameof(host)),
        };
    }

    /// <summary>
    /// Applies middlewares to every router on an entry point, including routers that don't come from Aspire routes.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="entryPoint">The entry point, for example <c>websecure</c>, or one added with <see cref="WithEntryPoint"/>.</param>
    /// <param name="middlewares">The middlewares, in the order Traefik runs them.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// Entry-point middlewares run before the middlewares of a route, which suits defaults for the whole proxy, such as
    /// security headers or a rate limit. Calling the method again for the same entry point appends to its middlewares.
    /// </para>
    /// <para>
    /// Traefik reads entry-point middlewares from its static configuration, so they are defined in Traefik's own file
    /// provider in both modes; at publish time that file is a Docker Compose config of the Traefik service. That isn't
    /// possible for a Traefik used with <c>PublishAsExternal</c>, which throws at publish time: configure the middlewares
    /// on the external Traefik instead.
    /// </para>
    /// <para>
    /// oauth2-proxy middlewares can't be used, because they would also intercept the sign-in callback. The <c>traefik</c>
    /// entry point can't be used either, because it serves the health check.
    /// </para>
    /// <para>
    /// See <see href="https://doc.traefik.io/traefik/reference/install-configuration/entrypoints/#http-middlewares"/>.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// var traefik = builder.AddTraefik("traefik")
    ///     .WithLetsEncrypt("admin@example.com");
    ///
    /// traefik.WithEntryPointMiddleware("websecure", traefik.AddSecurityHeaders("security-headers"));
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<TraefikResource> WithEntryPointMiddleware(this IResourceBuilder<TraefikResource> builder, string entryPoint, params IResourceBuilder<TraefikMiddlewareResource>[] middlewares)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPoint);
        ArgumentNullException.ThrowIfNull(middlewares);

        if (entryPoint == TraefikResource.TraefikEntryPointName)
        {
            throw new ArgumentException($"The '{TraefikResource.TraefikEntryPointName}' entry point serves the health check and the run-mode dashboard, so it can't have middlewares.", nameof(entryPoint));
        }

        var resources = GetOwnMiddlewares(builder, middlewares, nameof(middlewares));

        if (resources.SelectMany(m => m.WithReferences()).FirstOrDefault(m => m.OAuth2Proxy is not null) is { } signIn)
        {
            throw new ArgumentException($"The oauth2-proxy middleware '{signIn.Name}' can't be an entry-point middleware, because it would also intercept the sign-in callback. Apply it to the routes with WithTraefikMiddleware.", nameof(middlewares));
        }

        if (!builder.Resource.EntryPointMiddlewares.TryGetValue(entryPoint, out var list))
        {
            list = [];
            builder.Resource.EntryPointMiddlewares[entryPoint] = list;
        }

        list.AddRange(resources.Where(m => !list.Contains(m)));
        builder.ApplicationBuilder.Services.TryAddEventingSubscriber<TraefikRoutingSubscriber>();

        return builder;
    }

    private static List<TraefikMiddlewareResource> GetOwnMiddlewares(IResourceBuilder<TraefikResource> builder, IResourceBuilder<TraefikMiddlewareResource>[] middlewares, string parameterName)
    {
        var resources = new List<TraefikMiddlewareResource>(middlewares.Length);
        foreach (var middleware in middlewares)
        {
            ArgumentNullException.ThrowIfNull(middleware, parameterName);

            if (middleware.Resource.Parent != builder.Resource)
            {
                throw new ArgumentException($"The Traefik middleware '{middleware.Resource.Name}' belongs to '{middleware.Resource.Parent.Name}', not '{builder.Resource.Name}'.", parameterName);
            }

            resources.Add(middleware.Resource);
        }

        return resources;
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
    /// Calling the method again with another <paramref name="resolverName"/> adds another resolver, for example one that
    /// uses the DNS-01 challenge (<c>WithDnsChallenge</c>) for some hosts. The first resolver is the default; a route
    /// uses another with <see cref="TraefikRouteOptions.CertResolver"/>. Calling it again with the same name replaces
    /// that resolver's settings.
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
    /// Sets the level of Traefik's own logs, which show up as the resource's console logs.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="level">The minimum level that is logged.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// Traefik logs errors only by default. <see cref="TraefikLogLevel.Debug"/> shows the configuration Traefik receives
    /// from each provider, which helps when a route doesn't match. See
    /// <see href="https://doc.traefik.io/traefik/reference/install-configuration/observability/logs-and-accesslogs/"/>.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<TraefikResource> WithLogLevel(this IResourceBuilder<TraefikResource> builder, TraefikLogLevel level)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.LogLevel = level;

        return builder;
    }

    /// <summary>
    /// Enables Traefik's access logs, one line per request, which show up as the resource's console logs.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="configure">Sets the format and the filters of the access logs.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// Request headers are dropped unless listed in <see cref="TraefikAccessLogOptions.KeepHeaders"/>, because they can
    /// carry credentials or personal data. Traces in the Aspire dashboard already show each request; access logs are
    /// mostly useful when published. See
    /// <see href="https://doc.traefik.io/traefik/reference/install-configuration/observability/logs-and-accesslogs/#accesslogs"/>.
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// traefik.WithAccessLogs(logs =>
    /// {
    ///     logs.Format = TraefikAccessLogFormat.Json;
    ///     logs.StatusCodes.Add("400-599");
    /// });
    /// </code>
    /// </example>
    [AspireExport(RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<TraefikResource> WithAccessLogs(this IResourceBuilder<TraefikResource> builder, Action<TraefikAccessLogOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new TraefikAccessLogOptions();
        configure?.Invoke(options);

        foreach (var statusCode in options.StatusCodes)
        {
            if (statusCode is null || !StatusCodeRangeRegex().IsMatch(statusCode))
            {
                throw new ArgumentException($"The status code filter '{statusCode}' is invalid. Use a code or a range, for example '404' or '500-599'.", nameof(configure));
            }
        }

        foreach (var header in options.KeepHeaders)
        {
            // The header name is a segment of the option path, so it can't contain '.' or '='.
            if (header is null || !HeaderNameRegex().IsMatch(header))
            {
                throw new ArgumentException($"The header name '{header}' is invalid.", nameof(configure));
            }
        }

        if (options.MinDuration is { } minDuration)
        {
            TraefikNames.ToDuration(minDuration, nameof(configure));
        }

        builder.Resource.AccessLogs = options;

        return builder;
    }

    /// <summary>
    /// Reads the Docker API through a socket proxy at publish time, instead of mounting the Docker socket into Traefik.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="configure">Customizes the proxy container, for example to use an image from your own registry. Only called when publishing.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// Access to the Docker socket is access to the host, even read-only, so an internet-facing proxy is better off
    /// without it. This adds a <see href="https://github.com/Tecnativa/docker-socket-proxy">docker-socket-proxy</see>
    /// service named <c>&lt;name&gt;-socket-proxy</c> that mounts the socket and allows only the endpoints the Docker
    /// provider uses (listing and inspecting containers, and events), and points Traefik's Docker provider at it.
    /// </para>
    /// <para>
    /// The image is a third-party one, pinned by digest so that it can't change without an update of this package. To
    /// use a mirror or another proxy with the same environment variables, such as
    /// <see href="https://github.com/linuxserver/docker-socket-proxy">linuxserver/socket-proxy</see>, replace the image
    /// in <paramref name="configure"/> with <c>WithImage</c> and <c>WithImageRegistry</c>. The digest only applies while
    /// the image is the default one; call <c>WithImageSHA256</c> to pin a replacement.
    /// </para>
    /// <para>
    /// The proxy listens without authentication on the application's network, where other services could read the
    /// container list too. It has no effect in run mode, where Traefik reads a file instead, or with
    /// <c>PublishAsExternal</c>, where Traefik isn't deployed.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// traefik.WithDockerSocketProxy(proxy => proxy
    ///     .WithImageRegistry("registry.example.com")
    ///     .WithImage("mirror/docker-socket-proxy", "v0.5.0"));
    /// </code>
    /// </example>
    [AspireExport(RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<TraefikResource> WithDockerSocketProxy(this IResourceBuilder<TraefikResource> builder, Action<IResourceBuilder<ContainerResource>>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var resource = builder.Resource;
        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

        if (resource.DockerSocketProxy is { } existing)
        {
            if (configure is not null)
            {
                configure(builder.ApplicationBuilder.CreateResourceBuilder(existing));
                PinDefaultSocketProxyImage(existing);
            }

            return builder;
        }

        var proxy = builder.ApplicationBuilder.AddContainer($"{resource.Name}-socket-proxy", TraefikContainerImageTags.SocketProxyImage, TraefikContainerImageTags.SocketProxyTag)
            .WithImageRegistry(TraefikContainerImageTags.SocketProxyRegistry)
            .WithBindMount(DockerSocketPath, DockerSocketPath, isReadOnly: true)
            // Only the container endpoints; EVENTS, PING, and VERSION are allowed by default, and POST requests are not.
            // https://github.com/Tecnativa/docker-socket-proxy#grant-or-revoke-access-to-certain-api-sections
            .WithEnvironment("CONTAINERS", "1")
            .WithEndpoint(targetPort: SocketProxyPort, scheme: "tcp", name: SocketProxyEndpointName);

        resource.DockerSocketProxy = proxy.Resource;
        configure?.Invoke(proxy);
        PinDefaultSocketProxyImage(proxy.Resource);

        foreach (var mount in resource.Annotations.OfType<ContainerMountAnnotation>().Where(m => m.Target == DockerSocketPath).ToList())
        {
            resource.Annotations.Remove(mount);
        }

        builder.WaitFor(proxy);

        builder.ApplicationBuilder.Eventing.Subscribe<BeforeStartEvent>((_, _) =>
        {
            // PublishAsExternal may be called after this method. Without the Traefik service, the proxy has no client.
            if (resource.PublishExternal is not null)
            {
                proxy.ExcludeFromManifest();
            }

            return Task.CompletedTask;
        });

        return builder;
    }

    private static void PinDefaultSocketProxyImage(ContainerResource proxy)
    {
        // Pin only the image this package chose. WithImage keeps an existing digest, so pinning before `configure` would
        // pin a mirror or replacement image to the digest of a different image.
        if (proxy.Annotations.OfType<ContainerImageAnnotation>().LastOrDefault() is { } image &&
            image.Registry == TraefikContainerImageTags.SocketProxyRegistry &&
            image.Image == TraefikContainerImageTags.SocketProxyImage &&
            image.Tag == TraefikContainerImageTags.SocketProxyTag)
        {
            image.SHA256 ??= TraefikContainerImageTags.SocketProxySha256;
        }
    }

    /// <summary>
    /// Makes a certificate resolver prove control of the domains with the ACME DNS-01 challenge, through a DNS provider's
    /// API.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="provider">The lego name of the DNS provider, for example <c>cloudflare</c>, <c>route53</c>, or <c>azuredns</c>.</param>
    /// <param name="configure">Sets the provider's credentials and the other options of the challenge.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// DNS-01 works for hosts that the internet can't reach, and it is the only challenge that issues wildcard
    /// certificates (see <c>WithTlsDomain</c>). Call <c>WithLetsEncrypt</c> for the resolver first.
    /// </para>
    /// <para>
    /// See <see href="https://go-acme.github.io/lego/dns/"/> for the providers and their environment variables, and
    /// <see href="https://doc.traefik.io/traefik/reference/install-configuration/tls/certificate-resolvers/acme/#dnschallenge"/>.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// var token = builder.AddParameter("cloudflare-token", secret: true);
    ///
    /// var traefik = builder.AddTraefik("traefik")
    ///     .WithLetsEncrypt("admin@example.com")
    ///     .WithDnsChallenge("cloudflare", dns => dns.Credentials["CF_DNS_API_TOKEN"] = token)
    ///     .WithTlsDomain("example.com", "*.example.com");
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "The options hold a dictionary of parameter builders, which polyglot AppHosts can't pass yet.")]
    public static IResourceBuilder<TraefikResource> WithDnsChallenge(this IResourceBuilder<TraefikResource> builder, string provider, Action<TraefikDnsChallengeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        if (!DnsProviderRegex().IsMatch(provider))
        {
            throw new ArgumentException($"The DNS provider '{provider}' is invalid. Use the provider's lego name, for example 'cloudflare'.", nameof(provider));
        }

        var options = new TraefikDnsChallengeOptions();
        configure?.Invoke(options);

        var resource = builder.Resource;
        var resolver = options.ResolverName is null
            ? resource.DefaultAcmeResolver
            : resource.AcmeResolvers.FirstOrDefault(r => string.Equals(r.Name, options.ResolverName, StringComparison.OrdinalIgnoreCase));

        if (resolver is null)
        {
            throw new InvalidOperationException(options.ResolverName is null
                ? $"Traefik resource '{resource.Name}' has no certificate resolver for the DNS challenge. Call WithLetsEncrypt first."
                : $"Traefik resource '{resource.Name}' has no certificate resolver named '{options.ResolverName}'. Call WithLetsEncrypt with that name first.");
        }

        foreach (var (name, parameter) in options.Credentials)
        {
            if (!EnvironmentVariableRegex().IsMatch(name))
            {
                throw new ArgumentException($"The DNS credential name '{name}' is invalid. Use the environment variable name from the lego documentation, for example 'CF_DNS_API_TOKEN'.", nameof(configure));
            }

            ArgumentNullException.ThrowIfNull(parameter, nameof(configure));
        }

        foreach (var server in options.Resolvers)
        {
            // lego expects `host:port`; without a port it fails only when the first certificate is requested.
            if (string.IsNullOrWhiteSpace(server) || !server.Contains(':') || server.Contains(','))
            {
                throw new ArgumentException($"The DNS resolver '{server}' is invalid. Use host:port, for example '1.1.1.1:53'.", nameof(configure));
            }
        }

        resolver.DnsChallenge = new TraefikDnsChallenge(
            provider,
            options.Credentials.ToDictionary(c => c.Key, c => c.Value.Resource, StringComparer.Ordinal),
            [.. options.Resolvers],
            options.DelayBeforeChecks is { } delay ? TraefikNames.ToDuration(delay, nameof(configure)) : null);

        return builder;
    }

    /// <summary>
    /// Requests a certificate for a domain and its alternative names on the <c>websecure</c> entry point, for example a
    /// wildcard certificate.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="main">The main domain of the certificate, for example <c>example.com</c>.</param>
    /// <param name="sans">The subject alternative names, for example <c>*.example.com</c>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// Without TLS domains, Traefik requests a certificate per router host. With them, the default resolver requests one
    /// certificate per domain at publish time, and routers whose host it covers use it. A wildcard requires the DNS-01
    /// challenge (<c>WithDnsChallenge</c>) on the default resolver.
    /// </para>
    /// <para>
    /// The domains only apply to the managed Traefik at publish time; in run mode Traefik uses its self-signed
    /// certificate. See <see href="https://doc.traefik.io/traefik/reference/install-configuration/entrypoints/#http-tls"/>.
    /// </para>
    /// </remarks>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal withTlsDomain export, which accepts strings or reference expressions.")]
    public static IResourceBuilder<TraefikResource> WithTlsDomain(this IResourceBuilder<TraefikResource> builder, string main, params string[] sans)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(main);
        ArgumentNullException.ThrowIfNull(sans);

        return builder.WithTlsDomain(
            ReferenceExpression.Create($"{main}"),
            [.. sans.Select(san =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(san, nameof(sans));
                return ReferenceExpression.Create($"{san}");
            })]);
    }

    /// <summary>
    /// Requests a certificate for a domain and its alternative names on the <c>websecure</c> entry point, with names that
    /// can reference parameters, for example <c>*.{domain}</c>.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="main">The main domain of the certificate.</param>
    /// <param name="sans">The subject alternative names.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// See <see cref="WithTlsDomain(IResourceBuilder{TraefikResource}, string, string[])"/>.
    /// </remarks>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal withTlsDomain export, which accepts strings or reference expressions.")]
    public static IResourceBuilder<TraefikResource> WithTlsDomain(this IResourceBuilder<TraefikResource> builder, ReferenceExpression main, params ReferenceExpression[] sans)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(main);
        ArgumentNullException.ThrowIfNull(sans);

        foreach (var name in sans.Prepend(main))
        {
            ArgumentNullException.ThrowIfNull(name, nameof(sans));

            // Static configuration splits list values on commas.
            if (name.Format.Contains(','))
            {
                throw new ArgumentException($"The TLS domain '{name.ValueExpression}' contains ','.", nameof(sans));
            }
        }

        builder.Resource.TlsDomains.Add(new TraefikTlsDomain(main, sans));

        return builder;
    }

    /// <summary>
    /// Requests a certificate for a domain and its alternative names on the <c>websecure</c> entry point.
    /// </summary>
    [AspireExport("withTlsDomain")]
    internal static IResourceBuilder<TraefikResource> WithTlsDomainPolyglot(
        this IResourceBuilder<TraefikResource> builder,
        [AspireUnion(typeof(string), typeof(ReferenceExpression))] object main,
        [AspireUnion(typeof(string), typeof(ReferenceExpression))] object[]? sans = null)
    {
        return builder.WithTlsDomain(ToExpression(main, nameof(main)), [.. (sans ?? []).Select(san => ToExpression(san, nameof(sans)))]);

        static ReferenceExpression ToExpression(object value, string parameterName) => value switch
        {
            string text => ReferenceExpression.Create($"{text}"),
            ReferenceExpression expression => expression,
            _ => throw new ArgumentException($"Unsupported type '{value?.GetType().Name}'. Expected a string or a reference expression.", parameterName),
        };
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

        // Resolver names are keys of Traefik's static configuration, which it matches case-insensitively.
        var resolver = resource.AcmeResolvers.FirstOrDefault(r => string.Equals(r.Name, resolverName, StringComparison.OrdinalIgnoreCase));
        if (resolver is null)
        {
            resolver = new TraefikAcmeResolver(resolverName, email);
            resource.AcmeResolvers.Add(resolver);
        }

        resolver.Email = email;
        resolver.Challenge = challenge;
        resolver.Staging = staging;

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

    // A status code or a range, for example `404` or `500-599`, as Traefik's access log filter reads them.
    [GeneratedRegex("^[1-5][0-9]{2}(-[1-5][0-9]{2})?$")]
    private static partial Regex StatusCodeRangeRegex();

    // HTTP header field names are tokens (RFC 9110, section 5.1), without '.', which separates option path segments.
    [GeneratedRegex("^[A-Za-z0-9!#$%&'*+^_`|~-]+$")]
    private static partial Regex HeaderNameRegex();

    // lego provider names, for example `cloudflare`, `route53`, `azuredns`, or `ovh`.
    [GeneratedRegex("^[a-z0-9][a-z0-9.-]*$")]
    private static partial Regex DnsProviderRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvironmentVariableRegex();
}
