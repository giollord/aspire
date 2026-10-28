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
| Traefik resource → **Dashboard** link | Traefik API | The Traefik dashboard with the generated routers and services. |

In the Aspire dashboard:

- The `whoami` and `api` resources show their public URLs; the direct `localhost` URLs are in the resource details.
- **Traces**: a request to `api.dev.localhost` is one trace with a Traefik span and an `api` span.
- **Metrics**: select `traefik` for request counts and durations per entry point, router, and service.

Browsers resolve `*.localhost` to the local machine. Other tools may not; with curl, send the host explicitly:

```bash
curl -H "Host: api.dev.localhost" http://127.0.0.1:8000/
```

## HTTPS

Set `Traefik:EnableHttps` to `true` to add the `websecure` entry point with a redirect from HTTP. In run mode Traefik uses its self-signed certificate, so the browser shows a warning. Let's Encrypt is only used when published.

## Publish

```bash
aspire publish -o ./out
```

`out/docker-compose.yaml` contains a Traefik service using the Docker provider and `traefik.*` labels on `whoami` and `api`, with `${DOMAIN}` in the host rules.
