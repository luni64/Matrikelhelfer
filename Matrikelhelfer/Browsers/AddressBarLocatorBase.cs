using System;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace Matrikelhelfer.Browsers;

// Shared lookup strategy: try the browser's precise Name/AutomationId
// condition first, and if that doesn't match (unrecognized locale or UI
// version), fall back to whichever candidate control of the right type holds
// a URL-shaped value.
abstract class AddressBarLocatorBase : IBrowserAddressBarLocator
{
    static readonly Regex UrlShapePattern = new(
        @"^(https?://|([\w-]+\.)+[a-z]{2,}(/|$))", RegexOptions.IgnoreCase);

    protected abstract string[] ProcessNames { get; }
    protected abstract Condition TypeCondition { get; }
    protected abstract Condition PreciseCondition { get; }

    public bool HandlesProcess(string processName) =>
        Array.IndexOf(ProcessNames, processName.ToLowerInvariant()) >= 0;

    public AutomationElement? FindAddressBar(AutomationElement window)
    {
        var precise = window.FindFirst(TreeScope.Descendants, PreciseCondition);
        if (precise != null)
        {
            return precise;
        }

        var candidates = window.FindAll(TreeScope.Descendants, TypeCondition);
        foreach (AutomationElement candidate in candidates)
        {
            if (candidate.TryGetCurrentPattern(ValuePattern.Pattern, out object patternObj) &&
                UrlShapePattern.IsMatch(((ValuePattern)patternObj).Current.Value))
            {
                return candidate;
            }
        }
        return null;
    }
}
