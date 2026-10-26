using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Wistlet.Services;

namespace Wistlet.Infrastructure;

/// <summary>Marks a page as always reachable by guests, even when public browsing is turned off.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AllowGuestsAttribute : Attribute;

/// <summary>When the "public browsing" site setting is off, guests are sent to the login page.</summary>
public class GuestAccessFilter(SiteSettingsService settings) : IAsyncPageFilter
{
    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (!settings.Current.PublicBrowsing &&
            http.User.Identity?.IsAuthenticated != true &&
            context.ActionDescriptor.ModelTypeInfo?.IsDefined(typeof(AllowGuestsAttribute), true) != true)
        {
            var returnUrl = http.Request.Path + http.Request.QueryString;
            context.Result = new RedirectResult($"/account/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
            return;
        }
        await next();
    }
}
