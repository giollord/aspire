// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Traefik;

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
