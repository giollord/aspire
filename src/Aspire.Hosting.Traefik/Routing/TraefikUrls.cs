// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// Builds the public URL a route is reachable on through Traefik.
/// </summary>
internal static class TraefikUrls
{
    /// <summary>
    /// Builds a URL such as <c>http://api.dev.localhost:8000</c> in run mode, or <c>https://api.example.com</c> at
    /// publish time.
    /// </summary>
    /// <remarks>
    /// In run mode the entry point's host port is random unless pinned, so the URL includes it. At publish time the
    /// URL assumes the standard ports (80 for HTTP, 443 for HTTPS) that an internet-facing proxy listens on.
    /// </remarks>
    public static ReferenceExpression Build(ITraefikRouteTarget traefik, TraefikRouteOptions options, bool isPublishMode)
    {
        if (options.Host is not { } host)
        {
            throw new InvalidOperationException($"The Traefik route has no host, so it has no public URL. Set {nameof(TraefikRouteOptions)}.{nameof(TraefikRouteOptions.Host)}.");
        }

        var defaults = traefik.GetRouteDefaults(isPublishMode);
        // A route's own resolver turns on TLS when published (see TraefikDockerLabels), but not in run mode, where no
        // certificates are requested.
        var isSecure = options.EntryPoint is null
            ? defaults.IsSecure || (isPublishMode && options.CertResolver is not null)
            : string.Equals(options.EntryPoint, TraefikResource.WebSecureEntryPointName, StringComparison.Ordinal) || (isPublishMode && options.CertResolver is not null);

        var builder = new ReferenceExpressionBuilder();
        builder.AppendLiteral(isSecure ? "https://" : "http://");
        builder.AppendValueProvider(host);

        if (!isPublishMode && traefik is TraefikResource managed)
        {
            builder.AppendLiteral(":");
            var entryPoint = options.EntryPoint ?? defaults.EntryPointName;
            builder.AppendValueProvider(TraefikStaticConfiguration.GetHostEndpoint(managed, entryPoint).Property(EndpointProperty.Port));
        }

        if ((options.PathPrefix ?? options.Path) is { } path)
        {
            builder.AppendLiteral(path);
        }

        return builder.Build();
    }
}
