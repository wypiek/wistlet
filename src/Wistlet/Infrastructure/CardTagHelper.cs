using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Wistlet.Infrastructure;

/// <summary>
/// Renders cohost's titled box: <c>&lt;card title="Settings"&gt;…&lt;/card&gt;</c>.
/// </summary>
[HtmlTargetElement("card")]
public class CardTagHelper : TagHelper
{
    /// <summary>Optional heading shown in the box's header strip.</summary>
    public string? Title { get; set; }

    /// <summary>Extra classes for the box element.</summary>
    public string? Class { get; set; }

    /// <summary>Optional footer text (counts, dates…).</summary>
    public string? Status { get; set; }

    public string? Id { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var enc = HtmlEncoder.Default;
        output.TagName = "section";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", $"co-titled-box {Class}".Trim());
        if (Id is not null) output.Attributes.SetAttribute("id", Id);

        var head = string.IsNullOrEmpty(Title) ? "" : $"<h2 class=\"i-header\">{enc.Encode(Title)}</h2>";
        output.PreContent.SetHtmlContent(head + "<div class=\"i-contents\">");

        var foot = Status is null ? "" : $"<footer class=\"i-footer\">{enc.Encode(Status)}</footer>";
        output.PostContent.SetHtmlContent("</div>" + foot);
    }
}
