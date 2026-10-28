// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Traefik;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for routing traffic from Traefik to resources.
/// </summary>
public static class TraefikRoutingExtensions
{
    /// <summary>
    /// Routes HTTP requests for <paramref name="host"/> through <paramref name="traefik"/> to this resource.
    /// </summary>
    /// <typeparam name="T">The type of the routed resource.</typeparam>
    /// <param name="builder">The builder of the routed resource.</param>
    /// <param name="traefik">The Traefik resource that receives the requests.</param>
    /// <param name="host">The host name to match, for example <c>api.dev.localhost</c>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// Requests go to the resource's <c>http</c> endpoint. Use the overload that takes a configuration callback to
    /// choose another endpoint, match a path prefix, or name the router. A resource can have several routes.
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// var traefik = builder.AddTraefik("traefik");
    ///
    /// builder.AddContainer("whoami", "traefik/whoami")
    ///     .WithHttpEndpoint(targetPort: 80)
    ///     .WithTraefikRoute(traefik, "whoami.dev.localhost");
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal withTraefikRoute export, which accepts a string or a reference expression.")]
    public static IResourceBuilder<T> WithTraefikRoute<T>(this IResourceBuilder<T> builder, IResourceBuilder<TraefikResource> traefik, string host)
        where T : IResourceWithEndpoints
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        return builder.WithTraefikRoute(traefik, options => options.Host = ReferenceExpression.Create($"{host}"));
    }

    /// <summary>
    /// Routes HTTP requests for <paramref name="host"/> through <paramref name="traefik"/> to this resource.
    /// </summary>
    /// <typeparam name="T">The type of the routed resource.</typeparam>
    /// <param name="builder">The builder of the routed resource.</param>
    /// <param name="traefik">The Traefik resource that receives the requests.</param>
    /// <param name="host">The host name to match. It can reference parameters, which stay placeholders at publish time.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// <code lang="csharp">
    /// var domain = builder.AddParameter("domain");
    /// var traefik = builder.AddTraefik("traefik");
    ///
    /// builder.AddProject&lt;Projects.Api&gt;("api")
    ///     .WithTraefikRoute(traefik, ReferenceExpression.Create($"api.{domain}"));
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal withTraefikRoute export, which accepts a string or a reference expression.")]
    public static IResourceBuilder<T> WithTraefikRoute<T>(this IResourceBuilder<T> builder, IResourceBuilder<TraefikResource> traefik, ReferenceExpression host)
        where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(host);

        return builder.WithTraefikRoute(traefik, options => options.Host = host);
    }

    /// <summary>
    /// Routes HTTP requests through <paramref name="traefik"/> to this resource.
    /// </summary>
    [AspireExport("withTraefikRoute")]
    internal static IResourceBuilder<T> WithTraefikRoutePolyglot<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<TraefikResource> traefik,
        [AspireUnion(typeof(string), typeof(ReferenceExpression))] object host)
        where T : IResourceWithEndpoints
    {
        return host switch
        {
            string value => builder.WithTraefikRoute(traefik, value),
            ReferenceExpression expression => builder.WithTraefikRoute(traefik, expression),
            _ => throw new ArgumentException($"Unsupported host type '{host?.GetType().Name}'. Expected a string or a reference expression.", nameof(host)),
        };
    }

    /// <summary>
    /// Routes HTTP requests through <paramref name="traefik"/> to this resource, with the route described by
    /// <paramref name="configure"/>.
    /// </summary>
    /// <typeparam name="T">The type of the routed resource.</typeparam>
    /// <param name="builder">The builder of the routed resource.</param>
    /// <param name="traefik">The Traefik resource that receives the requests.</param>
    /// <param name="configure">Configures the route. At least a host or a path prefix is required.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// <code lang="csharp">
    /// builder.AddProject&lt;Projects.Api&gt;("api")
    ///     .WithTraefikRoute(traefik, route =>
    ///     {
    ///         route.Host = ReferenceExpression.Create($"app.dev.localhost");
    ///         route.PathPrefix = "/api";
    ///         route.RouterName = "app-api";
    ///     });
    /// </code>
    /// </example>
    [AspireExport("withTraefikRouteOptions", RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<T> WithTraefikRoute<T>(this IResourceBuilder<T> builder, IResourceBuilder<TraefikResource> traefik, Action<TraefikRouteOptions> configure)
        where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(traefik);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new TraefikRouteOptions();
        configure(options);

        if (options.Host is null && string.IsNullOrEmpty(options.PathPrefix))
        {
            throw new ArgumentException("A Traefik route needs a host, a path prefix, or both.", nameof(configure));
        }

        if (options.PathPrefix is { } pathPrefix && !pathPrefix.StartsWith('/'))
        {
            throw new ArgumentException($"The path prefix '{pathPrefix}' must start with '/'.", nameof(configure));
        }

        if (options.PathPrefix?.Contains('`') == true)
        {
            // Rule values are quoted with backticks, so a backtick would end the value early.
            throw new ArgumentException("The path prefix can't contain '`'.", nameof(configure));
        }

        builder.ApplicationBuilder.Services.TryAddEventingSubscriber<TraefikRoutingSubscriber>();

        return builder.WithAnnotation(new TraefikRouteAnnotation(traefik.Resource, options), ResourceAnnotationMutationBehavior.Append);
    }

    /// <summary>
    /// Gets the public URL of this resource through Traefik, from its first Traefik route.
    /// </summary>
    /// <typeparam name="T">The type of the routed resource.</typeparam>
    /// <param name="builder">The builder of the routed resource.</param>
    /// <returns>
    /// An expression such as <c>http://api.dev.localhost:52314</c> in run mode, which includes the entry point's host
    /// port, or <c>https://api.example.com</c> at publish time.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Use the URL wherever the public address is needed, for example as an OAuth redirect URI or a public base URL
    /// in another resource's environment.
    /// </para>
    /// <para>
    /// The scheme depends on the Traefik configuration when this method is called, so configure TLS
    /// (<c>WithLetsEncrypt</c>) and <c>PublishAsExternal</c> on Traefik first.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The resource has no Traefik route, or its first route has no host.</exception>
    [AspireExport]
    public static ReferenceExpression GetTraefikUrl<T>(this IResourceBuilder<T> builder)
        where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);

        var annotation = builder.Resource.Annotations.OfType<TraefikRouteAnnotation>().FirstOrDefault()
            ?? throw new InvalidOperationException($"Resource '{builder.Resource.Name}' has no Traefik route. Call WithTraefikRoute first.");

        return TraefikUrls.Build(annotation.Traefik, annotation.Options, builder.ApplicationBuilder.ExecutionContext.IsPublishMode);
    }
}
