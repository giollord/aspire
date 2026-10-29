// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// A tree of Traefik dynamic configuration options, such as the options of a middleware.
/// </summary>
/// <remarks>
/// <para>
/// Options are set by dotted paths, which is also how the Docker provider spells them in labels. For a
/// <c>headers</c> middleware:
/// <code>
/// customRequestHeaders.X-Script-Name = test
/// accessControlAllowMethods          = [GET, OPTIONS]
/// stsSeconds                         = 31536000
/// </code>
/// </para>
/// <para>
/// A value is a <see cref="string"/>, <see cref="bool"/>, <see cref="long"/>, <see cref="ReferenceExpression"/>, or a
/// list of strings and expressions. The same tree renders to YAML for the file provider (run mode) and to flat labels
/// for the Docker provider (publish).
/// </para>
/// </remarks>
internal sealed partial class TraefikOptionMap
{
    // Traefik matches option names case-insensitively in every provider, so `prefixes` and `Prefixes` are one key.
    private readonly OrderedDictionary<string, object> _entries = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _entries.Count;

    public IEnumerable<KeyValuePair<string, object>> Entries => _entries;

    /// <summary>
    /// Sets the value at a dotted path, creating the maps on the way. Setting a path again replaces the value.
    /// </summary>
    /// <exception cref="ArgumentException">The path is invalid, or it conflicts with a value set before.</exception>
    public void Set(string path, object value)
    {
        var segments = path.Split('.');
        if (segments.Any(s => !PathSegmentRegex().IsMatch(s)))
        {
            throw new ArgumentException($"The option path '{path}' is invalid. Use names of letters, digits, '-' and '_', separated by '.'.", nameof(path));
        }

        var map = this;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (!map._entries.TryGetValue(segments[i], out var existing))
            {
                existing = new TraefikOptionMap();
                map._entries[segments[i]] = existing;
            }

            map = existing as TraefikOptionMap
                ?? throw new ArgumentException($"The option path '{path}' conflicts with the value already set at '{string.Join('.', segments[..(i + 1)])}'.", nameof(path));
        }

        var key = segments[^1];
        if (map._entries.TryGetValue(key, out var current) && current is TraefikOptionMap)
        {
            throw new ArgumentException($"The option path '{path}' conflicts with the options already set below it.", nameof(path));
        }

        map._entries[key] = value;
    }

    /// <summary>
    /// Gets every expression in the tree, including those in lists.
    /// </summary>
    public IEnumerable<ReferenceExpression> GetExpressions()
    {
        foreach (var (_, value) in _entries)
        {
            switch (value)
            {
                case ReferenceExpression expression:
                    yield return expression;
                    break;
                case IReadOnlyList<object> list:
                    foreach (var expression in list.OfType<ReferenceExpression>())
                    {
                        yield return expression;
                    }
                    break;
                case TraefikOptionMap map:
                    foreach (var expression in map.GetExpressions())
                    {
                        yield return expression;
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Renders the tree for the file provider, resolving expressions from Traefik's point of view.
    /// </summary>
    public async Task<Dictionary<string, object>> ToFileConfigurationAsync(ValueProviderContext context, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, object>(_entries.Count);

        foreach (var (key, value) in _entries)
        {
            result[key] = value switch
            {
                TraefikOptionMap map => await map.ToFileConfigurationAsync(context, cancellationToken).ConfigureAwait(false),
                IReadOnlyList<object> list => await ResolveListAsync(list, context, cancellationToken).ConfigureAwait(false),
                ReferenceExpression expression => await expression.GetValueAsync(context, cancellationToken).ConfigureAwait(false) ?? string.Empty,
                _ => value,
            };
        }

        return result;

        static async Task<List<string>> ResolveListAsync(IReadOnlyList<object> list, ValueProviderContext context, CancellationToken cancellationToken)
        {
            var items = new List<string>(list.Count);
            foreach (var item in list)
            {
                items.Add(item is ReferenceExpression expression
                    ? await expression.GetValueAsync(context, cancellationToken).ConfigureAwait(false) ?? string.Empty
                    : (string)item);
            }

            return items;
        }
    }

    /// <summary>
    /// Renders the tree as Docker labels under <paramref name="prefix"/>.
    /// </summary>
    /// <remarks>
    /// Nested keys are joined with dots and lists with commas, as the Docker provider reads them:
    /// <code>
    /// traefik.http.middlewares.strip.stripPrefix.prefixes: /v1,/v2
    /// traefik.http.middlewares.sec.headers.customRequestHeaders.X-Script-Name: test
    /// </code>
    /// See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/other-providers/docker/"/>.
    /// </remarks>
    /// <param name="labels">The labels to add to.</param>
    /// <param name="prefix">The label key of the tree, for example <c>traefik.http.middlewares.strip.stripPrefix</c>.</param>
    /// <param name="renderExpression">Renders an expression for Docker Compose.</param>
    public void AddLabels(IDictionary<string, string> labels, string prefix, Func<ReferenceExpression, string> renderExpression)
    {
        foreach (var (key, value) in _entries)
        {
            var label = $"{prefix}.{key}";

            if (value is TraefikOptionMap map)
            {
                map.AddLabels(labels, label, renderExpression);
                continue;
            }

            labels[label] = value switch
            {
                IReadOnlyList<object> list => string.Join(",", list.Select(item => ToListItem(label, item, renderExpression))),
                _ => ToLabelValue(value, renderExpression),
            };
        }

        static string ToListItem(string label, object item, Func<ReferenceExpression, string> renderExpression)
        {
            // The Docker provider splits list values on commas, and there is no escape for a comma inside an item.
            if (item is string text && text.Contains(','))
            {
                throw new InvalidOperationException($"The value '{text}' of the Traefik option '{label}' contains ',', which Docker labels use to separate list items.");
            }

            return ToLabelValue(item, renderExpression);
        }
    }

    /// <summary>
    /// Gets the expressions in the tree that reference an endpoint, such as a forward authentication address, in a
    /// stable order.
    /// </summary>
    public IEnumerable<ReferenceExpression> GetEndpointExpressions() => GetExpressions().Where(ReferencesEndpoint);

    /// <summary>
    /// Gets the resources whose endpoints the tree references.
    /// </summary>
    public IEnumerable<IResource> GetReferencedResources() =>
        GetEndpointExpressions().SelectMany(GetEndpointResources).Distinct();

    internal static bool ReferencesEndpoint(ReferenceExpression expression) => GetEndpointResources(expression).Any();

    private static IEnumerable<IResource> GetEndpointResources(ReferenceExpression expression) =>
        expression.ValueProviders.SelectMany(provider => provider switch
        {
            EndpointReference endpoint => [endpoint.Resource],
            EndpointReferenceExpression property => [property.Endpoint.Resource],
            ReferenceExpression nested => GetEndpointResources(nested),
            _ => Enumerable.Empty<IResource>(),
        });

    private static string ToLabelValue(object value, Func<ReferenceExpression, string> renderExpression) => value switch
    {
        string text => TraefikDockerLabels.EscapeLiteral(text),
        bool flag => flag ? "true" : "false",
        long number => number.ToString(CultureInfo.InvariantCulture),
        ReferenceExpression expression => renderExpression(expression),
        _ => throw new InvalidOperationException($"Unsupported Traefik option value '{value}'."),
    };

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex PathSegmentRegex();
}
