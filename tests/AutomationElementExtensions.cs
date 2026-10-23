#nullable enable

using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace FlaUI.Core.AutomationElements;

/// <summary>
/// Element state to wait for in <see cref="Locator.WaitFor"/>.
/// </summary>
public enum WaitForState
{
    /// <summary>Element is in the UI tree, not offscreen, and has a non-empty bounding box.</summary>
    Visible,

    /// <summary>Element is not in the UI tree or is offscreen/zero-sized.</summary>
    Hidden,

    /// <summary>Element is present in the UI tree (even if offscreen).</summary>
    Attached,

    /// <summary>Element is not present in the UI tree.</summary>
    Detached
}

/// <summary>
/// Playwright-style locators, auto-waiting actions, and state queries for FlaUI <see cref="AutomationElement"/>.
/// Drop this single file into any test project; it shares the <c>FlaUI.Core.AutomationElements</c> namespace.
/// </summary>
public static class AutomationElementExtensions
{
    /// <summary>
    /// Creates a lazy locator that finds a descendant by its <c>AutomationId</c> (e.g. WPF <c>x:Name</c>).
    /// Does not query the UI until an action or state query is called.
    /// </summary>
    public static Locator FindElement(this AutomationElement root, string automationId) =>
        Locator.ByAutomationId(() => root, automationId);

    /// <summary>
    /// Creates a lazy locator that finds a descendant matching a custom FlaUI condition.
    /// Does not query the UI until an action or state query is called.
    /// </summary>
    public static Locator FindElement(this AutomationElement root, Func<ConditionFactory, ConditionBase> condition, string description = "custom condition") =>
        new(root, condition, description);

    /// <summary>
    /// Creates a lazy locator that finds a descendant by its <c>AutomationId</c> (Playwright alias for <see cref="FindElement(AutomationElement, string)"/>).
    /// </summary>
    public static Locator GetByTestId(this AutomationElement root, string automationId) =>
        root.FindElement(automationId);

    /// <summary>
    /// Creates a lazy locator that finds a descendant by its visible text (<c>Name</c> property).
    /// </summary>
    public static Locator GetByText(this AutomationElement root, string text) =>
        Locator.ByText(() => root, text);

    /// <summary>
    /// Creates a lazy locator that finds a descendant by its UIA <see cref="ControlType"/> (e.g. <c>ControlType.Button</c>) and optional accessible name.
    /// </summary>
    public static Locator GetByRole(this AutomationElement root, ControlType role, string? name = null) =>
        Locator.ByRole(() => root, role, name);
}

/// <summary>
/// Lazy reference to a UI element. Auto-waits for the element to be actionable before performing actions.
/// </summary>
public sealed class Locator
{
    private static readonly int[] ActionBackoffMs = [20, 50, 100, 100, 500];

    /// <summary>
    /// Default timeout for locator actions and queries (30 seconds).
    /// </summary>
    public static TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(30);

    private readonly Func<AutomationElement?> _rootResolver;
    private readonly Func<ConditionFactory, ConditionBase> _condition;
    private readonly string _description;

    /// <summary>
    /// Initializes a new locator rooted at <paramref name="root"/>.
    /// </summary>
    public Locator(AutomationElement root, Func<ConditionFactory, ConditionBase> condition, string description)
        : this(() => root, condition, description)
    {
    }

    private Locator(Func<AutomationElement?> rootResolver, Func<ConditionFactory, ConditionBase> condition, string description)
    {
        _rootResolver = rootResolver;
        _condition = condition;
        _description = description;
    }

    /// <summary>
    /// Resolves and returns the underlying FlaUI <see cref="AutomationElement"/>, waiting up to <see cref="DefaultTimeout"/> for it to be attached.
    /// </summary>
    public AutomationElement Element => ResolveAttached(null);

    /// <summary>
    /// Chains a child locator that finds a descendant by its <c>AutomationId</c> inside this locator.
    /// </summary>
    public Locator FindElement(string automationId) =>
        ByAutomationId(TryResolve, automationId, _description);

    /// <summary>
    /// Chains a child locator that finds a descendant matching a custom FlaUI condition inside this locator.
    /// </summary>
    public Locator FindElement(Func<ConditionFactory, ConditionBase> condition, string description = "custom condition") =>
        new(TryResolve, condition, ChainDescription(_description, description));

    /// <summary>
    /// Chains a child locator that finds a descendant by its <c>AutomationId</c> inside this locator.
    /// </summary>
    public Locator GetByTestId(string automationId) => FindElement(automationId);

    /// <summary>
    /// Chains a child locator that finds a descendant by its visible text (<c>Name</c> property) inside this locator.
    /// </summary>
    public Locator GetByText(string text) =>
        ByText(TryResolve, text, _description);

    /// <summary>
    /// Chains a child locator that finds a descendant by its <see cref="ControlType"/> and optional name inside this locator.
    /// </summary>
    public Locator GetByRole(ControlType role, string? name = null) =>
        ByRole(TryResolve, role, name, _description);

    internal static Locator ByAutomationId(Func<AutomationElement?> rootResolver, string automationId, string? parentDescription = null) =>
        new(rootResolver, cf => cf.ByAutomationId(automationId), ChainDescription(parentDescription, $"AutomationId='{automationId}'"));

    internal static Locator ByText(Func<AutomationElement?> rootResolver, string text, string? parentDescription = null) =>
        new(rootResolver, cf => cf.ByName(text), ChainDescription(parentDescription, $"Text='{text}'"));

    internal static Locator ByRole(Func<AutomationElement?> rootResolver, ControlType role, string? name, string? parentDescription = null) =>
        new(
            rootResolver,
            cf => name is null
                ? cf.ByControlType(role)
                : cf.ByControlType(role).And(cf.ByName(name)),
            ChainDescription(parentDescription, name is null ? $"Role={role}" : $"Role={role}, Name='{name}'"));

    private static string ChainDescription(string? parentDescription, string description) =>
        parentDescription is null ? description : $"{parentDescription} >> {description}";

    /// <summary>
    /// Waits until the element reaches the specified <paramref name="state"/> (defaults to <see cref="WaitForState.Visible"/>).
    /// </summary>
    public Locator WaitFor(WaitForState state = WaitForState.Visible, TimeSpan? timeout = null)
    {
        Poll(
            timeout,
            () =>
            {
                var element = TryResolve();
                return state switch
                {
                    WaitForState.Attached => element is not null
                        ? (true, true, null)
                        : (false, false, "element is not attached"),
                    WaitForState.Detached => element is null
                        ? (true, true, null)
                        : (false, false, "element is still attached"),
                    WaitForState.Visible => IsElementVisible(element)
                        ? (true, true, null)
                        : (false, false, element is null ? "element is not attached" : "element is not visible"),
                    WaitForState.Hidden => !IsElementVisible(element)
                        ? (true, true, null)
                        : (false, false, "element is still visible"),
                    _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
                };
            },
            $"WaitFor({state})");

        return this;
    }

    /// <summary>
    /// Waits for the element to be visible, stable, and enabled, then clicks it.
    /// Uses UIA patterns (<c>Invoke</c>, <c>Toggle</c>, <c>SelectionItem</c>) unless <paramref name="forceMouse"/> is <c>true</c>.
    /// </summary>
    public void Click(bool forceMouse = false, TimeSpan? timeout = null)
    {
        var element = ResolveActionable(timeout);

        if (!forceMouse)
        {
            if (element.Patterns.Invoke.IsSupported)
            {
                element.Patterns.Invoke.Pattern.Invoke();
                return;
            }

            if (element.Patterns.Toggle.IsSupported)
            {
                element.Patterns.Toggle.Pattern.Toggle();
                return;
            }

            if (element.Patterns.SelectionItem.IsSupported)
            {
                element.Patterns.SelectionItem.Pattern.Select();
                return;
            }
        }

        element.Click();
    }

    /// <summary>
    /// Waits for the element to be visible, stable, and enabled, then double-clicks it with the mouse.
    /// </summary>
    public void DoubleClick(TimeSpan? timeout = null) =>
        ResolveActionable(timeout).DoubleClick();

    /// <summary>
    /// Waits for the element to be visible, stable, and enabled, then right-clicks it with the mouse.
    /// </summary>
    public void RightClick(TimeSpan? timeout = null) =>
        ResolveActionable(timeout).RightClick();

    /// <summary>
    /// Waits for the input element to be visible, enabled, and editable, then sets its text value.
    /// </summary>
    public void Fill(string value, TimeSpan? timeout = null)
    {
        var element = ResolveActionable(timeout, requireStable: false, requireEditable: true);

        if (element.Patterns.Value.IsSupported)
        {
            element.Patterns.Value.Pattern.SetValue(value);
            return;
        }

        element.AsTextBox().Text = value;
    }

    /// <summary>
    /// Waits for the input element to be visible, enabled, and editable, then clears its text.
    /// </summary>
    public void Clear(TimeSpan? timeout = null) => Fill(string.Empty, timeout);

    /// <summary>
    /// Waits for the element to be visible and enabled, focuses it, and presses a single key (e.g. <see cref="VirtualKeyShort.RETURN"/>).
    /// </summary>
    public void Press(VirtualKeyShort key, TimeSpan? timeout = null)
    {
        ResolveActionable(timeout, requireStable: false).Focus();
        Keyboard.Type(key);
    }

    /// <summary>
    /// Waits for the element to be visible and enabled, focuses it, and types the text character by character.
    /// </summary>
    public void Type(string text, TimeSpan? timeout = null)
    {
        ResolveActionable(timeout, requireStable: false).Focus();
        Keyboard.Type(text);
    }

    /// <summary>
    /// Waits for the element to be attached and returns its text (<c>Value</c>, <c>Text</c> pattern, or <c>Name</c>).
    /// </summary>
    public string TextContent(TimeSpan? timeout = null)
    {
        var element = ResolveAttached(timeout);
        if (element.Patterns.Value.IsSupported)
        {
            return element.Patterns.Value.Pattern.Value.Value ?? string.Empty;
        }

        if (element.Patterns.Text.IsSupported)
        {
            return element.Patterns.Text.Pattern.DocumentRange.GetText(-1).TrimEnd('\r', '\n');
        }

        return element.Properties.Name.ValueOrDefault ?? string.Empty;
    }

    /// <summary>
    /// Waits for the element to be attached and returns its text (alias for <see cref="TextContent"/>).
    /// </summary>
    public string Text(TimeSpan? timeout = null) => TextContent(timeout);

    /// <summary>
    /// Waits for the input element to be attached and returns its current value.
    /// </summary>
    public string InputValue(TimeSpan? timeout = null)
    {
        var element = ResolveAttached(timeout);
        return element.Patterns.Value.IsSupported
            ? element.Patterns.Value.Pattern.Value.Value ?? string.Empty
            : element.AsTextBox().Text ?? string.Empty;
    }

    /// <summary>
    /// Waits for the checkbox or radio button to be visible, stable, and enabled, then checks it.
    /// </summary>
    public void Check(TimeSpan? timeout = null)
    {
        var element = ResolveActionable(timeout);

        if (element.Patterns.SelectionItem.IsSupported)
        {
            element.Patterns.SelectionItem.Pattern.Select();
            return;
        }

        if (element.Patterns.Toggle.IsSupported)
        {
            if (element.Patterns.Toggle.Pattern.ToggleState.Value != ToggleState.On)
            {
                element.Patterns.Toggle.Pattern.Toggle();
            }

            return;
        }

        if (element.Properties.ControlType.ValueOrDefault == ControlType.RadioButton)
        {
            element.AsRadioButton().IsChecked = true;
            return;
        }

        var checkBox = element.AsCheckBox();
        if (checkBox.IsChecked != true)
        {
            checkBox.IsChecked = true;
        }
    }

    /// <summary>
    /// Waits for the checkbox to be visible, stable, and enabled, then unchecks it.
    /// </summary>
    public void Uncheck(TimeSpan? timeout = null)
    {
        var element = ResolveActionable(timeout);

        if (element.Patterns.Toggle.IsSupported)
        {
            if (element.Patterns.Toggle.Pattern.ToggleState.Value != ToggleState.Off)
            {
                element.Patterns.Toggle.Pattern.Toggle();
            }

            return;
        }

        var checkBox = element.AsCheckBox();
        if (checkBox.IsChecked != false)
        {
            checkBox.IsChecked = false;
        }
    }

    /// <summary>
    /// Waits for the combo box to be visible, stable, and enabled, then selects an item by its text.
    /// </summary>
    public void SelectOption(string textOrValue, TimeSpan? timeout = null) =>
        ResolveActionable(timeout).AsComboBox().Select(textOrValue);

    /// <summary>
    /// Waits for the combo box to be visible, stable, and enabled, then selects an item by its zero-based index.
    /// </summary>
    public void SelectOption(int index, TimeSpan? timeout = null) =>
        ResolveActionable(timeout).AsComboBox().Select(index);

    /// <summary>
    /// Returns <c>true</c> immediately if the element is currently attached and visible (does not wait).
    /// </summary>
    public bool IsVisible() => IsElementVisible(TryResolve());

    /// <summary>
    /// Returns <c>true</c> immediately if the element is absent or hidden (does not wait).
    /// </summary>
    public bool IsHidden() => !IsVisible();

    /// <summary>
    /// Waits for the element to be attached and returns whether it is enabled.
    /// </summary>
    public bool IsEnabled(TimeSpan? timeout = null) =>
        ResolveAttached(timeout).Properties.IsEnabled.ValueOrDefault;

    /// <summary>
    /// Waits for the checkbox or radio button to be attached and returns whether it is checked.
    /// </summary>
    public bool IsChecked(TimeSpan? timeout = null)
    {
        var element = ResolveAttached(timeout);
        if (element.Patterns.Toggle.IsSupported)
        {
            return element.Patterns.Toggle.Pattern.ToggleState.Value == ToggleState.On;
        }

        if (element.Patterns.SelectionItem.IsSupported)
        {
            return element.Patterns.SelectionItem.Pattern.IsSelected.Value;
        }

        throw new InvalidOperationException($"Element does not support Toggle or SelectionItem pattern: {_description}");
    }

    private AutomationElement? TryResolve()
    {
        try
        {
            return _rootResolver()?.FindFirstDescendant(_condition);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsElementVisible(AutomationElement? element) =>
        TryGetVisibleBounds(element, out _);

    private static bool TryGetVisibleBounds(AutomationElement? element, out Rectangle bounds)
    {
        bounds = default;
        if (element is null)
        {
            return false;
        }

        try
        {
            return !element.Properties.IsOffscreen.ValueOrDefault
                && element.Properties.BoundingRectangle.TryGetValue(out bounds)
                && bounds.Width > 0
                && bounds.Height > 0;
        }
        catch
        {
            return false;
        }
    }

    private AutomationElement ResolveAttached(TimeSpan? timeout) =>
        Poll<AutomationElement>(
            timeout,
            () =>
            {
                var element = TryResolve();
                return element is not null
                    ? (true, element, null)
                    : (false, null, "element not found");
            },
            "Waiting")!;

    private AutomationElement ResolveActionable(
        TimeSpan? timeout,
        bool requireStable = true,
        bool requireEditable = false)
    {
        Rectangle? previousBounds = null;

        return Poll<AutomationElement>(
            timeout,
            () =>
            {
                var element = TryResolve();
                if (element is null)
                {
                    previousBounds = null;
                    return (false, null, "element is not attached");
                }

                try
                {
                    if (!TryGetVisibleBounds(element, out var bounds))
                    {
                        previousBounds = null;
                        return (false, null, "element is not visible");
                    }

                    if (requireStable && previousBounds != bounds)
                    {
                        previousBounds = bounds;
                        return (false, null, "element is not stable");
                    }

                    if (!element.Properties.IsEnabled.ValueOrDefault)
                    {
                        return (false, null, "element is not enabled");
                    }

                    if (requireEditable &&
                        element.Patterns.Value.IsSupported &&
                        element.Patterns.Value.Pattern.IsReadOnly.Value)
                    {
                        return (false, null, "element is read-only");
                    }

                    return (true, element, null);
                }
                catch (Exception ex)
                {
                    previousBounds = null;
                    return (false, null, ex.Message);
                }
            },
            "Action")!;
    }

    private T? Poll<T>(
        TimeSpan? timeout,
        Func<(bool Done, T? Value, string? LastFailure)> step,
        string operation)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;
        var stopwatch = Stopwatch.StartNew();
        var attempt = 0;

        while (true)
        {
            var (done, value, lastFailure) = step();
            if (done)
            {
                return value;
            }

            var remaining = effectiveTimeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException(
                    $"{operation} timed out after {effectiveTimeout.TotalMilliseconds}ms on '{_description}': {lastFailure}");
            }

            var delayMs = ActionBackoffMs[Math.Min(attempt, ActionBackoffMs.Length - 1)];
            attempt++;

            var sleepTime = TimeSpan.FromMilliseconds(delayMs);
            if (sleepTime > remaining)
            {
                sleepTime = remaining;
            }

            if (sleepTime > TimeSpan.Zero)
            {
                Thread.Sleep(sleepTime);
            }
        }
    }
}
