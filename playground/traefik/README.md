# Traefik playground

Demonstrates `Aspire.Hosting.Traefik`: host- and path-based routing to a container and to a project, public URLs, and Traefik telemetry in the Aspire dashboard.

## Run

Prerequisites: Docker, and ports 8000 and 8443 free (or change `Traefik:WebPort` / `Traefik:WebSecurePort` in `Traefik.AppHost/appsettings.json`).

```bash
cd playground/traefik
aspire run
```

## What to try

| URL | Backend | Shows |
| --- | --- | --- |
| `http://whoami.dev.localhost:8000` | `traefik/whoami` container | Routing to a container; the echoed request shows the `X-Forwarded-*` headers Traefik adds. |
| `http://api.dev.localhost:8000` | `Traefik.Api` project | Routing to a process on the host. The response includes `PUBLIC_URL`, computed with `GetTraefikUrl()`. |
| `http://app.dev.localhost:8000/v1/hello` | `Traefik.Api` project | A second, path-based route (`PathPrefix(/v1)`) to the same project. |
| `http://app.dev.localhost:8000/api/` | `Traefik.Api` project | `StripPathPrefix`: the API sees `/`, and `X-Forwarded-Prefix: /api`. |
| `http://admin.dev.localhost:8000` | `traefik/whoami` container | Basic authentication (`admin` / `admin`, from `Parameters:admin-password`). |
| `http://secure.dev.localhost:8000` | `traefik/whoami` container | Single sign-on with Keycloak through oauth2-proxy. Sign in as `alice` / `alice`; the echoed request shows the `X-Auth-Request-*` headers. |
| `http://auth.dev.localhost:8000` | Keycloak | The Keycloak realm `traefik-demo`, imported from `Traefik.AppHost/Realms`. |
| Traefik resource → **Dashboard** link | Traefik API | The Traefik dashboard with the generated routers and services. |
| `http://traefik.dev.localhost:8000/dashboard/` | Traefik API | The same dashboard, routed on a host behind basic authentication (`admin` / `admin`). |

Middlewares:

- `whoami` (both routes) uses the `defaults` chain: security headers (`X-Frame-Options`, `X-Content-Type-Options`, `Referrer-Policy`; HSTS on HTTPS only) and compression. Check the response headers in the browser's developer tools.
- Every router on the default entry point gets the `served-by` entry-point middleware, which adds `X-Served-By: aspire-traefik` to responses.
- `api` (all routes) is rate-limited to 20 requests per second with bursts of 40; more get `429`.
- The middlewares appear under `traefik` in the Aspire dashboard and in the Traefik dashboard.

In the Aspire dashboard:

- The `whoami` and `api` resources show their public URLs; the direct `localhost` URLs are in the resource details.
- **Traces**: a request to `api.dev.localhost` is one trace with a Traefik span and an `api` span.
- **Metrics**: select `traefik` for request counts and durations per entry point, router, and service.
- **Console logs**: `traefik` logs failed requests (status 400 to 599) as access logs; open a host without a route to see one.

Browsers resolve `*.localhost` to the local machine. Other tools may not; with curl, send the host explicitly:

```bash
curl -H "Host: api.dev.localhost" http://127.0.0.1:8000/
```

## HTTPS

Set `Traefik:EnableHttps` to `true` to add the `websecure` entry point with a redirect from HTTP. In run mode Traefik uses its self-signed certificate, so the browser shows a warning. Let's Encrypt is only used when published.

To publish with one wildcard certificate for `*.${DOMAIN}` through the DNS-01 challenge, also set `Traefik:DnsProvider` (a [lego provider](https://go-acme.github.io/lego/dns/), for example `cloudflare`) and `Traefik:DnsCredentialName` (its credential variable, for example `CF_DNS_API_TOKEN`). The credential is the `DNS_CREDENTIAL` variable of the published `.env` file.

## Publish

```bash
aspire publish -o ./out
```

`out/docker-compose.yaml` contains a Traefik service using the Docker provider and `traefik.*` labels on `whoami` and `api`, with `${DOMAIN}` in the host rules. The middleware definitions are labels on the services that use them. Traefik's own configuration, the dashboard router on `traefik.${DOMAIN}` and the entry-point middleware, is the `traefik-dynamic` config. Traefik reads the Docker API through the `traefik-socket-proxy` service and doesn't mount the Docker socket itself.

When published, Keycloak and oauth2-proxy use `https://auth.${DOMAIN}` and `https://secure.${DOMAIN}`; the realm's redirect URI follows from `GetTraefikUrl()`.

When published, basic authentication takes an htpasswd entry in the `ADMIN_HTPASSWD` variable instead of a plain password. Create one with `htpasswd -nbB admin <password>`. Docker Compose interpolates `$` in `.env` values, so put it in single quotes there: `ADMIN_HTPASSWD='admin:$2y$05$...'`.
