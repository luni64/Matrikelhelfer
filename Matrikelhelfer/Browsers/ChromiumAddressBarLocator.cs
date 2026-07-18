using System.Windows.Automation;

namespace Matrikelhelfer.Browsers;

// Chrome, Edge, Brave, Opera, Vivaldi (and legacy IE) are all Chromium/Trident
// derivatives that expose the omnibox as ControlType.Edit.
class ChromiumAddressBarLocator : AddressBarLocatorBase
{
    protected override string[] ProcessNames { get; } =
        { "chrome", "msedge", "brave", "opera", "vivaldi", "iexplore" };

    protected override Condition TypeCondition { get; } =
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);

    protected override Condition PreciseCondition { get; }

    public ChromiumAddressBarLocator()
    {
        var identifierCondition = new OrCondition(
            new PropertyCondition(AutomationElement.NameProperty, "Address and search bar"),
            new PropertyCondition(AutomationElement.NameProperty, "Address and search using Bing"));

        PreciseCondition = new AndCondition(TypeCondition, identifierCondition);
    }
}
