namespace Matrikelhelfer.Models;

// A named citation format expressed as a template string with {Placeholder}
// tokens (see CitationTemplateEngine for the supported list) - kept as plain
// data rather than code so a later version can let users design and save
// their own templates the same way built-in ones work, with no separate
// code path needed. DateFormat is the id of the CitationTemplateEngine
// .DateStyle applied to the date placeholders ({Von}, {Bis}, {AccessDate});
// the default keeps formats saved before the option existed rendering
// provider dates verbatim.
public record CitationStyle(string Name, string Template, string DateFormat = "original")
{
    public override string ToString() => Name;
}
