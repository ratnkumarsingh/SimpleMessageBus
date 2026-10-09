using Microsoft.AspNetCore.Components;

namespace MessageBroker.Dashboard.Components;

/// <summary>
/// The parts of the page shell that only .NET 10 has. On .NET 9 both are empty: there is no resource
/// preloader, and Blazor shows its built-in reconnect overlay, because the custom modal's script relies on
/// .NET 10's reconnect events and Blazor.resumeCircuit.
/// </summary>
public partial class App
{
#if NET10_0_OR_GREATER
    private static readonly RenderFragment Preloader = builder =>
    {
        builder.OpenComponent<ResourcePreloader>(0);
        builder.CloseComponent();
    };

    private static readonly RenderFragment Reconnect = builder =>
    {
        builder.OpenComponent<Layout.ReconnectModal>(0);
        builder.CloseComponent();
    };
#else
    private static readonly RenderFragment? Preloader = null;
    private static readonly RenderFragment? Reconnect = null;
#endif
}
