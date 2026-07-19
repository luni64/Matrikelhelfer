using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Matrikelhelfer.Browsers;

namespace Matrikelhelfer.Services;

// A user-selected browser process this app is allowed to read from. Nothing
// happens until Connect() is called, and every read/navigate afterward is a
// single on-demand operation triggered by a button click - no background
// watching of any kind, unlike the WinEventHook-based BrowserWatcher this
// replaced.
class BrowserConnection
{
    static readonly IBrowserAddressBarLocator[] Locators =
    {
        new ChromiumAddressBarLocator(),
        new FirefoxAddressBarLocator(),
    };

    // Process names are the lowercase executable stem (e.g. "msedge"), not
    // fit for display - map to the name a user would recognize.
    static readonly Dictionary<string, string> DisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = "Chrome",
        ["msedge"] = "Edge",
        ["brave"] = "Brave",
        ["opera"] = "Opera",
        ["vivaldi"] = "Vivaldi",
        ["iexplore"] = "Internet Explorer",
        ["firefox"] = "Firefox",
    };

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    static extern int GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    int? _connectedPid;

    public string? ConnectedLabel { get; private set; }
    public bool IsConnected => _connectedPid.HasValue;

    // One EnumWindows pass, keeping only the first (topmost, since
    // EnumWindows returns windows in Z-order) visible window per process.
    // Chromium browsers run many same-named subprocesses (one per
    // tab/site-isolation renderer, plus GPU/utility processes) that never
    // own a visible top-level window, so this naturally collapses each
    // running browser instance down to exactly the one process a user would
    // recognize as "Chrome is open" - no need to filter those out
    // separately.
    public IReadOnlyList<BrowserCandidate> ListRunningBrowsers()
    {
        var seenPids = new HashSet<int>();
        var result = new List<BrowserCandidate>();
        foreach (var (_, pid) in EnumerateVisibleTopLevelWindows())
        {
            if (!seenPids.Add(pid))
            {
                continue;
            }
            Process process;
            try
            {
                process = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                continue;
            }
            string processName = process.ProcessName;
            if (!Locators.Any(l => l.HandlesProcess(processName)))
            {
                continue;
            }
            string displayName = DisplayNames.TryGetValue(processName, out var name) ? name : processName;
            result.Add(new BrowserCandidate(pid, processName, displayName, ExtractIcon(process)));
        }
        return result;
    }

    // Pulls the icon straight from the running executable - the same one
    // Explorer/Task Manager would show - rather than bundling per-browser
    // image assets to keep in sync (and sidesteps any question of bundling
    // third-party trademarked logos).
    static ImageSource? ExtractIcon(Process process)
    {
        try
        {
            string? exePath = process.MainModule?.FileName;
            if (exePath == null)
            {
                return null;
            }
            using var icon = Icon.ExtractAssociatedIcon(exePath);
            if (icon == null)
            {
                return null;
            }
            var bitmapSource = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmapSource.Freeze();
            return bitmapSource;
        }
        catch (Exception)
        {
            // Best-effort - e.g. access denied reading MainModule of an
            // elevated process shouldn't stop the browser from being listed.
            return null;
        }
    }

    public void Connect(BrowserCandidate candidate)
    {
        _connectedPid = candidate.Pid;
        ConnectedLabel = candidate.ToString();
    }

    public void Disconnect()
    {
        _connectedPid = null;
        ConnectedLabel = null;
    }

    // Synchronous: reading a UIA value doesn't need real OS focus, unlike
    // TryNavigateAsync's keyboard input below.
    public string? ReadActiveTabUrl()
    {
        var resolved = ResolveConnectedAddressBar();
        if (resolved == null)
        {
            return null;
        }
        var (_, addressBar) = resolved.Value;
        return addressBar.TryGetCurrentPattern(ValuePattern.Pattern, out object patternObj)
            ? ((ValuePattern)patternObj).Current.Value
            : null;
    }

    // The connected window's title = the active tab's page title, i.e. what
    // the browser actually RENDERED - so it reflects the logged-in page,
    // unlike this app's cookie-less HTTP fetch. Used by providers whose
    // citation data isn't in the URL and whose page HTML is login-gated:
    // ARCHION serves the breadcrumb only to a session, but puts that same
    // chain in the <title>, which the window title mirrors. Synchronous, like
    // ReadActiveTabUrl - a plain Win32 text read, no focus needed.
    public string? ReadActiveTabTitle()
    {
        if (_connectedPid is not int pid)
        {
            return null;
        }
        var hwnd = FindTopWindowForPid(pid);
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }
        int len = GetWindowTextLength(hwnd);
        if (len <= 0)
        {
            return null;
        }
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    // Walks the connected window's UI Automation tree for the first hyperlink
    // or text field whose text matches `pattern`, returning the matched
    // substring. Used to pull a link out of the RENDERED page that the URL
    // never carries - ARCHION's page permalink, shown only in its (user-opened)
    // permalink panel. Deliberately fragile and best-effort: it depends on the
    // panel being open and on the browser exposing the link in its
    // accessibility tree, and it is comparatively slow (a whole-window scan),
    // so only call it when a provider genuinely needs it. Returns null on no
    // match or any UIA error.
    public string? FindPageLinkMatching(Regex pattern)
    {
        if (_connectedPid is not int pid)
        {
            return null;
        }
        var hwnd = FindTopWindowForPid(pid);
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            // Hyperlinks and edit fields only - a permalink is rendered as one
            // or the other, and both are few per page (unlike generic Text,
            // which every text node would swell into and slow the scan).
            var condition = new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            foreach (AutomationElement element in root.FindAll(TreeScope.Descendants, condition))
            {
                foreach (string candidate in ElementTexts(element))
                {
                    var match = pattern.Match(candidate);
                    if (match.Success)
                    {
                        return match.Value;
                    }
                }
            }
        }
        catch (Exception)
        {
            // Best-effort automation of an external process: the window/tree
            // may change under us, elements may go stale, etc.
        }
        return null;
    }

    // A link's URL shows up as the element's Name (hyperlinks) or its
    // ValuePattern value (edit fields) depending on how the page renders it.
    static IEnumerable<string> ElementTexts(AutomationElement element)
    {
        string? name = null;
        try { name = element.Current.Name; } catch (ElementNotAvailableException) { }
        if (!string.IsNullOrEmpty(name))
        {
            yield return name;
        }
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object patternObj))
        {
            string? value = null;
            try { value = ((ValuePattern)patternObj).Current.Value; } catch (ElementNotAvailableException) { }
            if (!string.IsNullOrEmpty(value))
            {
                yield return value;
            }
        }
    }

    // Navigates the connected browser's active window to a URL in place:
    // brings it forward, focuses the address bar via the browser's own
    // Ctrl+L shortcut (AutomationElement.SetFocus() isn't reliable on
    // Chromium's custom-rendered omnibox), sets its value, and submits with
    // a simulated Enter.
    public async Task<bool> TryNavigateAsync(string url)
    {
        var resolved = ResolveConnectedAddressBar();
        if (resolved == null)
        {
            return false;
        }
        var (hwnd, addressBar) = resolved.Value;

        try
        {
            SetForegroundWindow(hwnd);

            // SetForegroundWindow's window-activation swap is asynchronous;
            // sending keystrokes before it actually completes means they can
            // go to whatever window was foreground previously. Poll instead
            // of a fixed delay.
            for (int i = 0; i < 20 && GetForegroundWindow() != hwnd; i++)
            {
                await Task.Delay(25);
            }

            NativeInput.SendChord(NativeInput.VkControl, NativeInput.VkL);
            await Task.Delay(150);

            if (!addressBar.TryGetCurrentPattern(ValuePattern.Pattern, out object patternObj))
            {
                return false;
            }
            ((ValuePattern)patternObj).SetValue(url);
            await Task.Delay(150);

            NativeInput.SendKey(NativeInput.VkReturn);
            return true;
        }
        catch (Exception)
        {
            // Best-effort UI automation of an external process: the window
            // or address bar element may have gone away in the meantime.
            return false;
        }
    }

    // Resolves the connected pid down to its active window's address bar,
    // shared by ReadActiveTabUrl and TryNavigateAsync. Disconnects
    // automatically if the connected process has exited, since a stale pid
    // can otherwise get silently reused by an unrelated later process.
    (IntPtr Hwnd, AutomationElement AddressBar)? ResolveConnectedAddressBar()
    {
        if (_connectedPid is not int pid)
        {
            return null;
        }

        string processName;
        try
        {
            processName = Process.GetProcessById(pid).ProcessName;
        }
        catch (ArgumentException)
        {
            Disconnect();
            return null;
        }

        var hwnd = FindTopWindowForPid(pid);
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        var locator = Array.Find(Locators, l => l.HandlesProcess(processName));
        if (locator == null)
        {
            return null;
        }

        try
        {
            var addressBar = locator.FindAddressBar(AutomationElement.FromHandle(hwnd));
            return addressBar != null ? (hwnd, addressBar) : null;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    // First (topmost in Z-order) visible top-level window belonging to the
    // given process - the same "active window" resolution used to build the
    // connect list above.
    static IntPtr FindTopWindowForPid(int pid)
    {
        foreach (var (hwnd, windowPid) in EnumerateVisibleTopLevelWindows())
        {
            if (windowPid == pid)
            {
                return hwnd;
            }
        }
        return IntPtr.Zero;
    }

    // GetWindowTextLength > 0 is used as a "is this a real top-level window,
    // not a background/tool window" filter - the title itself isn't kept,
    // since candidates are displayed by browser name only.
    static IEnumerable<(IntPtr Hwnd, int Pid)> EnumerateVisibleTopLevelWindows()
    {
        var windows = new List<(IntPtr, int)>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindowTextLength(hwnd) == 0)
            {
                return true;
            }
            GetWindowThreadProcessId(hwnd, out int pid);
            windows.Add((hwnd, pid));
            return true;
        }, IntPtr.Zero);
        return windows;
    }
}

public record BrowserCandidate(int Pid, string ProcessName, string DisplayName, ImageSource? Icon)
{
    public override string ToString() => DisplayName;
}
