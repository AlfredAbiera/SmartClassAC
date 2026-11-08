using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartClassAC.Shared;
using Xunit;

namespace SmartClassAC.Tests;

public sealed class AutoDismissTests
{
    [Fact]
    public async Task RepeatedMessagesGetTheirOwnFiveSecondsAndCanBeDismissed()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<NotificationHarness>());
        var harness = await renderer.Dispatcher.InvokeAsync(() => NotificationHarness.LastInstance!);
        await renderer.Dispatcher.InvokeAsync(() => harness.Show("First"));
        await Task.Delay(TimeSpan.FromSeconds(3));
        await renderer.Dispatcher.InvokeAsync(() => harness.Show("Second"));
        await Task.Delay(TimeSpan.FromSeconds(2.4));
        Assert.Contains("Second", await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        await Task.Delay(TimeSpan.FromSeconds(2.9));
        Assert.DoesNotContain("Second", await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
        await renderer.Dispatcher.InvokeAsync(() => harness.Show("Dismiss me"));
        await renderer.Dispatcher.InvokeAsync(harness.Dismiss);
        Assert.DoesNotContain("Dismiss me", await renderer.Dispatcher.InvokeAsync(root.ToHtmlString));
    }

    public sealed class NotificationHarness : ComponentBase
    {
        public static NotificationHarness? LastInstance { get; private set; }
        private string message = "";
        private long version;

        protected override void OnInitialized() => LastInstance = this;
        public void Show(string value) { message = value; version++; StateHasChanged(); }
        public void Dismiss() { message = ""; version++; StateHasChanged(); }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<AutoDismiss>(0);
            builder.AddAttribute(1, nameof(AutoDismiss.Active), message.Length > 0);
            builder.AddAttribute(2, nameof(AutoDismiss.Version), version);
            builder.AddAttribute(3, nameof(AutoDismiss.Dismissed), EventCallback.Factory.Create<long>(this, (long dismissed) =>
            {
                if (dismissed == version) Dismiss();
            }));
            builder.AddAttribute(4, nameof(AutoDismiss.ChildContent), (RenderFragment)(child => child.AddContent(0, message)));
            builder.CloseComponent();
        }
    }
}
