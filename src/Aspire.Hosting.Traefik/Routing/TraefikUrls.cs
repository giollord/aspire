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
    /// <param name="traefik">The Traefik the route targets.</param>
    /// <param name="options">The route options, which need a host.</param>
    /// <param name="isPublishMode">Whether the URL is for the published application.</param>
    /// <returns>The URL.</returns>
    /// <remarks>
    /// <para>
    /// In run mode the entry point's host port is random unless pinned, so the URL includes it. At publish time the
    /// URL assumes the standard ports (80 for HTTP, 443 for HTTPS) that an internet-facing proxy listens on.
    /// </para>
    /// <para>
    /// The scheme and the port depend on how Traefik is configured, which can still change after the URL is built:
    /// <c>GetTraefikUrl</c> may be called before <c>WithLetsEncrypt</c> or <c>PublishAsExternal</c>. So both are
    /// decided when the expression is evaluated, once the model is complete. The scheme is a conditional expression,
    /// which the Docker Compose publisher resolves while writing the files; the port only exists in run mode.
    /// </para>
    /// </remarks>
    public static ReferenceExpression Build(ITraefikRouteTarget traefik, TraefikRouteOptions options, bool isPublishMode)
    {
        if (options.Host is not { } host)
        {
            throw new InvalidOperationException($"The Traefik route has no host, so it has no public URL. Set {nameof(TraefikRouteOptions)}.{nameof(TraefikRouteOptions.Host)}.");
        }

        var builder = new ReferenceExpressionBuilder();
        builder.AppendValueProvider(ReferenceExpression.CreateConditional(
            new SecureCondition(traefik, options, isPublishMode),
            bool.TrueString,
            ReferenceExpression.Create($"https://"),
            ReferenceExpression.Create($"http://")));
        builder.AppendValueProvider(host);

        if (!isPublishMode && traefik is TraefikResource managed)
        {
            builder.AppendLiteral(":");
            builder.AppendValueProvider(new HostPort(managed, options));
        }

        if ((options.PathPrefix ?? options.Path) is { } path)
        {
            builder.AppendLiteral(path);
        }

        return builder.Build();
    }

    /// <summary>
    /// Determines whether a route is served over HTTPS.
    /// </summary>
    /// <param name="traefik">The Traefik the route targets.</param>
    /// <param name="options">The route options.</param>
    /// <param name="isPublishMode">Whether the route is for the published application.</param>
    /// <returns><see langword="true"/> when the route uses TLS; otherwise, <see langword="false"/>.</returns>
    public static bool IsSecure(ITraefikRouteTarget traefik, TraefikRouteOptions options, bool isPublishMode)
    {
        // A route's own resolver turns on TLS when published (see TraefikDockerLabels), but not in run mode, where no
        // certificates are requested.
        if (isPublishMode && options.CertResolver is not null)
        {
            return true;
        }

        return options.EntryPoint is null
            ? traefik.GetRouteDefaults(isPublishMode).IsSecure
            : string.Equals(options.EntryPoint, TraefikResource.WebSecureEntryPointName, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>True</c> when the route is served over HTTPS, read from Traefik's configuration when evaluated.
    /// </summary>
    /// <remarks>
    /// The value expression names exactly the inputs the result depends on, for example
    /// <c>traefik-https.traefik.default.letsencrypt.publish</c>. A conditional expression is named after a hash of its
    /// condition's value expression and its branches, and the manifest keeps one conditional per name, so without it
    /// every Traefik URL would share the name of the first one.
    /// </remarks>
    private sealed class SecureCondition(ITraefikRouteTarget traefik, TraefikRouteOptions options, bool isPublishMode) : IValueProvider, IManifestExpressionProvider
    {
        public string ValueExpression =>
            $"traefik-https.{traefik.Name}.{options.EntryPoint ?? "default"}.{(isPublishMode ? options.CertResolver ?? "none" : "none")}.{(isPublishMode ? "publish" : "run")}";

        public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>(IsSecure(traefik, options, isPublishMode).ToString());
    }

    /// <summary>
    /// The host port of the route's entry point in run mode, read from Traefik's configuration when evaluated.
    /// </summary>
    /// <remarks>
    /// A conditional expression can't choose between the <c>web</c> and <c>websecure</c> ports: dependency tracking
    /// walks both branches, and the <c>websecure</c> endpoint doesn't exist without TLS.
    /// </remarks>
    private sealed class HostPort(TraefikResource traefik, TraefikRouteOptions options) : IValueProvider, IManifestExpressionProvider, IValueWithReferences
    {
        public string ValueExpression => GetPort().ValueExpression;

        public IEnumerable<object> References => [traefik];

        public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default) =>
            GetPort().GetValueAsync(cancellationToken);

        public ValueTask<string?> GetValueAsync(ValueProviderContext context, CancellationToken cancellationToken = default) =>
            ((IValueProvider)GetPort()).GetValueAsync(context, cancellationToken);

        private EndpointReferenceExpression GetPort()
        {
            var entryPoint = options.EntryPoint ?? ((ITraefikRouteTarget)traefik).GetRouteDefaults(isPublishMode: false).EntryPointName;
            return TraefikStaticConfiguration.GetHostEndpoint(traefik, entryPoint).Property(EndpointProperty.Port);
        }
    }
}
