// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// Options for an HTTP route from Traefik to a resource.
/// </summary>
/// <remarks>
/// The route becomes a Traefik router and a Traefik service with the same name. The router rule is built from
/// <see cref="Host"/> and <see cref="PathPrefix"/>, for example <c>Host(`api.example.com`) &amp;&amp; PathPrefix(`/v1`)</c>.
/// See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/routing/rules-and-priority/"/>.
/// </remarks>
[AspireExport(ExposeProperties = true)]
public sealed class TraefikRouteOptions
{
    /// <summary>
    /// Gets or sets the host name the route matches, for example <c>api.example.com</c>.
    /// </summary>
    /// <remarks>
    /// The value can reference parameters, for example <c>ReferenceExpression.Create($"api.{domain}")</c>. At publish
    /// time parameter values stay placeholders in the generated files.
    /// </remarks>
    public ReferenceExpression? Host { get; set; }

    /// <summary>
    /// Gets or sets the path prefix the route matches, for example <c>/api</c>. The prefix is forwarded to the
    /// backend unchanged.
    /// </summary>
    public string? PathPrefix { get; set; }

    /// <summary>
    /// Gets or sets the router priority. When unset, Traefik uses the length of the rule, so more specific rules win.
    /// </summary>
    public int? Priority { get; set; }

    /// <summary>
    /// Gets or sets the name of the endpoint on the routed resource that receives the traffic. Defaults to <c>http</c>,
    /// or to the resource's only HTTP endpoint when it has no endpoint named <c>http</c>.
    /// </summary>
    public string? EndpointName { get; set; }

    /// <summary>
    /// Gets or sets the name of the Traefik router and service. Defaults to the resource name, with a numeric
    /// suffix for the second and later routes of the same resource.
    /// </summary>
    public string? RouterName { get; set; }

    /// <summary>
    /// Gets or sets the entry point the router listens on. Defaults to <c>websecure</c> when TLS is configured on
    /// Traefik and to <c>web</c> otherwise.
    /// </summary>
    public string? EntryPoint { get; set; }
}

/// <summary>
/// The ACME challenge Traefik uses to prove control of a domain.
/// </summary>
/// <remarks>
/// See <see href="https://doc.traefik.io/traefik/reference/install-configuration/tls/certificate-resolvers/acme/"/>.
/// </remarks>
public enum TraefikAcmeChallenge
{
    /// <summary>
    /// The TLS-ALPN-01 challenge, answered on the <c>websecure</c> entry point (port 443).
    /// </summary>
    TlsAlpn,

    /// <summary>
    /// The HTTP-01 challenge, answered on the <c>web</c> entry point (port 80).
    /// </summary>
    Http,
}
