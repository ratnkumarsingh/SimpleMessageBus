using Microsoft.AspNetCore.Components;

namespace BlazorSubscriber.Components;

/// <summary>
/// How the router shows the not-found page: .NET 10 takes the page type (NotFoundPage), .NET 9 only a
/// NotFound fragment, which here renders the same page inside the main layout.
/// </summary>
public partial class Routes
{
#if NET10_0_OR_GREATER
    private static readonly IReadOnlyDictionary<string, object> NotFoundParameters = new Dictionary<string, object>
    {
        ["NotFoundPage"] = typeof(Pages.NotFound),
    };
#else
    private static readonly IReadOnlyDictionary<string, object> NotFoundParameters = new Dictionary<string, object>
    {
        ["NotFound"] = (RenderFragment)(builder =>
        {
            builder.OpenComponent<LayoutView>(0);
            builder.AddComponentParameter(1, nameof(LayoutView.Layout), typeof(Layout.MainLayout));
            builder.AddComponentParameter(2, nameof(LayoutView.ChildContent), (RenderFragment)(page =>
            {
                page.OpenComponent<Pages.NotFound>(0);
                page.CloseComponent();
            }));
            builder.CloseComponent();
        }),
    };
#endif
}
