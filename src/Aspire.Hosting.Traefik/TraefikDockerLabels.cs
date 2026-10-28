// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
using Aspire.Hosting.Docker.Resources.ComposeNodes;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// Writes the <c>traefik.*</c> labels that Traefik's Docker provider reads onto a routed Docker Compose service.
/// </summary>
/// <remarks>
/// For a route named <c>api</c> the labels look like:
/// <code>
/// traefik.enable: "true"
/// traefik.http.routers.api.rule: Host(`api.${DOMAIN}`)
/// traefik.http.routers.api.entrypoints: websecure
/// traefik.http.routers.api.service: api
/// traefik.http.routers.api.tls: "true"
/// traefik.http.routers.api.tls.certresolver: letsencrypt
/// traefik.http.services.api.loadbalancer.server.port: "8080"
/// </code>
/// Docker Compose interpolates <c>${VAR}</c> in label values, which keeps parameter values in the generated
/// <c>.env</c> file. A literal <c>$</c> must therefore be written as <c>$$</c>.
/// See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/other-providers/docker/"/> and
/// <see href="https://docs.docker.com/reference/compose-file/interpolation/"/>.
/// </remarks>
internal static class TraefikDockerLabels
{
    public static void Apply(DockerComposeServiceResource composeService, Service service, TraefikRoute route, TraefikRouteDefaults defaults)
    {
        var labels = service.Labels;
        var router = $"traefik.http.routers.{route.RouterName}";
        var loadBalancer = $"traefik.http.services.{route.RouterName}.loadbalancer";

        var host = route.Options.Host is { } hostExpression ? ToComposeExpression(composeService, hostExpression) : null;
        var pathPrefix = route.Options.PathPrefix is { } prefix ? EscapeLiteral(prefix) : null;

        labels["traefik.enable"] = "true";
        labels[$"{router}.rule"] = TraefikRule.Build(host, pathPrefix);
        labels[$"{router}.entrypoints"] = route.Options.EntryPoint ?? ToComposeExpression(composeService, defaults.EntryPoint);
        labels[$"{router}.service"] = route.RouterName;

        if (route.Options.Priority is int priority)
        {
            labels[$"{router}.priority"] = priority.ToString(CultureInfo.InvariantCulture);
        }

        if (route.Options.EntryPoint is null)
        {
            if (defaults.Tls)
            {
                labels[$"{router}.tls"] = "true";
            }

            if (defaults.CertResolver is { } certResolver)
            {
                labels[$"{router}.tls.certresolver"] = ToComposeExpression(composeService, certResolver);
            }
        }

        if (route.Middlewares.Count > 0)
        {
            // Middlewares are defined on every routed service that uses them, not on Traefik's own service, so the
            // same labels work for an external Traefik. Traefik accepts identical definitions from several
            // containers. Within the Docker provider a middleware is referenced without the `@docker` suffix.
            labels[$"{router}.middlewares"] = string.Join(",", route.Middlewares.Select(m => m.Name));

            foreach (var middleware in route.Middlewares)
            {
                var definition = $"traefik.http.middlewares.{middleware.Name}.{middleware.Type}";
                if (middleware.Options.Count == 0)
                {
                    // A middleware with only default options (for example `compress`) is declared with `=true`.
                    labels[definition] = "true";
                }
                else
                {
                    middleware.Options.AddLabels(composeService, labels, definition);
                }
            }
        }

        // Pin the backend port. Traefik only infers it when the container exposes exactly one port, which isn't
        // true for services with several endpoints.
        labels[$"{loadBalancer}.server.port"] = GetTargetPort(service, route);

        if (string.Equals(route.Endpoint.EndpointAnnotation.UriScheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            labels[$"{loadBalancer}.server.scheme"] = "https";
        }

        if (defaults.Network is { } network)
        {
            // The external Traefik reaches the service over the shared network. A service on several networks
            // needs `traefik.docker.network`, or Traefik may pick an address on a network it isn't attached to.
            if (!service.Networks.Contains(network))
            {
                service.Networks.Add(network);
            }

            labels["traefik.docker.network"] = network;
        }
    }

    /// <summary>
    /// The name of the temporary environment variable that carries a route's backend port from the Docker Compose
    /// publisher to the labels. See <see cref="AddTargetPortProbe"/>.
    /// </summary>
    internal static string GetTargetPortVariableName(TraefikRoute route) =>
        $"ASPIRE_TRAEFIK_TARGET_PORT_{TraefikNames.ToEnvironmentName(route.RouterName)}";

    /// <summary>
    /// Adds an environment variable to the routed resource whose value is the target port of the route's endpoint.
    /// </summary>
    /// <remarks>
    /// The container port of a service is decided by the Docker Compose publisher: a fixed target port, a port it
    /// allocates for containers without one (for example <c>8000</c>), or a <c>${API_PORT}</c> placeholder for .NET
    /// projects. That mapping isn't public, but the publisher resolves endpoint references in environment variables
    /// with it. So the port goes through an environment variable, which <see cref="Apply"/> reads back into the label
    /// and then removes from the service.
    /// </remarks>
    internal static void AddTargetPortProbe(IResourceWithEnvironment resource, TraefikRoute route)
    {
        var name = GetTargetPortVariableName(route);
        resource.Annotations.Add(new EnvironmentCallbackAnnotation(context =>
        {
            context.EnvironmentVariables[name] = route.Endpoint.Property(EndpointProperty.TargetPort);
        }));
    }

    private static string GetTargetPort(Service service, TraefikRoute route)
    {
        if (service.Environment.Remove(GetTargetPortVariableName(route), out var port) && !string.IsNullOrEmpty(port))
        {
            return port;
        }

        if (route.Endpoint.EndpointAnnotation.TargetPort is int targetPort)
        {
            return targetPort.ToString(CultureInfo.InvariantCulture);
        }

        throw new InvalidOperationException($"The container port of endpoint '{route.Endpoint.EndpointName}' on resource '{route.Resource.Name}' can't be determined for the Traefik route '{route.RouterName}'.");
    }

    /// <summary>
    /// Renders <paramref name="expression"/> for a Docker Compose file: parameter references become <c>${VAR}</c>
    /// placeholders whose values are written to <c>.env</c>, and literal text is escaped.
    /// </summary>
    internal static string ToComposeExpression(DockerComposeServiceResource composeService, ReferenceExpression expression)
    {
        if (expression.IsConditional)
        {
            throw new NotSupportedException("Conditional expressions are not supported in Traefik routes.");
        }

        var args = expression.ValueProviders
            .Select(object (valueProvider) => valueProvider switch
            {
                // The ParameterResource overload honors the parameter's configured environment name.
                ParameterResource parameter => parameter.AsEnvironmentPlaceholder(composeService),
                ReferenceExpression nested => ToComposeExpression(composeService, nested),
                IManifestExpressionProvider provider => provider.AsEnvironmentPlaceholder(composeService),
                _ => throw new NotSupportedException($"The value '{valueProvider}' can't be used in a Traefik route at publish time."),
            })
            .ToArray();

        return string.Format(CultureInfo.InvariantCulture, EscapeLiteral(expression.Format), args);
    }

    internal static string EscapeLiteral(string value) => value.Replace("$", "$$", StringComparison.Ordinal);
}
