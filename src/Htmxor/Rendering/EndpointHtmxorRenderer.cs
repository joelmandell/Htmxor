// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using Htmxor.Components;
using Htmxor.DependencyInjection;
using Htmxor.Http;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Infrastructure;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using static Htmxor.LinkerFlags;
using RouteData = Microsoft.AspNetCore.Components.RouteData;

namespace Htmxor.Rendering;

/// <summary>
/// A <see cref="StaticHtmxorRenderer"/> subclass which is also the implementation of the
/// <see cref="IComponentPrerenderer"/> DI service. This is the underlying mechanism shared by:
///
/// * Html.RenderComponentAsync (the earliest prerendering mechanism - a Razor HTML helper)
/// * ComponentTagHelper (the primary prerendering mechanism before .NET 8)
/// * RazorComponentResult and RazorComponentEndpoint (the primary prerendering mechanisms since .NET 8)
///
/// EndpointHtmlRenderer wraps the underlying <see cref="Htmxor.Rendering.HtmxorRenderer"/> mechanism, annotating the
/// output with prerendering markers so the content can later switch into interactive mode when used with
/// blazor.*.js. It also deals with initializing the standard component DI services once per request.
/// </summary>
internal partial class EndpointHtmxorRenderer : StaticHtmxorRenderer, IComponentPrerenderer
{
    private readonly static Type httpContextFormDataProviderType;
    private readonly IServiceProvider _services;
    private readonly RazorComponentsServiceOptions _options;
    private Task? _servicesInitializedTask;
    private HttpContext _httpContext = default!; // Always set at the start of an inbound call

    // The underlying Renderer always tracks the pending tasks representing *full* quiescence, i.e.,
    // when everything (regardless of streaming SSR) is fully complete. In this subclass we also track
    // the subset of those that are from the non-streaming subtrees, since we want the response to
    // wait for the non-streaming tasks (these ones), then start streaming until full quiescence.
    private readonly List<Task> _nonStreamingPendingTasks = new();

    static EndpointHtmxorRenderer()
    {
        httpContextFormDataProviderType = AppDomain
            .CurrentDomain
            .GetAssemblies()
            .SelectMany(assembly => assembly.GetTypes())
            .First(t => t.FullName == "Microsoft.AspNetCore.Components.Endpoints.HttpContextFormDataProvider");
    }

    public EndpointHtmxorRenderer(IServiceProvider serviceProvider, ILoggerFactory loggerFactory)
        : base(serviceProvider, loggerFactory)
    {
        _services = serviceProvider;
        _options = serviceProvider.GetRequiredService<IOptions<RazorComponentsServiceOptions>>().Value;
    }

    internal HttpContext? HttpContext => _httpContext;

    private void SetHttpContext(HttpContext httpContext)
    {
        if (_httpContext is null)
        {
            _httpContext = httpContext;
        }
        else if (_httpContext != httpContext)
        {
            throw new InvalidOperationException("The HttpContext cannot change value once assigned.");
        }
    }

    internal static async Task InitializeStandardComponentServicesAsync(
        HttpContext httpContext,
        [DynamicallyAccessedMembers(Component)] Type? componentType = null,
        [DynamicallyAccessedMembers(Component)] Type? layoutType = null,
        string? handler = null,
        IFormCollection? form = null)
    {
        var navigationManager = (IHostEnvironmentNavigationManager)httpContext.RequestServices.GetRequiredService<NavigationManager>();
        navigationManager?.Initialize(GetContextBaseUri(httpContext.Request), GetFullUri(httpContext.Request));

        if (httpContext.RequestServices.GetService<AuthenticationStateProvider>() is IHostEnvironmentAuthenticationStateProvider authenticationStateProvider)
        {
            var authenticationState = new AuthenticationState(httpContext.User);
            authenticationStateProvider.SetAuthenticationState(Task.FromResult(authenticationState));
        }

        if (form is not null)
        {
            // This code has been replaced by the reflection based code below it.
            //httpContext
            //    .RequestServices
            //    .GetRequiredService<HttpContextFormDataProvider>()
            //    .SetFormData(handler ?? "", new FormCollectionReadOnlyDictionary(form), form.Files);

            var httpContextFormDataProvider = httpContext
                .RequestServices
                .GetService(httpContextFormDataProviderType);
            httpContextFormDataProviderType
                .GetMethod("SetFormData", BindingFlags.Instance | BindingFlags.Public)!
                .Invoke(httpContextFormDataProvider, [handler ?? "", new FormCollectionReadOnlyDictionary(form), form.Files]);
        }

        var antiforgery = httpContext.RequestServices.GetRequiredService<AntiforgeryStateProvider>();
        if (antiforgery.GetType().GetMethod("SetRequestContext", BindingFlags.Instance | BindingFlags.NonPublic) is MethodInfo setRequestContextMethod)
        {
            setRequestContextMethod.Invoke(antiforgery, [httpContext]);
        }

        // It's important that this is initialized since a component might try to restore state during prerendering
        // (which will obviously not work, but should not fail)
        var componentApplicationLifetime = httpContext.RequestServices.GetRequiredService<ComponentStatePersistenceManager>();
        await componentApplicationLifetime.RestoreStateAsync(new PrerenderComponentApplicationStore());

        if (componentType is not null)
        {
            SetRouteData(httpContext, componentType, layoutType);
        }
    }

    protected internal override void WriteComponentHtml(int componentId, TextWriter output)
    {
        var htmxContext = _httpContext.GetHtmxContext();
        if (htmxContext.Request.IsHtmxRequest && !htmxContext.Request.IsBoosted)
        {
            var matchingPartialComponentId = FindPartialComponentMatchingRequest(componentId);
            base.WriteComponentHtml(
                matchingPartialComponentId.HasValue ? matchingPartialComponentId.Value : componentId,
                output);
        }
        else
        {
            base.WriteComponentHtml(componentId, output);
        }
    }

    private int? FindPartialComponentMatchingRequest(int componentId)
    {
        var frames = GetCurrentRenderTreeFrames(componentId);

        for (int i = 0; i < frames.Count; i++)
        {
            ref var frame = ref frames.Array[i];

            if (frame.FrameType is RenderTreeFrameType.Component)
            {
                if (frame.Component is HtmxPartial partial)
                {
                    if (partial.ShouldRender())
                    {
                        return frame.ComponentId;
                    }
                    else
                    {
                        // if the partial should not render, none of it children should render either.
                        continue;
                    }
                }

                var candidate = FindPartialComponentMatchingRequest(frame.ComponentId);

                if (candidate.HasValue)
                {
                    return candidate.Value;
                }
            }
        }

        return null;
    }

    private static void SetRouteData(HttpContext httpContext, Type componentType, Type? layoutType)
    {
        // Saving RouteData to avoid routing twice in Router component
        var routingStateProvider = httpContext.RequestServices.GetRequiredService<EndpointRoutingStateProvider>();
        routingStateProvider.LayoutType = layoutType;
        routingStateProvider.RouteData = new RouteData(componentType, httpContext.GetRouteData().Values);
        if (httpContext.GetEndpoint() is RouteEndpoint endpoint)
        {
            routingStateProvider.RoutePattern = endpoint.RoutePattern;
            routingStateProvider.RouteData.Template = endpoint.RoutePattern.RawText;
        }
    }

    protected override ComponentState CreateComponentState(int componentId, IComponent component, ComponentState? parentComponentState)
    {
        return new HtmxorComponentState(this, componentId, component, parentComponentState);
    }

    protected override void AddPendingTask(ComponentState? componentState, Task task)
    {
        var streamRendering = componentState is null
            ? false
            : ((HtmxorComponentState)componentState).StreamRendering;

        if (!streamRendering)
        {
            _nonStreamingPendingTasks.Add(task);
        }

        // We still need to determine full quiescence, so always let the base renderer track this task too
        base.AddPendingTask(componentState, task);
    }

    // For tests only
    internal Task? NonStreamingPendingTasksCompletion;

    protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
    {
        UpdateHtmxorEvents(in renderBatch);
        UpdateNamedSubmitEvents(in renderBatch);
        return base.UpdateDisplayAsync(renderBatch);
    }

    private static string GetFullUri(HttpRequest request)
    {
        return UriHelper.BuildAbsolute(
            request.Scheme,
            request.Host,
            request.PathBase,
            request.Path,
            request.QueryString);
    }

    private static string GetContextBaseUri(HttpRequest request)
    {
        var result = UriHelper.BuildAbsolute(request.Scheme, request.Host, request.PathBase);

        // PathBase may be "/" or "/some/thing", but to be a well-formed base URI
        // it has to end with a trailing slash
        return result.EndsWith('/') ? result : result += "/";
    }

    private sealed class FormCollectionReadOnlyDictionary : IReadOnlyDictionary<string, StringValues>
    {
        private readonly IFormCollection _form;
        private List<StringValues>? _values;

        public FormCollectionReadOnlyDictionary(IFormCollection form)
        {
            _form = form;
        }

        public StringValues this[string key] => _form[key];

        public IEnumerable<string> Keys => _form.Keys;

        public IEnumerable<StringValues> Values => _values ??= MaterializeValues(_form);

        private static List<StringValues> MaterializeValues(IFormCollection form)
        {
            var result = new List<StringValues>(form.Keys.Count);
            foreach (var key in form.Keys)
            {
                result.Add(form[key]);
            }

            return result;
        }

        public int Count => _form.Count;

        public bool ContainsKey(string key)
        {
            return _form.ContainsKey(key);
        }

        public IEnumerator<KeyValuePair<string, StringValues>> GetEnumerator()
        {
            return _form.GetEnumerator();
        }

        public bool TryGetValue(string key, [MaybeNullWhen(false)] out StringValues value)
        {
            return _form.TryGetValue(key, out value);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return _form.GetEnumerator();
        }
    }
}
