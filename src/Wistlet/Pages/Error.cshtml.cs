using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Wistlet.Infrastructure;

namespace Wistlet.Pages;

[AllowGuests]
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public class ErrorModel : PageModel
{
    public int Code { get; private set; } = 500;
    public string? RequestId { get; private set; }

    public void OnGet(int? code) => Load(code);
    public void OnPost(int? code) => Load(code);

    private void Load(int? code)
    {
        Code = code ?? 500;
        RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
    }

    public (string Title, string Message) Describe() => Code switch
    {
        404 => ("Page not found", "The page you requested doesn't exist. Check the address and try again."),
        403 => ("Access denied", "You don't have permission to open this page."),
        429 => ("Slow down", "You're doing that too often. Wait a minute and try again."),
        400 => ("Bad request", "The request couldn't be understood. Try reloading the page."),
        _ => ("Something went wrong", "Something went wrong on our side. Please try again in a moment."),
    };
}
