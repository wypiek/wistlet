using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Wistlet.Infrastructure;

/// <summary>
/// Renders a content card: <c>&lt;card title="Settings"&gt;…&lt;/card&gt;</c>.
/// </summary>
[HtmlTargetElement("card")]
public class CardTagHelper : TagHelper
{
    /// <summary>Optional heading shown at the top of the card.</summary>
    public string? Title { get; set; }

    /// <summary>Extra classes for the card element.</summary>
    public string? Class { get; set; }

    /// <summary>Optional footer text (counts, dates…).</summary>
    public string? Status { get; set; }

    public string? Id { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var enc = HtmlEncoder.Default;
        output.TagName = "section";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", $"card {Class}".Trim());
        if (Id is not null) output.Attributes.SetAttribute("id", Id);

        var head = string.IsNullOrEmpty(Title) ? "" : $"<header class=\"card-head\"><h2>{enc.Encode(Title)}</h2></header>";
        output.PreContent.SetHtmlContent(head + "<div class=\"card-body\">");

        var foot = Status is null ? "" : $"<footer class=\"card-foot\">{enc.Encode(Status)}</footer>";
        output.PostContent.SetHtmlContent("</div>" + foot);
    }
}
