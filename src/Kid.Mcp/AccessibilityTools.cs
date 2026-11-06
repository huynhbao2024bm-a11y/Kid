using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Kid.Mcp;

[McpServerToolType]
public static partial class AccessibilityTools
{
    private const int DefaultElementLimit = 120;
    private const int MaximumElementLimit = 300;
    private const int MaximumTextLength = 2_000;
    private const int MaximumTreeDepth = 12;

    [McpServerTool(Name = "list_open_windows", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("List visible top-level windows exposed by Windows UI Automation, including title, process ID, and native window handle.")]
    public static IReadOnlyList<OpenWindow> ListOpenWindows()
    {
        var windows = AutomationElement.RootElement.FindAll(
            TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));

        var foregroundHandle = NativeMethods.GetForegroundWindow();
        var results = new List<OpenWindow>(windows.Count);
        for (var index = 0; index < windows.Count; index++)
        {
            var window = windows[index];
            var current = window.Current;
            if (current.IsOffscreen || current.NativeWindowHandle == 0)
            {
                continue;
            }

            results.Add(new OpenWindow(
                Truncate(current.Name) ?? string.Empty,
                current.ProcessId,
                current.NativeWindowHandle,
                current.NativeWindowHandle == foregroundHandle.ToInt32()));
        }

        return results;
    }

    [McpServerTool(Name = "focus_window")]
    [Description("Bring a visible top-level window to the foreground using its native window handle from list_open_windows.")]
    public static string FocusWindow(
        [Description("Native window handle returned by list_open_windows.")] int nativeWindowHandle)
    {
        if (nativeWindowHandle == 0)
        {
            throw new McpException("Window handle must not be zero.");
        }

        var windows = AutomationElement.RootElement.FindAll(
            TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
        var isVisibleWindow = false;
        for (var index = 0; index < windows.Count; index++)
        {
            var current = windows[index].Current;
            if (current.NativeWindowHandle == nativeWindowHandle && !current.IsOffscreen)
            {
                isVisibleWindow = true;
                break;
            }
        }

        if (!isVisibleWindow)
        {
            throw new McpException("The handle does not identify a visible top-level window.");
        }

        var requested = new IntPtr(nativeWindowHandle);
        if (NativeMethods.GetForegroundWindow() != requested)
        {
            NativeMethods.SetForegroundWindow(requested);
        }

        if (!SpinWait.SpinUntil(() => NativeMethods.GetForegroundWindow() == requested, 350))
        {
            throw new McpException("Foreground focus was not observed. Re-list windows before acting or retrying.");
        }

        return "Focused window.";
    }

    [McpServerTool(Name = "read_active_window", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read the foreground window's accessibility tree using Windows UI Automation. Includes accessible control text but never reads password fields. Returns truncation and transient-element counts.")]
    public static WindowSnapshot ReadActiveWindow(
        [Description("Maximum number of elements to return (1-300).")] int maxElements = DefaultElementLimit)
    {
        ValidateElementLimit(maxElements);

        var root = GetForegroundWindow();
        var window = root.Current;
        var results = new List<AccessibleElement>();
        var pending = new Stack<(AutomationElement Element, int Depth)>();
        var wasTruncated = false;
        var skippedUnavailableElements = 0;

        results.Add(ReadElement(root, 0));
        var childResult = AddChildren(root, 1, pending, maxElements - results.Count);
        wasTruncated |= childResult.WasTruncated;
        skippedUnavailableElements += childResult.SkippedUnavailableElements;

        while (pending.Count > 0 && results.Count < maxElements)
        {
            var (element, depth) = pending.Pop();
            try
            {
                results.Add(ReadElement(element, depth));
            }
            catch (ElementNotAvailableException)
            {
                skippedUnavailableElements++;
                continue;
            }

            if (depth < MaximumTreeDepth)
            {
                childResult = AddChildren(element, depth + 1, pending, maxElements - results.Count - pending.Count);
                wasTruncated |= childResult.WasTruncated;
                skippedUnavailableElements += childResult.SkippedUnavailableElements;
            }
            else if (HasChildren(element, ref skippedUnavailableElements))
            {
                wasTruncated = true;
            }
        }

        if (pending.Count > 0)
        {
            wasTruncated = true;
        }

        return new WindowSnapshot(
            Truncate(window.Name) ?? string.Empty,
            window.ProcessId,
            window.NativeWindowHandle,
            maxElements,
            wasTruncated,
            skippedUnavailableElements,
            results);
    }

    [McpServerTool(Name = "focus_accessible_element")]
    [Description("Focus one uniquely matching accessible element in the foreground window using its exact accessible name or automation ID.")]
    public static string FocusAccessibleElement(
        [Description("Exact accessible name. May be omitted when automationId is provided.")] string? name = null,
        [Description("Exact UI Automation ID; useful when names are duplicated.")] string? automationId = null,
        [Description("Expected foreground window handle. Supply it to reject changes of target window.")] int? expectedWindowHandle = null,
        [Description("Optional observed subtree reference. Uniqueness is checked within that subtree.")] string? scopeRef = null)
    {
        var target = FindUniqueElement(name, automationId, expectedWindowHandle, scopeRef);
        EnsureExpectedForegroundWindow(target.WindowHandle);
        EnsureEnabled(target.Element);
        EnsureNotPassword(target.Element);
        CompactAccessibilityTools.InvalidateWindowReferences(target.WindowHandle);
        target.Element.SetFocus();
        var hasFocus = SpinWait.SpinUntil(() => target.Element.Current.HasKeyboardFocus, 500);
        EnsureExpectedForegroundWindow(target.WindowHandle);
        if (!hasFocus)
        {
            throw new McpException("The control did not acquire keyboard focus. Re-read the window before sending keys.");
        }

        return "Focused element.";
    }

    [McpServerTool(Name = "activate_accessible_element")]
    [Description("Activate one uniquely matching accessible element in the foreground window. Provide its exact accessible name or automation ID.")]
    public static string ActivateAccessibleElement(
        [Description("Exact accessible name. May be omitted when automationId is provided.")] string? name = null,
        [Description("Exact UI Automation ID; useful when names are duplicated.")] string? automationId = null,
        [Description("Action to perform: invoke, select, toggle, expand, or collapse. Defaults to invoke; specify toggle or expand/collapse explicitly.")] string action = "invoke",
        [Description("Expected foreground window handle. Supply it to reject changes of target window.")] int? expectedWindowHandle = null,
        [Description("Optional observed subtree reference. Uniqueness is checked within that subtree.")] string? scopeRef = null)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            throw new McpException("Provide an activation action.");
        }

        action = action.Trim().ToLowerInvariant();
        var target = FindUniqueElement(name, automationId, expectedWindowHandle, scopeRef);
        EnsureExpectedForegroundWindow(target.WindowHandle);
        EnsureEnabled(target.Element);
        EnsureNotPassword(target.Element);

        if (action is not ("invoke" or "select" or "toggle" or "expand" or "collapse"))
        {
            throw new McpException("Action must be invoke, select, toggle, expand, or collapse.");
        }

        CompactAccessibilityTools.InvalidateWindowReferences(target.WindowHandle);
        if (action == "invoke"
            && target.Element.TryGetCurrentPattern(InvokePattern.Pattern, out var invokePattern))
        {
            EnsureExpectedForegroundWindow(target.WindowHandle);
            ((InvokePattern)invokePattern).Invoke();
            return "Activated element.";
        }

        if (action == "select"
            && target.Element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionPattern))
        {
            var pattern = (SelectionItemPattern)selectionPattern;
            EnsureExpectedForegroundWindow(target.WindowHandle);
            pattern.Select();
            if (!SpinWait.SpinUntil(() => pattern.Current.IsSelected, 500))
            {
                throw new McpException("Selection was requested but the control has not confirmed it. Inspect before retrying.");
            }

            return "Selected element.";
        }

        if (action == "toggle"
            && target.Element.TryGetCurrentPattern(TogglePattern.Pattern, out var togglePattern))
        {
            var pattern = (TogglePattern)togglePattern;
            var previousState = pattern.Current.ToggleState;
            EnsureExpectedForegroundWindow(target.WindowHandle);
            pattern.Toggle();
            if (!SpinWait.SpinUntil(() => pattern.Current.ToggleState != previousState, 500))
            {
                throw new McpException("Toggle was requested but its state change could not be confirmed. Inspect before retrying.");
            }

            return "Toggled element.";
        }

        if (action is "expand" or "collapse"
            && target.Element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expandPattern))
        {
            var pattern = (ExpandCollapsePattern)expandPattern;
            var currentState = pattern.Current.ExpandCollapseState;
            if (action == "expand")
            {
                if (currentState == ExpandCollapseState.Expanded)
                {
                    return "Element is already expanded; no action was taken.";
                }

                EnsureExpectedForegroundWindow(target.WindowHandle);
                pattern.Expand();
                if (!SpinWait.SpinUntil(() => pattern.Current.ExpandCollapseState == ExpandCollapseState.Expanded, 500))
                {
                    throw new McpException("Expansion was requested but could not be confirmed. Inspect before retrying.");
                }

                return "Expanded element.";
            }

            if (currentState == ExpandCollapseState.Collapsed)
            {
                return "Element is already collapsed; no action was taken.";
            }

            EnsureExpectedForegroundWindow(target.WindowHandle);
            pattern.Collapse();
            if (!SpinWait.SpinUntil(() => pattern.Current.ExpandCollapseState == ExpandCollapseState.Collapsed, 500))
            {
                throw new McpException("Collapse was requested but could not be confirmed. Inspect before retrying.");
            }

            return "Collapsed element.";
        }

        throw new McpException($"The matched element does not support the requested '{action}' action.");
    }

    [McpServerTool(Name = "set_accessible_text")]
    [Description("Set text on one uniquely matching UI Automation control. Password and read-only controls are rejected.")]
    public static string SetAccessibleText(
        [Description("Text to set on the matched control.")] string text,
        [Description("Exact accessible name. May be omitted when automationId is provided.")] string? name = null,
        [Description("Exact UI Automation ID; useful when names are duplicated.")] string? automationId = null,
        [Description("Expected foreground window handle. Supply it to reject changes of target window.")] int? expectedWindowHandle = null,
        [Description("Optional observed subtree reference. Uniqueness is checked within that subtree.")] string? scopeRef = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        var target = FindUniqueElement(name, automationId, expectedWindowHandle, scopeRef);
        EnsureExpectedForegroundWindow(target.WindowHandle);
        SetElementText(target.Element, text, target.WindowHandle);
        return "Text set on the matched control.";
    }

    [McpServerTool(Name = "set_focused_text")]
    [Description("Set text on the currently focused UI Automation control. Password and read-only controls are rejected.")]
    public static string SetFocusedText(
        [Description("Text to set on the focused control.")] string text,
        [Description("Expected foreground window handle. Supply it to reject changes of target window.")] int? expectedWindowHandle = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (expectedWindowHandle.HasValue)
        {
            EnsureExpectedForegroundWindow(expectedWindowHandle.Value);
        }

        var foregroundWindowHandle = NativeMethods.GetForegroundWindow();
        if (foregroundWindowHandle == IntPtr.Zero)
        {
            throw new McpException("Could not identify the foreground window.");
        }

        var focused = AutomationElement.FocusedElement
            ?? throw new McpException("There is no focused accessibility element.");

        EnsureElementInWindow(focused, foregroundWindowHandle.ToInt32());
        EnsureExpectedForegroundWindow(foregroundWindowHandle.ToInt32());
        if (!focused.Current.HasKeyboardFocus)
        {
            throw new McpException("Keyboard focus changed before setting text. Re-read the window.");
        }

        SetElementText(focused, text, foregroundWindowHandle.ToInt32());
        return "Text set on the focused control.";
    }

    [McpServerTool(Name = "press_keys")]
    [Description("Send keyboard keys after checking that the expected window is foreground. Windows routes keyboard input globally, so focus can still race between the check and input. Each entry is one key or a chord such as CTRL+L, ENTER, TAB, ESC, or F5.")]
    public static string PressKeys(
        [Description("Expected current foreground window handle from list_open_windows. The request fails if focus has already changed; Windows cannot atomically bind SendInput to this window.")] int expectedWindowHandle,
        [Description("One to ten key presses or chords, for example [\"CTRL+L\", \"ENTER\"].")] string[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (expectedWindowHandle == 0)
        {
            throw new McpException("Window handle must not be zero.");
        }

        if (keys.Length is < 1 or > 10)
        {
            throw new McpException("Provide between 1 and 10 key presses.");
        }

        // Parse the entire request before sending anything, so an invalid later chord
        // cannot leave an earlier action applied.
        IReadOnlyList<NativeMethods.Input[]> chords;
        try
        {
            chords = ParseKeyInputs(keys);
        }
        catch (ArgumentException error)
        {
            throw new McpException(error.Message);
        }
        for (var index = 0; index < chords.Count; index++)
        {
            EnsureExpectedForegroundWindow(expectedWindowHandle);
            var focused = AutomationElement.FocusedElement
                ?? throw new McpException("There is no focused accessibility element.");
            EnsureElementInWindow(focused, expectedWindowHandle);
            EnsureEnabled(focused);
            EnsureNotPassword(focused);
            EnsureExpectedForegroundWindow(expectedWindowHandle);
            CompactAccessibilityTools.InvalidateWindowReferences(expectedWindowHandle);
            SendKeyboardInputs(chords[index], index);
        }

        return $"Sent {keys.Length} key press(es) to the foreground window. Inspect the result before further input.";
    }

    internal static IReadOnlyList<NativeMethods.Input[]> ParseKeyInputs(string[] keys)
    {
        var chords = new List<NativeMethods.Input[]>(keys.Length);
        for (var index = 0; index < keys.Length; index++)
        {
            try
            {
                chords.Add(KeyInputParser.Parse(keys[index])
                    .Select(keyEvent => NativeMethods.Keyboard(keyEvent.VirtualKey, keyEvent.KeyUp))
                    .ToArray());
            }
            catch (ArgumentException)
            {
                // Do not echo user input: accidentally supplied text may contain secrets.
                throw new ArgumentException($"Unsupported or malformed key chord at position {index + 1}. No keys were sent.", nameof(keys));
            }
        }

        return chords;
    }

    private static void SendKeyboardInputs(NativeMethods.Input[] inputArray, int completedChords)
    {
        var sent = NativeMethods.SendInput((uint)inputArray.Length, inputArray, NativeMethods.InputSize);
        if (sent != inputArray.Length)
        {
            var errorCode = Marshal.GetLastWin32Error();
            var sentEvents = inputArray.Take((int)sent).ToArray();
            var keysStillPressed = new HashSet<ushort>();
            foreach (var input in sentEvents)
            {
                var virtualKey = input.Data.Keyboard.VirtualKey;
                if ((input.Data.Keyboard.Flags & 0x0002) != 0)
                {
                    keysStillPressed.Remove(virtualKey);
                }
                else
                {
                    keysStillPressed.Add(virtualKey);
                }
            }

            var releaseInputs = keysStillPressed
                .Select(virtualKey => NativeMethods.Keyboard(virtualKey, keyUp: true))
                .ToArray();
            if (releaseInputs.Length > 0)
            {
                NativeMethods.SendInput((uint)releaseInputs.Length, releaseInputs, NativeMethods.InputSize);
            }

            throw new McpException(
                $"After {completedChords} completed chord(s), Windows accepted {sent} of {inputArray.Length} events in the next chord (Win32 error {errorCode}). Inspect the window before retrying.");
        }
    }

    private static void EnsureExpectedForegroundWindow(int expectedWindowHandle)
    {
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            throw new McpException("Could not identify the foreground window to receive keyboard input.");
        }

        if (foregroundWindow.ToInt32() != expectedWindowHandle)
        {
            throw new McpException("The foreground window changed. Re-list windows and retry against the intended window.");
        }
    }

    internal static (AutomationElement Element, int WindowHandle) FindUniqueElement(
        string? name, string? automationId, int? expectedWindowHandle, string? scopeRef,
        int maximumDepth = MaximumTreeDepth)
    {
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(automationId))
        {
            throw new McpException("Provide a name or automationId.");
        }

        if (expectedWindowHandle.HasValue)
        {
            EnsureExpectedForegroundWindow(expectedWindowHandle.Value);
        }

        var root = GetForegroundWindow();
        var windowHandle = root.Current.NativeWindowHandle;
        if (expectedWindowHandle.HasValue && windowHandle != expectedWindowHandle.Value)
        {
            throw new McpException("The foreground window changed before the control search. Inspect it again.");
        }
        if (scopeRef is not null)
        {
            root = CompactAccessibilityTools.ResolveSearchScope(scopeRef, windowHandle);
        }
        var search = BoundedTreeSearch.Search(
            root,
            GetSearchChildren,
            element => GetMatchState(element, name, automationId),
            MaximumElementLimit,
            scopeRef is null ? maximumDepth : 32);
        var scopeMatch = scopeRef is null ? BoundedTreeSearch.TreeMatchState.NoMatch : GetMatchState(root, name, automationId);
        var matches = scopeMatch == BoundedTreeSearch.TreeMatchState.Match ? [root, .. search.Matches] : search.Matches;
        AutomationElement target;
        try
        {
            target = UniqueElementSearchPolicy.Resolve(
                matches,
                search.WasTruncated || scopeMatch == BoundedTreeSearch.TreeMatchState.Unavailable,
                search.SkippedUnavailableElements);
        }
        catch (InvalidOperationException error)
        {
            // This policy only emits fixed diagnostic text and numeric counts.
            throw new McpException(error.Message);
        }

        EnsureExpectedForegroundWindow(windowHandle);
        if (GetMatchState(target, name, automationId) != BoundedTreeSearch.TreeMatchState.Match)
        {
            throw new McpException("The matched control changed before the action. Inspect it again.");
        }
        return (target, windowHandle);
    }

    private static BoundedTreeSearch.BoundedChildren<AutomationElement> GetSearchChildren(
        AutomationElement parent,
        int maximumChildren)
    {
        var children = new Stack<(AutomationElement Element, int Depth)>();
        var result = AddChildren(parent, 0, children, maximumChildren);
        return new BoundedTreeSearch.BoundedChildren<AutomationElement>(
            children.Select(child => child.Element).ToArray(),
            result.WasTruncated,
            result.SkippedUnavailableElements);
    }

    private static BoundedTreeSearch.TreeMatchState GetMatchState(
        AutomationElement element,
        string? name,
        string? automationId)
    {
        try
        {
            var current = element.Current;
            var matches = (string.IsNullOrWhiteSpace(name)
                    || string.Equals(current.Name, name, StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrWhiteSpace(automationId)
                    || string.Equals(current.AutomationId, automationId, StringComparison.Ordinal));
            return matches
                ? BoundedTreeSearch.TreeMatchState.Match
                : BoundedTreeSearch.TreeMatchState.NoMatch;
        }
        catch (ElementNotAvailableException)
        {
            return BoundedTreeSearch.TreeMatchState.Unavailable;
        }
    }

    private static AccessibleElement ReadElement(AutomationElement element, int depth)
    {
        var current = element.Current;
        var content = current.IsPassword ? null : ReadElementContent(element);
        var toggleState = element.TryGetCurrentPattern(TogglePattern.Pattern, out var togglePattern)
            ? ((TogglePattern)togglePattern).Current.ToggleState.ToString()
            : null;
        var expandCollapseState = element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expandPattern)
            ? ((ExpandCollapsePattern)expandPattern).Current.ExpandCollapseState.ToString()
            : null;
        var isSelected = element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionPattern)
            ? ((SelectionItemPattern)selectionPattern).Current.IsSelected
            : (bool?)null;

        return new AccessibleElement(
            depth,
            current.IsPassword ? "[password]" : Truncate(current.Name) ?? string.Empty,
            current.ControlType.ProgrammaticName,
            Truncate(current.AutomationId) ?? string.Empty,
            content,
            current.IsEnabled,
            current.IsOffscreen,
            current.HasKeyboardFocus,
            current.IsPassword,
            isSelected,
            toggleState,
            expandCollapseState);
    }

    private static string? ReadElementContent(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern))
        {
            return Truncate(((ValuePattern)valuePattern).Current.Value);
        }

        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern))
        {
            return Truncate(((TextPattern)textPattern).DocumentRange.GetText(MaximumTextLength + 1));
        }

        return null;
    }

    private static void SetElementText(AutomationElement element, string text, int expectedWindowHandle)
    {
        EnsureEnabled(element);

        EnsureNotPassword(element);

        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern))
        {
            throw new McpException("The element does not support ValuePattern.");
        }

        var value = (ValuePattern)valuePattern;
        if (value.Current.IsReadOnly)
        {
            throw new McpException("The element is read-only.");
        }

        EnsureExpectedForegroundWindow(expectedWindowHandle);
        EnsureNotPassword(element);
        CompactAccessibilityTools.InvalidateWindowReferences(expectedWindowHandle);
        value.SetValue(text);
        if (!SpinWait.SpinUntil(() => string.Equals(value.Current.Value, text, StringComparison.Ordinal), 500))
        {
            throw new McpException("The control did not retain the requested text. Inspect its current state before retrying.");
        }
    }

    private static void EnsureNotPassword(AutomationElement element)
    {
        var ancestor = element;
        for (var depth = 0; ancestor is not null && depth < 128; depth++)
        {
            if (ancestor.Current.IsPassword)
            {
                throw new McpException("Refusing to operate on a password field. The user must enter secrets privately.");
            }

            if (ancestor.Current.ControlType == ControlType.Window) return;
            ancestor = TreeWalker.RawViewWalker.GetParent(ancestor);
        }

        throw new McpException("Could not verify the control's password ancestry. Inspect the window again.");
    }

    private static void EnsureElementInWindow(AutomationElement element, int expectedWindowHandle)
    {
        var current = element;
        for (var depth = 0; current is not null && depth < 128; depth++)
        {
            if (current.Current.NativeWindowHandle == expectedWindowHandle)
            {
                return;
            }

            current = TreeWalker.RawViewWalker.GetParent(current);
        }

        throw new McpException("The focused control could not be verified inside the intended window. Re-read the window before retrying.");
    }

    private static void EnsureEnabled(AutomationElement element)
    {
        if (!element.Current.IsEnabled)
        {
            throw new McpException("The matched element is disabled.");
        }
    }

    private static AutomationElement GetForegroundWindow()
    {
        var handle = NativeMethods.GetForegroundWindow();
        if (handle == IntPtr.Zero)
        {
            throw new McpException("Could not identify the foreground window.");
        }

        return AutomationElement.FromHandle(handle)
            ?? throw new McpException("The foreground window is not accessible through UI Automation.");
    }

    private static ChildEnumerationResult AddChildren(
        AutomationElement parent,
        int depth,
        Stack<(AutomationElement Element, int Depth)> pending,
        int maximumChildren)
    {
        var children = new List<AutomationElement>();
        var wasTruncated = false;
        var skippedUnavailableElements = 0;
        AutomationElement? child;
        try
        {
            if (parent.Current.IsPassword) return new ChildEnumerationResult(false, 0);
            child = TreeWalker.ControlViewWalker.GetFirstChild(parent);
        }
        catch (ElementNotAvailableException)
        {
            skippedUnavailableElements++;
            return new ChildEnumerationResult(true, skippedUnavailableElements);
        }

        while (child is not null)
        {
            if (children.Count >= maximumChildren)
            {
                wasTruncated = true;
                break;
            }

            children.Add(child);
            try
            {
                child = TreeWalker.ControlViewWalker.GetNextSibling(child);
            }
            catch (ElementNotAvailableException)
            {
                skippedUnavailableElements++;
                wasTruncated = true;
                break;
            }
        }

        for (var index = children.Count - 1; index >= 0; index--)
        {
            pending.Push((children[index], depth));
        }

        return new ChildEnumerationResult(wasTruncated, skippedUnavailableElements);
    }

    private static bool HasChildren(AutomationElement parent, ref int skippedUnavailableElements)
    {
        try
        {
            if (parent.Current.IsPassword) return false;
            return TreeWalker.ControlViewWalker.GetFirstChild(parent) is not null;
        }
        catch (ElementNotAvailableException)
        {
            skippedUnavailableElements++;
            return false;
        }
    }

    private static string? Truncate(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return SensitiveContentRedactor.Redact(value);
        }

        var wasTruncated = value.Length > MaximumTextLength;
        var content = wasTruncated ? value[..MaximumTextLength] : value;
        var redactedContent = SensitiveContentRedactor.Redact(content);
        return wasTruncated ? $"{redactedContent} [truncated]" : redactedContent;
    }

    private static void ValidateElementLimit(int maxElements)
    {
        if (maxElements is < 1 or > MaximumElementLimit)
        {
            throw new McpException($"Value must be between 1 and {MaximumElementLimit}.");
        }
    }

    public sealed record OpenWindow(
        string Title,
        int ProcessId,
        int NativeWindowHandle,
        bool HasKeyboardFocus);

    public sealed record WindowSnapshot(
        string Title,
        int ProcessId,
        int NativeWindowHandle,
        int MaximumElements,
        bool IsTruncated,
        int SkippedUnavailableElements,
        IReadOnlyList<AccessibleElement> Elements);

    public sealed record AccessibleElement(
        int Depth,
        string Name,
        string ControlType,
        string AutomationId,
        string? Content,
        bool IsEnabled,
        bool IsOffscreen,
        bool HasKeyboardFocus,
        bool IsPassword,
        bool? IsSelected,
        string? ToggleState,
        string? ExpandCollapseState);

    private sealed record ChildEnumerationResult(
        bool WasTruncated,
        int SkippedUnavailableElements);
}
