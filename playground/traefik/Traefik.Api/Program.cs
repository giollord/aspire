// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

var app = builder.Build();

app.MapDefaultEndpoints();

// Echo what arrived through Traefik: the original host and the X-Forwarded-* headers Traefik adds.
app.MapGet("/", (HttpRequest request) => Describe(request));
app.MapGet("/v1/{**path}", (HttpRequest request) => Describe(request));

app.Run();

static object Describe(HttpRequest request) => new
{
    PublicUrl = Environment.GetEnvironmentVariable("PUBLIC_URL"),
    Host = request.Host.Value,
    Path = request.Path.Value,
    ForwardedHeaders = request.Headers
        .Where(h => h.Key.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase))
        .ToDictionary(h => h.Key, h => h.Value.ToString()),
};
