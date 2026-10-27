using Microsoft.AspNetCore.Razor.TagHelpers;
using Wistlet.Services;

namespace Wistlet.Infrastructure;

/// <summary>
/// Animated profile pictures and banners (uploads saved with a still first frame): for people who
/// paused animations, the still is shown and the animation plays on hover; for everyone else the
/// still is noted in <c>data-still</c>, which site.js uses when the device asks for reduced motion.
/// </summary>
[HtmlTargetElement("img", Attributes = "src")]
public class AnimatedImageTagHelper(ViewerContext viewer) : TagHelper
{
    // After other tag helpers have settled the src.
    public override int Order => 1000;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var src = output.Attributes["src"]?.Value?.ToString();
        if (UploadService.StillFor(src) is not { } still) return;

        if ((await viewer.GetUserAsync())?.PauseAnimatedImages == true)
        {
            output.Attributes.SetAttribute("src", still);
            output.Attributes.SetAttribute("data-animated", src);
        }
        else
        {
            output.Attributes.SetAttribute("data-still", still);
        }
    }
}
