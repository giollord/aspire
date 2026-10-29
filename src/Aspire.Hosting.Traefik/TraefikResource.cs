// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Traefik;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A resource that represents a Traefik Proxy instance used as the ingress of the application.
/// </summary>
/// <remarks>
/// <para>
/// When the application runs locally, Traefik is a container managed by Aspire. Routes declared with
/// <c>WithTraefikRoute</c> on other resources are written to a generated file-provider configuration.
/// </para>
/// <para>
/// When the application is published to Docker Compose, the routes become <c>traefik.*</c> labels on the
/// routed services, and Traefik discovers them through its Docker provider. A Traefik that already runs on
/// the target host can be used instead with <c>PublishAsExternal</c>.
/// </para>
/// </remarks>
/// <param name="name">The name of the resource.</param>
[AspireExport]
public class TraefikResource(string name) : ContainerResource(name), IResourceWithServiceDiscovery, ITraefikRouteTarget
{
    internal const string WebEntryPointName = "web";
    internal const string WebSecureEntryPointName = "websecure";
    internal const string TraefikEntryPointName = "traefik";

    internal const int WebTargetPort = 80;
    internal const int WebSecureTargetPort = 443;
    internal const int TraefikTargetPort = 8080;

    internal const string DynamicConfigurationDirectory = "/etc/traefik/dynamic";
    internal const string AcmeStorageDirectory = "/letsencrypt";

    private EndpointReference? _webEndpoint;

    /// <summary>
    /// Gets the endpoint of the <c>web</c> entry point (plain HTTP).
    /// </summary>
    public EndpointReference WebEndpoint => _webEndpoint ??= new(this, WebEntryPointName);

    /// <summary>
    /// Gets a value indicating whether the Traefik dashboard and API are enabled.
    /// </summary>
    internal bool DashboardEnabled { get; set; }

    /// <summary>
    /// Gets a value indicating whether requests to the <c>web</c> entry point are redirected to <c>websecure</c>.
    /// </summary>
    internal bool HttpsRedirectEnabled { get; set; }

    /// <summary>
    /// Gets the HTTPS host port requested with <c>WithHostHttpsPort</c>, applied when the <c>websecure</c>
    /// entry point is created.
    /// </summary>
    internal int? HostHttpsPort { get; set; }

    /// <summary>
    /// Gets the ACME (Let's Encrypt) certificate resolvers, in the order they were added.
    /// </summary>
    internal List<TraefikAcmeResolver> AcmeResolvers { get; } = [];

    /// <summary>
    /// Gets the resolver that routes and the <c>websecure</c> entry point use unless a route names another: the first
    /// one added.
    /// </summary>
    internal TraefikAcmeResolver? DefaultAcmeResolver => AcmeResolvers.Count > 0 ? AcmeResolvers[0] : null;

    /// <summary>
    /// Gets the certificate domains of the <c>websecure</c> entry point, added with <c>WithTlsDomain</c>.
    /// </summary>
    internal List<TraefikTlsDomain> TlsDomains { get; } = [];

    /// <summary>
    /// Gets the ACME directory URL set with <c>WithAcmeCaServer</c>, if any.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="AcmeResolvers"/> so it doesn't depend on the order of the calls. It applies to every
    /// resolver.
    /// </remarks>
    internal ReferenceExpression? AcmeCaServer { get; set; }

    /// <summary>
    /// Gets the settings of the external Traefik used at publish time, if one is configured.
    /// </summary>
    internal TraefikExternalSettings? PublishExternal { get; set; }

    /// <summary>
    /// Gets the OTLP gRPC endpoint of the Docker Compose environment's dashboard that Traefik sends telemetry to at
    /// publish time, if that dashboard is enabled.
    /// </summary>
    internal EndpointReference? PublishOtlpEndpoint { get; set; }

    /// <summary>
    /// Gets the custom entry points added with <c>WithEntryPoint</c>.
    /// </summary>
    internal List<TraefikEntryPoint> CustomEntryPoints { get; } = [];

    /// <summary>
    /// Gets the middlewares added with <c>AddMiddleware</c> and the other middleware methods.
    /// </summary>
    internal List<TraefikMiddlewareResource> Middlewares { get; } = [];

    /// <summary>
    /// Gets the middlewares that apply to every router on an entry point, by entry point name, added with
    /// <c>WithEntryPointMiddleware</c>.
    /// </summary>
    internal OrderedDictionary<string, List<TraefikMiddlewareResource>> EntryPointMiddlewares { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the host the dashboard is routed on with <c>WithDashboard(host, middlewares)</c>, if any.
    /// </summary>
    internal ReferenceExpression? DashboardHost { get; set; }

    /// <summary>
    /// Gets the middlewares that protect the dashboard router.
    /// </summary>
    internal List<TraefikMiddlewareResource> DashboardMiddlewares { get; } = [];

    /// <summary>
    /// Gets the name of the router that serves the dashboard on <see cref="DashboardHost"/>.
    /// </summary>
    internal string DashboardRouterName => $"{Name}-dashboard";

    /// <summary>
    /// Gets a value indicating whether the managed container reads a dynamic configuration file at publish time. Routes
    /// are labels then, but entry-point middlewares and the dashboard router belong to Traefik itself.
    /// </summary>
    internal bool HasPublishFileConfiguration => PublishExternal is null && (EntryPointMiddlewares.Count > 0 || DashboardHost is not null);

    /// <summary>
    /// Gets the dynamic configuration written for the managed container at publish time, set while the Docker Compose
    /// file is generated. See <see cref="TraefikDynamicConfiguration.GeneratePublish"/>.
    /// </summary>
    internal TraefikPublishFileConfiguration? PublishFileConfiguration { get; set; }

    /// <summary>
    /// Gets the routes that target this Traefik. Populated by <see cref="TraefikRoutingSubscriber"/> before the
    /// application starts or publishes.
    /// </summary>
    internal List<TraefikRoute> Routes { get; } = [];

    /// <summary>
    /// Gets a value indicating whether the <c>websecure</c> entry point exists on the managed container.
    /// </summary>
    internal bool HasWebSecureEntryPoint => AcmeResolvers.Count > 0;

    TraefikRouteDefaults ITraefikRouteTarget.GetRouteDefaults(bool isPublishMode)
    {
        if (isPublishMode && PublishExternal is { } external)
        {
            // The external Traefik owns its entry points and certificates. A router on a TLS entry point that
            // has no resolver of its own still gets TLS from the entry point's `http.tls` settings, so only
            // name the resolver when one was given. The URL scheme follows the usual convention that only
            // the `web` entry point is plain HTTP. A parameterized entry point is assumed to be HTTPS, because its
            // value is only known when the compose file is deployed.
            var secure = external.CertResolver is not null || TraefikNames.GetLiteral(external.EntryPoint) is not WebEntryPointName;
            return new(external.EntryPoint, Tls: external.CertResolver is not null, external.CertResolver, external.Network, secure);
        }

        if (HasWebSecureEntryPoint)
        {
            // Let's Encrypt can't issue certificates for a local `aspire run`: the ACME server must reach the
            // machine on public ports 80/443 under a public DNS name. Run mode therefore still terminates TLS on
            // `websecure`, but with Traefik's built-in self-signed default certificate and no resolver.
            var resolver = isPublishMode ? ReferenceExpression.Create($"{DefaultAcmeResolver!.Name}") : null;
            return new(ReferenceExpression.Create($"{WebSecureEntryPointName}"), Tls: true, resolver, Network: null, IsSecure: true);
        }

        return new(ReferenceExpression.Create($"{WebEntryPointName}"), Tls: false, CertResolver: null, Network: null, IsSecure: false);
    }
}
