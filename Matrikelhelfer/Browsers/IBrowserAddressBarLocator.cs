using System.Windows.Automation;

namespace Matrikelhelfer.Browsers;

interface IBrowserAddressBarLocator
{
    bool HandlesProcess(string processName);

    AutomationElement? FindAddressBar(AutomationElement window);
}
