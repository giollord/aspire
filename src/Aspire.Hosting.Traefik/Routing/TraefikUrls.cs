// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
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
    /// URL includes the entry point's public port only when it isn't the standard port of the scheme (80 for HTTP, 443
    /// for HTTPS), for example after <c>WithHostHttpsPort(8443)</c> or <c>WithExternalEntryPoint("https", true, 8443)</c>.
    /// </para>
    /// <para>
    /// The scheme and the port depend on how Traefik is configured, which can still change after the URL is built:
    /// <c>GetTraefikUrl</c> may be called before <c>WithLetsEncrypt</c> or <c>PublishAsExternal</c>. The scheme is
    /// therefore a conditional expression decided when it is evaluated, which the Docker Compose publisher does while
    /// writing the files. The publisher can't evaluate any other kind of value, so the publish-time port is read
    /// when the URL is built, and checked again when it is evaluated (see <see cref="PublishPortCondition"/>).
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
        else if (isPublishMode)
        {
            var port = GetNonStandardPublishPort(traefik, options);
            builder.AppendValueProvider(ReferenceExpression.CreateConditional(
                new PublishPortCondition(traefik, options, port),
                bool.TrueString,
                port is null ? ReferenceExpression.Empty : ReferenceExpression.Create($":{port.Value.ToString(CultureInfo.InvariantCulture)}"),
                ReferenceExpression.Empty));
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

        return options.EntryPoint is { } entryPoint
            ? traefik.IsTlsEntryPoint(entryPoint, isPublishMode)
            : traefik.GetRouteDefaults(isPublishMode).IsSecure;
    }

    /// <summary>
    /// Gets the public port of a route's entry point at publish time when it isn't the standard port of its scheme.
    /// </summary>
    /// <param name="traefik">The Traefik the route targets.</param>
    /// <param name="options">The route options.</param>
    /// <returns>The port, or <see langword="null"/> for the standard port or a parameterized entry point.</returns>
    public static int? GetNonStandardPublishPort(ITraefikRouteTarget traefik, TraefikRouteOptions options)
    {
        if ((options.EntryPoint ?? TraefikNames.GetLiteral(traefik.GetRouteDefaults(isPublishMode: true).EntryPoint)) is not { } entryPoint
            || traefik.GetPublishPort(entryPoint) is not { } port)
        {
            return null;
        }

        var standardPort = IsSecure(traefik, options, isPublishMode: true) ? 443 : 80;
        return port == standardPort ? null : port;
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
    /// <c>True</c> when the publish-time URL has a port, that is, when the entry point's public port isn't the standard
    /// port of the scheme.
    /// </summary>
    /// <remarks>
    /// The port in the URL is the one known when the URL was built, because the Docker Compose publisher only
    /// evaluates the condition of a conditional expression, not other values. Evaluating this condition checks that the
    /// port is still the same once the model is complete, so a URL built before <c>WithHostHttpsPort</c> or
    /// <c>WithExternalEntryPoint</c> fails instead of pointing at the wrong port.
    /// </remarks>
    private sealed class PublishPortCondition(ITraefikRouteTarget traefik, TraefikRouteOptions options, int? port) : IValueProvider, IManifestExpressionProvider
    {
        public string ValueExpression =>
            $"traefik-port.{traefik.Name}.{options.EntryPoint ?? "default"}.{(port is { } value ? value.ToString(CultureInfo.InvariantCulture) : "standard")}";

        public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
        {
            var currentPort = GetNonStandardPublishPort(traefik, options);
            if (currentPort != port)
            {
                throw new InvalidOperationException(
                    $"A Traefik URL of '{traefik.Name}' was built for {Describe(port)}, but its entry point is now published on {Describe(currentPort)}. " +
                    "Configure Traefik's ports and entry points (WithHostPort, WithHostHttpsPort, WithEntryPoint, WithExternalEntryPoint) before calling GetTraefikUrl.");
            }

            return ValueTask.FromResult<string?>((port is not null).ToString());

            static string Describe(int? value) => value is { } number ? $"port {number.ToString(CultureInfo.InvariantCulture)}" : "the standard port";
        }
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
