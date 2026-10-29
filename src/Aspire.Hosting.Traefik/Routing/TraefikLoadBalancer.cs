// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// Builds the options of the load balancer behind an HTTP route, apart from its servers.
/// </summary>
/// <remarks>
/// The options are built once, keyed as in the file provider, and written either as YAML (run mode) or flattened into
/// lowercase labels (publish):
/// <code>
/// loadBalancer:                          traefik.http.services.api.loadbalancer.passhostheader: "false"
///   passHostHeader: false                traefik.http.services.api.loadbalancer.sticky.cookie.name: affinity
///   sticky:                              traefik.http.services.api.loadbalancer.healthcheck.path: /health
///     cookie:                            traefik.http.services.api.loadbalancer.healthcheck.interval: 5s
///       name: affinity
///   healthCheck:
///     path: /health
///     interval: 5s
/// </code>
/// See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/load-balancing/service/"/>.
/// </remarks>
internal static class TraefikLoadBalancer
{
    /// <summary>
    /// Gets the load balancer options of a route other than its servers, in the casing of the file provider. Options the
    /// route doesn't set are left out, so Traefik's defaults apply.
    /// </summary>
    public static Dictionary<string, object> GetOptions(TraefikRoute route)
    {
        var options = new Dictionary<string, object>();

        if (route.Options.PassHostHeader is bool passHostHeader)
        {
            options["passHostHeader"] = passHostHeader;
        }

        if (route.Options.StickySession is { } sticky)
        {
            var cookie = new Dictionary<string, object>();
            if (sticky.CookieName is { } name)
            {
                cookie["name"] = name;
            }

            cookie["secure"] = sticky.Secure;
            cookie["httpOnly"] = sticky.HttpOnly;

            if (sticky.SameSite is { } sameSite)
            {
                cookie["sameSite"] = sameSite.ToString().ToLowerInvariant();
            }

            if (sticky.MaxAgeSeconds is int maxAge)
            {
                cookie["maxAge"] = maxAge;
            }

            options["sticky"] = new Dictionary<string, object> { ["cookie"] = cookie };
        }

        if (GetHealthCheck(route) is { } healthCheck)
        {
            var check = new Dictionary<string, object> { ["path"] = healthCheck.Path };

            if (healthCheck.IntervalSeconds is int interval)
            {
                check["interval"] = TraefikNames.ToDuration(TimeSpan.FromSeconds(interval), nameof(healthCheck.IntervalSeconds));
            }

            if (healthCheck.TimeoutSeconds is int timeout)
            {
                check["timeout"] = TraefikNames.ToDuration(TimeSpan.FromSeconds(timeout), nameof(healthCheck.TimeoutSeconds));
            }

            if (healthCheck.Status is int status)
            {
                check["status"] = status;
            }

            if (healthCheck.Method is { } method)
            {
                check["method"] = method;
            }

            if (healthCheck.Hostname is { } hostname)
            {
                check["hostname"] = hostname;
            }

            options["healthCheck"] = check;
        }

        return options;
    }

    /// <summary>
    /// Gets the health check of a route: its own, or the one taken from an HTTP probe of the routed endpoint.
    /// </summary>
    /// <remarks>
    /// A readiness probe says whether the resource can take requests, which is what a load balancer needs to know, so it
    /// wins over a liveness probe. A startup probe only matters until the resource has started. A probe of another
    /// endpoint is skipped: Traefik checks the servers of the route, which are addresses of the routed endpoint.
    /// </remarks>
    public static TraefikHealthCheckOptions? GetHealthCheck(TraefikRoute route)
    {
        if (route.Options.HealthCheck is { } healthCheck)
        {
            return healthCheck;
        }

        if (!route.Options.UseResourceProbe)
        {
            return null;
        }

        var probe = route.Resource.Annotations.OfType<EndpointProbeAnnotation>()
            .Where(p => p.Type is ProbeType.Readiness or ProbeType.Liveness)
            .Where(p => p.EndpointReference.Resource == route.Resource && string.Equals(p.EndpointReference.EndpointName, route.Endpoint.EndpointName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Type == ProbeType.Readiness ? 0 : 1)
            .FirstOrDefault();

        if (probe is null)
        {
            return null;
        }

        return new TraefikHealthCheckOptions
        {
            Path = probe.Path,
            // Zero means unset on a probe annotation built by hand; Traefik's defaults apply then.
            IntervalSeconds = probe.PeriodSeconds > 0 ? probe.PeriodSeconds : null,
            TimeoutSeconds = probe.TimeoutSeconds > 0 ? probe.TimeoutSeconds : null,
        };
    }

    /// <summary>
    /// Gets a value indicating whether Traefik talks to the endpoint over HTTP/2 without TLS (h2c), which an endpoint
    /// that only speaks HTTP/2, such as a gRPC service, needs. Traefik uses HTTP/1.1 for <c>http</c> backends otherwise.
    /// </summary>
    /// <remarks>See <see href="https://doc.traefik.io/traefik/user-guides/grpc/"/>.</remarks>
    public static bool UsesH2c(EndpointReference endpoint) =>
        string.Equals(endpoint.EndpointAnnotation.UriScheme, "http", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(endpoint.EndpointAnnotation.Transport, "http2", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Writes options built by <see cref="GetOptions"/> as labels under <paramref name="prefix"/>, for example
    /// <c>traefik.http.services.api.loadbalancer</c>. The Docker provider reads label keys case-insensitively, and
    /// Traefik's own examples use lowercase.
    /// </summary>
    public static void AddLabels(IDictionary<string, string> labels, string prefix, Dictionary<string, object> options)
    {
        foreach (var (key, value) in options)
        {
            var path = $"{prefix}.{key.ToLowerInvariant()}";
            switch (value)
            {
                case Dictionary<string, object> nested:
                    if (key == "sticky")
                    {
                        // The cookie section is enabled by its own key, even when all its options are set.
                        labels[$"{path}.cookie"] = "true";
                    }

                    AddLabels(labels, path, nested);
                    break;
                case bool flag:
                    labels[path] = flag ? "true" : "false";
                    break;
                case int number:
                    labels[path] = number.ToString(CultureInfo.InvariantCulture);
                    break;
                case string text:
                    labels[path] = TraefikDockerLabels.EscapeLiteral(text);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported load balancer option value '{value}'.");
            }
        }
    }
}
