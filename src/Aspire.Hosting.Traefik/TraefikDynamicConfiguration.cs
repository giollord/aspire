// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using YamlDotNet.Serialization;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// Generates the file-provider (dynamic) configuration used by the managed Traefik in run mode.
/// </summary>
/// <remarks>
/// The output is YAML because the file provider reads YAML or TOML, not JSON:
/// <code>
/// http:
///   routers:
///     api:
///       rule: Host(`api.dev.localhost`)
///       entryPoints:
///       - web
///       service: api
///   services:
///     api:
///       loadBalancer:
///         servers:
///         - url: http://host.docker.internal:5123
/// </code>
/// See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/routing/router/"/>.
/// </remarks>
internal static class TraefikDynamicConfiguration
{
    internal const string FileName = "aspire.yml";

    public static async Task<string> GenerateAsync(TraefikResource traefik, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        // Backend URLs and hosts are resolved from Traefik's point of view: containers by their name on the
        // Aspire container network, host processes through the container host (host.docker.internal, or the
        // Aspire container tunnel), so nothing about Docker Desktop or the tunnel is hardcoded here.
        var context = new ValueProviderContext
        {
            ExecutionContext = executionContext,
            Caller = traefik,
            Network = KnownNetworkIdentifiers.DefaultAspireContainerNetwork,
        };

        var defaults = ((ITraefikRouteTarget)traefik).GetRouteDefaults(isPublishMode: false);
        var routers = new Dictionary<string, object>();
        var services = new Dictionary<string, object>();

        foreach (var route in traefik.Routes)
        {
            var host = route.Options.Host is { } hostExpression
                ? await hostExpression.GetValueAsync(context, cancellationToken).ConfigureAwait(false)
                : null;

            var router = new Dictionary<string, object>
            {
                ["rule"] = TraefikRule.Build(host, route.Options.PathPrefix),
                ["entryPoints"] = new[] { route.Options.EntryPoint ?? defaults.EntryPointName },
                ["service"] = route.RouterName,
            };

            if (route.Options.Priority is int priority)
            {
                router["priority"] = priority;
            }

            if (defaults.Tls && route.Options.EntryPoint is null)
            {
                // An empty `tls` section enables TLS with the default certificate. No resolver in run mode: see
                // TraefikResource.GetRouteDefaults.
                router["tls"] = new Dictionary<string, object>();
            }

            routers[route.RouterName] = router;

            var url = await route.Endpoint.GetValueAsync(context, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"The endpoint '{route.Endpoint.EndpointName}' of resource '{route.Resource.Name}' has no URL.");

            services[route.RouterName] = new Dictionary<string, object>
            {
                ["loadBalancer"] = new Dictionary<string, object>
                {
                    ["servers"] = new[] { new Dictionary<string, object> { ["url"] = url } },
                },
            };
        }

        var document = new Dictionary<string, object>();
        if (routers.Count > 0)
        {
            document["http"] = new Dictionary<string, object>
            {
                ["routers"] = routers,
                ["services"] = services,
            };
        }

        var serializer = new SerializerBuilder()
            .DisableAliases()
            .Build();

        return serializer.Serialize(document);
    }
}

/// <summary>
/// Builds Traefik v3 router rules.
/// </summary>
internal static class TraefikRule
{
    /// <summary>
    /// Builds a rule such as <c>Host(`api.example.com`) &amp;&amp; PathPrefix(`/v1`)</c>. Values are quoted with
    /// backticks, which can't appear in host names or URL paths.
    /// See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/routing/rules-and-priority/"/>.
    /// </summary>
    public static string Build(string? host, string? pathPrefix)
    {
        var matchers = new List<string>(2);

        if (!string.IsNullOrEmpty(host))
        {
            matchers.Add($"Host(`{host}`)");
        }

        if (!string.IsNullOrEmpty(pathPrefix))
        {
            matchers.Add($"PathPrefix(`{pathPrefix}`)");
        }

        return string.Join(" && ", matchers);
    }
}
