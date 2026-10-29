// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

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
/// An ACME certificate resolver configured with <c>WithLetsEncrypt</c>.
/// </summary>
internal sealed record TraefikAcmeResolver(string Name, ReferenceExpression Email, TraefikAcmeChallenge Challenge, bool Staging);

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
internal sealed record TraefikEntryPoint(string Name, int TargetPort);

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
