# Traefik hosting integration

Use this integration to model, configure, and orchestrate a Traefik Proxy reverse proxy that routes traffic to the resources of an Aspire solution.

## Getting started

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Traefik` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Traefik
```

## Usage example

Then, in the AppHost, add a Traefik resource and route a host name to another resource with either C# or TypeScript:

**C#**

```csharp
var traefik = builder.AddTraefik("traefik")
                     .WithDashboard();

var api = builder.AddProject<Projects.Api>("api")
                 .WithTraefikRoute(traefik, "api.dev.localhost");
```

**TypeScript**

```typescript
const traefik = await builder.addTraefik("traefik")
    .withDashboard();

const api = await builder.addNodeApp("api", "../api", "server.js")
    .withTraefikRoute(traefik, "api.dev.localhost");
```

Browsers resolve every `*.localhost` name to the local machine, so `http://api.dev.localhost:<port>` works without editing the hosts file. The routed resource shows that URL in the dashboard.

## How routes are applied

Routes are declared on the routed resource and work the same way in both modes:

- **Run mode**: Traefik runs as a container. The routes are written to a file-provider configuration inside the container, and backends are reached from the container network, both containers and processes on the host (projects, executables). Traefik's traces and metrics are sent to the Aspire dashboard.
- **Publish (Docker Compose)**: the routes become `traefik.*` labels on the routed services. Traefik discovers them through its Docker provider, with the Docker socket mounted read-only. Parameter values in host names stay `${VAR}` placeholders in the generated `.env` file. When the compose environment deploys its Aspire dashboard (the default), Traefik sends its traces and metrics there.

Use the overload with a callback for path-based routes, priorities, or a specific endpoint:

```csharp
var domain = builder.AddParameter("domain");

api.WithTraefikRoute(traefik, route =>
{
    route.Host = ReferenceExpression.Create($"app.{domain}");
    route.PathPrefix = "/api";
    route.StripPathPrefix = true;   // the backend sees /orders for /api/orders
    route.EndpointName = "http";
});
```

### Rules

The matcher options of a route are combined with `&&`: `Host`, `PathPrefix`, `Path`, `Methods` and `ClientIps` (any of them), `Headers`, and `Query`. For alternatives, negations, or regular expressions, set `Rule` to a `TraefikRule`, built with `&`, `|`, and `!`:

```csharp
api.WithTraefikRoute(traefik, route =>
{
    route.Host = ReferenceExpression.Create($"api.{domain}");
    route.Rule = (TraefikRule.PathPrefix("/admin") | TraefikRule.PathRegexp("^/ops/[0-9]+"))
               & !TraefikRule.ClientIP("203.0.113.0/24");
});
// Host(`api.${DOMAIN}`) && (PathPrefix(`/admin`) || PathRegexp(`^/ops/[0-9]+`)) && !ClientIP(`203.0.113.0/24`)
```

Values are quoted and nested combinations get parentheses. `RawRule` takes a whole rule in Traefik's syntax instead, and can't be combined with the other options. See [rules and priority](https://doc.traefik.io/traefik/reference/routing-configuration/http/routing/rules-and-priority/).

## Middlewares

Traefik middlewares change requests or responses on a route, for example by adding headers or rewriting the path. The common ones have their own methods:

```csharp
var defaults = traefik.AddChain("defaults",
    traefik.AddSecurityHeaders("security"),          // HSTS, X-Frame-Options, nosniff, Referrer-Policy
    traefik.AddCompress("compress"));

var cors = traefik.AddHeaders("cors", headers =>
{
    headers.AccessControlAllowOriginList.Add("https://app.example.com");
    headers.AccessControlAllowMethods.Add("GET");
});

api.WithTraefikMiddleware(defaults, cors, traefik.AddRateLimit("rate", average: 100, burst: 50));
```

| Method | Traefik middleware |
| --- | --- |
| `AddStripPrefix`, `AddStripPrefixRegex`, `AddAddPrefix`, `AddReplacePath`, `AddReplacePathRegex` | Path rewrites |
| `AddRedirectScheme`, `AddRedirectRegex` | Redirects |
| `AddHeaders`, `AddSecurityHeaders` | `headers` (custom headers, CORS, security headers) |
| `AddBasicAuth` | `basicAuth` |
| `AddIpAllowList`, `AddRateLimit`, `AddInFlightRequestLimit` | Access and load limits |
| `AddCompress`, `AddRetry`, `AddCircuitBreaker`, `AddBuffering` | Responses and resilience |
| `AddChain` | `chain` |

### Basic authentication

`AddBasicAuth` takes one parameter per user, each holding an `htpasswd` entry (`htpasswd -nbB admin <password>`). This works in run mode and when published, and the plain password stays out of the deployment. Docker Compose interpolates `$` in `.env` values, which bcrypt hashes contain, so write the value in single quotes there: `ADMIN_HTPASSWD='admin:$2y$05$...'`.

For local development, `AddBasicAuth(name, user, passwordParameter)` takes a plain password and hashes it with SHA1. It throws when publishing.

### Other middlewares

Any other middleware can be added by its Traefik type name and options:

```csharp
var headers = traefik.AddMiddleware("security-headers", "headers")
    .WithOption("frameDeny", true)
    .WithOption("customRequestHeaders.X-Script-Name", "test");

var strip = traefik.AddMiddleware("strip-v1", "stripPrefix")
    .WithOption("prefixes", ["/v1"]);

api.WithTraefikMiddleware(headers)                                    // every route of api
   .WithTraefikRoute(traefik, route =>
   {
       route.Host = ReferenceExpression.Create($"app.{domain}");
       route.PathPrefix = "/v1";
       route.Middlewares.Add(strip.Resource);                         // this route only
   });
```

- Option paths use dots for nested options, as in Traefik's label syntax. Values can be strings, booleans, numbers, lists, parameters, or expressions; at publish time parameters become `${VAR}` placeholders.
- Middlewares added with `WithTraefikMiddleware` run first, then the route's own, in the order they were added. They also apply to routes added later.
- In run mode middlewares are written to Traefik's dynamic configuration. At publish time they become `traefik.http.middlewares.*` labels on each service that uses them, so they also work with an external Traefik.
- Middlewares appear under Traefik in the dashboard.

See the [middleware reference](https://doc.traefik.io/traefik/reference/routing-configuration/http/middlewares/overview/) for the types and their options.

## Public URLs

`GetTraefikUrl()` returns the address a resource is reachable on through Traefik. In run mode the URL includes Traefik's host port, so it is correct even with randomly assigned ports. Use it for OAuth redirect URIs or public base URLs:

```csharp
web.WithEnvironment("PUBLIC_URL", web.GetTraefikUrl());
```

Configure HTTPS (`WithLetsEncrypt`) and `PublishAsExternal` before calling `GetTraefikUrl()`, because they decide the scheme.

## HTTPS with Let's Encrypt

```csharp
var email = builder.AddParameter("acme-email");

var traefik = builder.AddTraefik("traefik")
                     .WithLetsEncrypt(email)
                     .WithHttpsRedirect();
```

This adds the `websecure` entry point (port 443). At publish time, routes request certificates from Let's Encrypt (TLS-ALPN-01 by default, or HTTP-01), which are stored in a volume. Let's Encrypt can't issue certificates for a local run, because it must reach the machine on public ports 80 and 443 under a public DNS name, so in run mode Traefik serves HTTPS with its self-signed default certificate. Pass `staging: true` while testing a deployment to avoid Let's Encrypt rate limits.

The email can be a string, a parameter, or an expression such as `ReferenceExpression.Create($"admin@{domain}")`. To choose the ACME directory per deployment, or to use another ACME certificate authority, pass it as a parameter instead of `staging`:

```csharp
var caServer = builder.AddParameter("acme-ca-server", "https://acme-v02.api.letsencrypt.org/directory");

traefik.WithLetsEncrypt(email)
       .WithAcmeCaServer(caServer);
```

## Using a Traefik that already runs on the server

When the deployment target already runs Traefik with the Docker provider, use the local container only for development:

```csharp
var traefik = builder.AddTraefik("traefik")
                     .PublishAsExternal(network: "traefik", entryPoint: "websecure", certResolver: "letsencrypt");
```

At publish time no Traefik service is generated. The routed services get labels for the existing Traefik, join the `traefik` network, and the network is declared as external.

When servers name their entry point or resolver differently, pass them as expressions; parameters become `${VAR}` placeholders in the labels. The network name must be a literal, because Docker Compose doesn't substitute variables in YAML keys.

```csharp
var entryPoint = builder.AddParameter("traefik-entrypoint");

traefik.PublishAsExternal("traefik", ReferenceExpression.Create($"{entryPoint}"));
```

## Other settings

- `WithHostPort` / `WithHostHttpsPort` pin the host ports (random in run mode, 80 and 443 at publish time by default).
- `WithEntryPoint` adds more entry points, for example for TCP traffic.
- `WithDashboard` enables the Traefik dashboard. In run mode it is served without authentication and linked from the resource. At publish time it is enabled but not exposed.
- Traefik reads its static configuration from command-line arguments, so `WithArgs("--log.level=DEBUG")` adds any other setting.

## Security considerations

At publish time the managed Traefik mounts `/var/run/docker.sock` read-only so the Docker provider can read labels. Access to the Docker socket is powerful even when read-only; consider a socket proxy for internet-facing deployments.

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://doc.traefik.io/traefik/

## Feedback & contributing

https://github.com/microsoft/aspire

_*Traefik* is a trademark of *Traefik Labs*._
