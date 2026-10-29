// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// What route generation needs to know about the Traefik a route targets.
/// </summary>
/// <remarks>
/// Routing code depends on this instead of <see cref="TraefikResource"/> so that a standalone external
/// Traefik resource could be added later without changing the routing pipeline.
/// </remarks>
internal interface ITraefikRouteTarget
{
    string Name { get; }

    TraefikRouteDefaults GetRouteDefaults(bool isPublishMode);
}

/// <summary>
/// The entry point and TLS settings applied to a route that doesn't override them.
/// </summary>
/// <param name="EntryPoint">
/// The entry point the router listens on. Only an external Traefik at publish time can use a parameterized name;
/// for the managed Traefik it is always a literal (see <see cref="TraefikRouteDefaults.EntryPointName"/>).
/// </param>
/// <param name="Tls">Whether the router enables TLS itself (<c>tls: {}</c> / <c>tls=true</c>).</param>
/// <param name="CertResolver">The certificate resolver the router uses, if any.</param>
/// <param name="Network">The shared Docker network routed services must join (external Traefik only).</param>
/// <param name="IsSecure">Whether clients reach the entry point over HTTPS. Used for public URLs.</param>
internal sealed record TraefikRouteDefaults(ReferenceExpression EntryPoint, bool Tls, ReferenceExpression? CertResolver, string? Network, bool IsSecure)
{
    /// <summary>
    /// Gets the entry point name of a route on the managed Traefik, whose entry points are always literal names.
    /// </summary>
    public string EntryPointName => TraefikNames.GetLiteral(EntryPoint)
        ?? throw new InvalidOperationException("The entry point name is parameterized, which is only supported for an external Traefik at publish time.");
}

/// <summary>
/// An ACME certificate resolver added with <c>WithLetsEncrypt</c>.
/// </summary>
/// <remarks>
/// Mutable because <c>WithLetsEncrypt</c> and <c>WithDnsChallenge</c> configure the same resolver, in either order of
/// later calls.
/// </remarks>
internal sealed class TraefikAcmeResolver(string name, ReferenceExpression email)
{
    public string Name { get; } = name;

    public ReferenceExpression Email { get; set; } = email;

    public TraefikAcmeChallenge Challenge { get; set; }

    public bool Staging { get; set; }

    /// <summary>
    /// Gets the DNS-01 challenge, which replaces <see cref="Challenge"/> when set.
    /// </summary>
    public TraefikDnsChallenge? DnsChallenge { get; set; }
}

internal sealed record TraefikDnsChallenge(string Provider, IReadOnlyDictionary<string, ParameterResource> Credentials, IReadOnlyList<string> Resolvers, string? DelayBeforeChecks);

internal sealed record TraefikTlsDomain(ReferenceExpression Main, IReadOnlyList<ReferenceExpression> Sans)
{
    /// <summary>
    /// Gets a value indicating whether a name is a wildcard, which Let's Encrypt only issues with the DNS-01 challenge.
    /// </summary>
    public bool HasWildcard => Sans.Prepend(Main).Any(name => name.Format.Contains('*'));
}

/// <summary>
/// The external Traefik used at publish time, configured with <c>PublishAsExternal</c>.
/// </summary>
/// <remarks>
/// The entry point and resolver only end up in <c>traefik.*</c> label values, where Docker Compose interpolates
/// <c>${VAR}</c>, so they can be parameters. The network can't: it is a YAML key in the compose file, and Compose
/// doesn't interpolate keys.
/// </remarks>
internal sealed record TraefikExternalSettings(string Network, ReferenceExpression EntryPoint, ReferenceExpression? CertResolver);

/// <summary>
/// A custom entry point added with <c>WithEntryPoint</c>.
/// </summary>
/// <param name="Name">The name of the entry point and of its endpoint.</param>
/// <param name="TargetPort">The port Traefik listens on inside the container.</param>
/// <param name="IsUdp">Whether the entry point listens for UDP datagrams instead of TCP connections.</param>
internal sealed record TraefikEntryPoint(string Name, int TargetPort, bool IsUdp);

/// <summary>
/// A Docker Compose config holding the dynamic configuration of a managed Traefik at publish time.
/// </summary>
/// <param name="Environment">The Docker Compose environment the Traefik service belongs to.</param>
/// <param name="Name">The name of the config in the compose file.</param>
/// <param name="Content">The YAML content, with <c>${VAR}</c> placeholders that Docker Compose interpolates.</param>
internal sealed record TraefikPublishFileConfiguration(DockerComposeEnvironmentResource Environment, string Name, string Content);

/// <summary>
/// Records an HTTP route declared on a routed resource with <c>WithTraefikRoute</c>.
/// </summary>
/// <param name="Traefik">The Traefik resource that receives the requests.</param>
/// <param name="Options">The route options.</param>
/// <param name="IsSignInRoute">
/// Whether the route was added by <see cref="TraefikRoutingSubscriber"/> to send <c>/oauth2/*</c> on a protected host to
/// oauth2-proxy. Such routes are recreated whenever the routes are applied.
/// </param>
internal sealed record TraefikRouteAnnotation(TraefikResource Traefik, TraefikRouteOptions Options, bool IsSignInRoute = false) : IResourceAnnotation;

/// <summary>
/// Records a TCP route declared on a routed resource with <c>WithTraefikTcpRoute</c>.
/// </summary>
internal sealed record TraefikTcpRouteAnnotation(TraefikResource Traefik, TraefikTcpRouteOptions Options) : IResourceAnnotation;

/// <summary>
/// Records a UDP route declared on a routed resource with <c>WithTraefikUdpRoute</c>. UDP routers have no rule: every
/// datagram on the entry point goes to the route's service.
/// </summary>
internal sealed record TraefikUdpRouteAnnotation(TraefikResource Traefik, string EntryPoint, string? EndpointName) : IResourceAnnotation;

/// <summary>
/// Marks a resource that already joins an external Traefik's shared network because a middleware calls it.
/// </summary>
internal sealed record TraefikNetworkAnnotation(string Network) : IResourceAnnotation;

/// <summary>
/// Records a middleware applied to every route of a resource with <c>WithTraefikMiddleware</c>.
/// </summary>
internal sealed record TraefikMiddlewareAnnotation(TraefikMiddlewareResource Middleware) : IResourceAnnotation;

/// <summary>
/// A route resolved by <see cref="TraefikRoutingSubscriber"/>: the annotation plus its final router name, backend
/// endpoint, and middlewares in the order Traefik runs them.
/// </summary>
internal sealed record TraefikRoute(
    TraefikResource Traefik,
    IResourceWithEndpoints Resource,
    string RouterName,
    EndpointReference Endpoint,
    TraefikRouteOptions Options,
    IReadOnlyList<TraefikMiddlewareResource> Middlewares);

/// <summary>
/// A TCP or UDP route resolved by <see cref="TraefikRoutingSubscriber"/>.
/// </summary>
/// <param name="Traefik">The Traefik resource that receives the connections.</param>
/// <param name="Resource">The routed resource.</param>
/// <param name="RouterName">The name of the router and of its service.</param>
/// <param name="Endpoint">The endpoint of the routed resource that receives the connections.</param>
/// <param name="EntryPoint">The entry point the router listens on.</param>
/// <param name="Tcp">The options of a TCP route, or <see langword="null"/> for a UDP route.</param>
internal sealed record TraefikTransportRoute(
    TraefikResource Traefik,
    IResourceWithEndpoints Resource,
    string RouterName,
    EndpointReference Endpoint,
    string EntryPoint,
    TraefikTcpRouteOptions? Tcp)
{
    /// <summary>
    /// Gets the configuration section of the route: <c>tcp</c> or <c>udp</c>.
    /// </summary>
    public string Protocol => Tcp is null ? "udp" : "tcp";
}
