// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// Records a TCP route declared on a routed resource with <c>WithTraefikTcpRoute</c>.
/// </summary>
internal sealed record TraefikTcpRouteAnnotation(TraefikResource Traefik, TraefikTcpRouteOptions Options) : IResourceAnnotation;

/// <summary>
/// Records a UDP route declared on a routed resource with <c>WithTraefikUdpRoute</c>. UDP routers have no rule: every
/// datagram on the entry point goes to the route's service.
/// </summary>
internal sealed record TraefikUdpRouteAnnotation(TraefikResource Traefik, string EntryPoint, string? EndpointName) : IResourceAnnotation;
