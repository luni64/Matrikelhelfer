using System.Windows.Automation;

namespace Matrikelhelfer.Browsers;

// Firefox exposes the urlbar as ControlType.ComboBox with a locale-dependent
// Name (e.g. German: "Mit Google suchen oder Adresse eingeben") but a stable
// AutomationId, confirmed via a live accessibility-tree dump.
class FirefoxAddressBarLocator : AddressBarLocatorBase
{
    protected override string[] ProcessNames { get; } = { "firefox" };

    protected override Condition TypeCondition { get; } =
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox);

    protected override Condition PreciseCondition { get; }

    public FirefoxAddressBarLocator()
    {
        var identifierCondition = new OrCondition(
            new PropertyCondition(AutomationElement.AutomationIdProperty, "urlbar-input"),
            new PropertyCondition(AutomationElement.NameProperty, "Search or enter address"),
            new PropertyCondition(AutomationElement.NameProperty, "Address bar"));

        PreciseCondition = new AndCondition(TypeCondition, identifierCondition);
    }
}
