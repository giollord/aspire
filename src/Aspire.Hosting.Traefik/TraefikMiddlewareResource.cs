// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Traefik;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A Traefik HTTP middleware, which changes requests or responses on the routes that use it.
/// </summary>
/// <remarks>
/// <para>
/// A middleware is configuration of its parent <see cref="TraefikResource"/>; it doesn't run anything itself. It is
/// written to Traefik's dynamic configuration in run mode and to <c>traefik.http.middlewares.*</c> labels at publish
/// time. The Traefik middleware name is the resource name.
/// </para>
/// <para>
/// See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/middlewares/overview/"/>.
/// </para>
/// </remarks>
[AspireExport]
public class TraefikMiddlewareResource : Resource, IResourceWithParent<TraefikResource>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TraefikMiddlewareResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource, which is also the Traefik middleware name.</param>
    /// <param name="type">The Traefik middleware type, for example <c>stripPrefix</c> or <c>headers</c>.</param>
    /// <param name="parent">The Traefik resource the middleware belongs to.</param>
    public TraefikMiddlewareResource(string name, string type, TraefikResource parent) : base(name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(parent);

        Type = type;
        Parent = parent;
    }

    /// <summary>
    /// Gets the Traefik middleware type, for example <c>stripPrefix</c> or <c>headers</c>.
    /// </summary>
    public string Type { get; }

    /// <summary>
    /// Gets the Traefik resource the middleware belongs to.
    /// </summary>
    public TraefikResource Parent { get; }

    /// <summary>
    /// Gets the options of the middleware, the keys under <c>&lt;type&gt;</c> in Traefik's configuration.
    /// </summary>
    internal TraefikOptionMap Options { get; } = new();
}
