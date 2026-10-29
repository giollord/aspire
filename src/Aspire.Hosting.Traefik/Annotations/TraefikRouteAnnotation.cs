// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Traefik;

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
