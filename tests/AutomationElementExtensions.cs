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
/// Playwright-style locators, auto-waiting actions, and assertions for FlaUI <see cref="AutomationElement"/>.
/// Drop this single file into any test project; it shares the <c>FlaUI.Core.AutomationElements</c> namespace.
/// </summary>
public static class AutomationElementExtensions
{
    /// <summary>
    /// Creates a lazy locator that finds a descendant by its <c>AutomationId</c> (e.g. WPF <c>x:Name</c>).
    /// Does not query the UI until an action or assertion is called.
    /// </summary>
    public static Locator FindElement(this AutomationElement root, string automationId) =>
        new(root, cf => cf.ByAutomationId(automationId), $"AutomationId='{automationId}'");

    /// <summary>
    /// Creates a lazy locator that finds a descendant matching a custom FlaUI condition.
    /// Does not query the UI until an action or assertion is called.
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
        new(root, cf => cf.ByName(text), $"Text='{text}'");

    /// <summary>
    /// Creates a lazy locator that finds a descendant by its UIA <see cref="ControlType"/> (e.g. <c>ControlType.Button</c>) and optional accessible name.
    /// </summary>
    public static Locator GetByRole(this AutomationElement root, ControlType role, string? name = null) =>
        new(
            root,
            cf => name is null
                ? cf.ByControlType(role)
                : cf.ByControlType(role).And(cf.ByName(name)),
            name is null ? $"Role={role}" : $"Role={role}, Name='{name}'");

    /// <summary>
    /// Creates an auto-retrying assertion builder for the locator (5s default timeout).
    /// Can be called as <c>locator.Expect()</c> or <c>Expect(locator)</c> with <c>using static FlaUI.Core.AutomationElements.AutomationElementExtensions;</c>.
    /// </summary>
    public static LocatorAssertions Expect(this Locator locator) => new(locator);
}

/// <summary>
/// Lazy reference to a UI element. Auto-waits for the element to be actionable before performing actions.
/// </summary>
public sealed class Locator
{
    private static readonly int[] ActionBackoffMs = [20, 50, 100, 100, 500];
    internal static readonly int[] ExpectBackoffMs = [100, 250, 500, 1000];

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

    internal string Description => _description;

    /// <summary>
    /// Resolves and returns the underlying FlaUI <see cref="AutomationElement"/>, waiting up to <see cref="DefaultTimeout"/> for it to be attached.
    /// </summary>
    public AutomationElement Element => ResolveAttached(DefaultTimeout);

    /// <summary>
    /// Chains a child locator that finds a descendant by its <c>AutomationId</c> inside this locator.
    /// </summary>
    public Locator FindElement(string automationId) =>
        new(TryResolve, cf => cf.ByAutomationId(automationId), $"{_description} >> AutomationId='{automationId}'");

    /// <summary>
    /// Chains a child locator that finds a descendant matching a custom FlaUI condition inside this locator.
    /// </summary>
    public Locator FindElement(Func<ConditionFactory, ConditionBase> condition, string description = "custom condition") =>
        new(TryResolve, condition, $"{_description} >> {description}");

    /// <summary>
    /// Chains a child locator that finds a descendant by its <c>AutomationId</c> inside this locator.
    /// </summary>
    public Locator GetByTestId(string automationId) => FindElement(automationId);

    /// <summary>
    /// Chains a child locator that finds a descendant by its visible text (<c>Name</c> property) inside this locator.
    /// </summary>
    public Locator GetByText(string text) =>
        new(TryResolve, cf => cf.ByName(text), $"{_description} >> Text='{text}'");

    /// <summary>
    /// Chains a child locator that finds a descendant by its <see cref="ControlType"/> and optional name inside this locator.
    /// </summary>
    public Locator GetByRole(ControlType role, string? name = null) =>
        new(
            TryResolve,
            cf => name is null
                ? cf.ByControlType(role)
                : cf.ByControlType(role).And(cf.ByName(name)),
            name is null ? $"{_description} >> Role={role}" : $"{_description} >> Role={role}, Name='{name}'");

    /// <summary>
    /// Waits until the element reaches the specified <paramref name="state"/> (defaults to <see cref="WaitForState.Visible"/>).
    /// </summary>
    public Locator WaitFor(WaitForState state = WaitForState.Visible, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;
        PollWithBackoff<bool>(
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
            effectiveTimeout,
            ActionBackoffMs,
            reason => new TimeoutException(
                $"WaitFor({state}) timed out after {effectiveTimeout.TotalMilliseconds}ms for '{_description}': {reason}"));

        return this;
    }

    /// <summary>
    /// Waits for the element to be visible, stable, and enabled, then clicks it.
    /// Uses UIA patterns (<c>Invoke</c>, <c>Toggle</c>, <c>SelectionItem</c>) unless <paramref name="forceMouse"/> is <c>true</c>.
    /// </summary>
    public void Click(bool forceMouse = false, TimeSpan? timeout = null)
    {
        var element = ResolveActionable(
            timeout ?? DefaultTimeout,
            requireVisible: true,
            requireStable: true,
            requireEnabled: true,
            requireEditable: false);

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
        ResolveActionable(
            timeout ?? DefaultTimeout,
            requireVisible: true,
            requireStable: true,
            requireEnabled: true,
            requireEditable: false).DoubleClick();

    /// <summary>
    /// Waits for the element to be visible, stable, and enabled, then right-clicks it with the mouse.
    /// </summary>
    public void RightClick(TimeSpan? timeout = null) =>
        ResolveActionable(
            timeout ?? DefaultTimeout,
            requireVisible: true,
            requireStable: true,
            requireEnabled: true,
            requireEditable: false).RightClick();

    /// <summary>
    /// Waits for the input element to be visible, enabled, and editable, then sets its text value.
    /// </summary>
    public void Fill(string value, TimeSpan? timeout = null)
    {
        var element = ResolveActionable(
            timeout ?? DefaultTimeout,
            requireVisible: true,
            requireStable: false,
            requireEnabled: true,
            requireEditable: true);

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
        var element = ResolveActionable(
            timeout ?? DefaultTimeout,
            requireVisible: true,
            requireStable: false,
            requireEnabled: true,
            requireEditable: false);

        element.Focus();
        Keyboard.Type(key);
    }

    /// <summary>
    /// Waits for the element to be visible and enabled, focuses it, and types the text character by character.
    /// </summary>
    public void Type(string text, TimeSpan? timeout = null)
    {
        var element = ResolveActionable(
            timeout ?? DefaultTimeout,
            requireVisible: true,
            requireStable: false,
            requireEnabled: true,
            requireEditable: false);

        element.Focus();
        Keyboard.Type(text);
    }

    /// <summary>
    /// Waits for the element to be attached and returns its text (<c>Value</c>, <c>Text</c> pattern, or <c>Name</c>).
    /// </summary>
    public string TextContent(TimeSpan? timeout = null) =>
        ReadTextContent(ResolveAttached(timeout ?? DefaultTimeout));

    /// <summary>
    /// Waits for the element to be attached and returns its text (alias for <see cref="TextContent"/>).
    /// </summary>
    public string Text(TimeSpan? timeout = null) => TextContent(timeout);

    /// <summary>
    /// Waits for the input element to be attached and returns its current value.
    /// </summary>
    public string InputValue(TimeSpan? timeout = null) =>
        ReadInputValue(ResolveAttached(timeout ?? DefaultTimeout));

    /// <summary>
    /// Waits for the checkbox or radio button to be visible, stable, and enabled, then checks it.
    /// </summary>
    public void Check(TimeSpan? timeout = null)
    {
        var element = ResolveActionable(
            timeout ?? DefaultTimeout,
            requireVisible: true,
            requireStable: true,
            requireEnabled: true,
            requireEditable: false);

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
        var element = ResolveActionable(
            timeout ?? DefaultTimeout,
            requireVisible: true,
            requireStable: true,
            requireEnabled: true,
            requireEditable: false);

        var checkBox = element.AsCheckBox();
        if (checkBox.IsChecked != false)
        {
            checkBox.IsChecked = false;
        }
    }

    /// <summary>
    /// Waits for the combo box to be visible, stable, and enabled, then selects an item by its text.
    /// </summary>
    public void SelectOption(string textOrValue, TimeSpan? timeout = null)
    {
        var element = ResolveActionable(
            timeout ?? DefaultTimeout,
            requireVisible: true,
            requireStable: true,
            requireEnabled: true,
            requireEditable: false);

        element.AsComboBox().Select(textOrValue);
    }

    /// <summary>
    /// Waits for the combo box to be visible, stable, and enabled, then selects an item by its zero-based index.
    /// </summary>
    public void SelectOption(int index, TimeSpan? timeout = null)
    {
        var element = ResolveActionable(
            timeout ?? DefaultTimeout,
            requireVisible: true,
            requireStable: true,
            requireEnabled: true,
            requireEditable: false);

        element.AsComboBox().Select(index);
    }

    /// <summary>
    /// Returns <c>true</c> immediately if the element is currently attached and visible (does not wait).
    /// Use <c>Expect(locator).ToBeVisible()</c> to wait for visibility.
    /// </summary>
    public bool IsVisible() => IsElementVisible(TryResolve());

    /// <summary>
    /// Returns <c>true</c> immediately if the element is absent or hidden (does not wait).
    /// Use <c>Expect(locator).ToBeHidden()</c> to wait for the element to hide.
    /// </summary>
    public bool IsHidden() => !IsVisible();

    /// <summary>
    /// Waits for the element to be attached and returns whether it is enabled.
    /// </summary>
    public bool IsEnabled(TimeSpan? timeout = null) =>
        ResolveAttached(timeout ?? DefaultTimeout).Properties.IsEnabled.ValueOrDefault;

    /// <summary>
    /// Waits for the checkbox or radio button to be attached and returns whether it is checked.
    /// </summary>
    public bool IsChecked(TimeSpan? timeout = null) =>
        ReadIsChecked(ResolveAttached(timeout ?? DefaultTimeout), _description);

    internal AutomationElement? TryResolve()
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

    internal static bool IsElementVisible(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            if (element.Properties.IsOffscreen.ValueOrDefault)
            {
                return false;
            }

            if (element.Properties.BoundingRectangle.TryGetValue(out var rect))
            {
                return rect.Width > 0 && rect.Height > 0;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static string ReadTextContent(AutomationElement element)
    {
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

    internal static string ReadInputValue(AutomationElement element)
    {
        if (element.Patterns.Value.IsSupported)
        {
            return element.Patterns.Value.Pattern.Value.Value ?? string.Empty;
        }

        return element.AsTextBox().Text ?? string.Empty;
    }

    internal static bool ReadIsChecked(AutomationElement element, string description)
    {
        if (element.Patterns.Toggle.IsSupported)
        {
            return element.Patterns.Toggle.Pattern.ToggleState.Value == ToggleState.On;
        }

        if (element.Patterns.SelectionItem.IsSupported)
        {
            return element.Patterns.SelectionItem.Pattern.IsSelected.Value;
        }

        throw new InvalidOperationException($"Element does not support Toggle or SelectionItem pattern: {description}");
    }

    private AutomationElement ResolveAttached(TimeSpan timeout) =>
        PollWithBackoff<AutomationElement>(
            () =>
            {
                var element = TryResolve();
                return element is not null
                    ? (true, element, null)
                    : (false, null, "element not found");
            },
            timeout,
            ActionBackoffMs,
            reason => new TimeoutException(
                $"Timed out after {timeout.TotalMilliseconds}ms waiting for '{_description}': {reason}"))!;

    private AutomationElement ResolveActionable(
        TimeSpan timeout,
        bool requireVisible,
        bool requireStable,
        bool requireEnabled,
        bool requireEditable)
    {
        Rectangle? previousBounds = null;
        var hasPreviousBounds = false;

        return PollWithBackoff<AutomationElement>(
            () =>
            {
                var element = TryResolve();
                if (element is null)
                {
                    hasPreviousBounds = false;
                    return (false, null, "element is not attached");
                }

                try
                {
                    if (requireVisible && !IsElementVisible(element))
                    {
                        hasPreviousBounds = false;
                        return (false, null, "element is not visible");
                    }

                    if (requireStable && element.Properties.BoundingRectangle.TryGetValue(out var bounds))
                    {
                        if (!hasPreviousBounds || previousBounds != bounds)
                        {
                            previousBounds = bounds;
                            hasPreviousBounds = true;
                            Thread.Sleep(20);
                            if (element.Properties.BoundingRectangle.TryGetValue(out var currentBounds) && currentBounds != bounds)
                            {
                                previousBounds = currentBounds;
                                return (false, null, "element is not stable");
                            }
                        }
                    }

                    if (requireEnabled && !element.Properties.IsEnabled.ValueOrDefault)
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
                    hasPreviousBounds = false;
                    return (false, null, ex.Message);
                }
            },
            timeout,
            ActionBackoffMs,
            reason => new TimeoutException(
                $"Action timed out after {timeout.TotalMilliseconds}ms on '{_description}': {reason}"))!;
    }

    internal static T? PollWithBackoff<T>(
        Func<(bool Done, T? Value, string? LastFailure)> step,
        TimeSpan timeout,
        ReadOnlySpan<int> backoffMs,
        Func<string?, Exception> onTimeout)
    {
        var stopwatch = Stopwatch.StartNew();
        var attempt = 0;

        while (true)
        {
            var (done, value, lastFailure) = step();
            if (done)
            {
                return value;
            }

            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw onTimeout(lastFailure);
            }

            var delayMs = backoffMs[Math.Min(attempt, backoffMs.Length - 1)];
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

/// <summary>
/// Auto-retrying assertions for a <see cref="Locator"/> (polls up to <see cref="DefaultTimeout"/> with backoff).
/// </summary>
public sealed class LocatorAssertions
{
    /// <summary>
    /// Default timeout for assertions (5 seconds).
    /// </summary>
    public static TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(5);

    private readonly Locator _locator;
    private readonly bool _isNot;

    /// <summary>
    /// Initializes assertions for <paramref name="locator"/>.
    /// </summary>
    public LocatorAssertions(Locator locator, bool isNot = false)
    {
        _locator = locator;
        _isNot = isNot;
    }

    /// <summary>
    /// Negates the assertion (e.g. <c>Expect(locator).Not.ToBeVisible()</c>).
    /// </summary>
    public LocatorAssertions Not => new(_locator, !_isNot);

    /// <summary>
    /// Asserts that the element is attached and visible within the timeout.
    /// </summary>
    public void ToBeVisible(TimeSpan? timeout = null) =>
        PollAssertion(
            () =>
            {
                var visible = _locator.IsVisible();
                return (visible, visible ? "visible" : "hidden");
            },
            expectedDescription: "to be visible",
            timeout);

    /// <summary>
    /// Asserts that the element is absent or hidden within the timeout.
    /// </summary>
    public void ToBeHidden(TimeSpan? timeout = null) =>
        PollAssertion(
            () =>
            {
                var hidden = _locator.IsHidden();
                return (hidden, hidden ? "hidden" : "visible");
            },
            expectedDescription: "to be hidden",
            timeout);

    /// <summary>
    /// Asserts that the element is attached and enabled within the timeout.
    /// </summary>
    public void ToBeEnabled(TimeSpan? timeout = null) =>
        PollAssertion(
            () =>
            {
                var element = _locator.TryResolve();
                if (element is null)
                {
                    return (null, "element not found");
                }

                var enabled = element.Properties.IsEnabled.ValueOrDefault;
                return (enabled, enabled ? "enabled" : "disabled");
            },
            expectedDescription: "to be enabled",
            timeout);

    /// <summary>
    /// Asserts that the element is attached and disabled within the timeout.
    /// </summary>
    public void ToBeDisabled(TimeSpan? timeout = null) =>
        PollAssertion(
            () =>
            {
                var element = _locator.TryResolve();
                if (element is null)
                {
                    return (null, "element not found");
                }

                var disabled = !element.Properties.IsEnabled.ValueOrDefault;
                return (disabled, disabled ? "disabled" : "enabled");
            },
            expectedDescription: "to be disabled",
            timeout);

    /// <summary>
    /// Asserts that the checkbox or radio button is checked within the timeout.
    /// </summary>
    public void ToBeChecked(TimeSpan? timeout = null) =>
        PollAssertion(
            () =>
            {
                var element = _locator.TryResolve();
                if (element is null)
                {
                    return (null, "element not found");
                }

                var isChecked = Locator.ReadIsChecked(element, _locator.Description);
                return (isChecked, isChecked ? "checked" : "unchecked");
            },
            expectedDescription: "to be checked",
            timeout);

    /// <summary>
    /// Asserts that the element's text exactly equals <paramref name="expected"/> within the timeout.
    /// </summary>
    public void ToHaveText(string expected, TimeSpan? timeout = null) =>
        PollAssertion(
            () =>
            {
                var element = _locator.TryResolve();
                if (element is null)
                {
                    return (null, "element not found");
                }

                var actual = Locator.ReadTextContent(element);
                return (string.Equals(actual, expected, StringComparison.Ordinal), $"'{actual}'");
            },
            expectedDescription: $"to have text '{expected}'",
            timeout);

    /// <summary>
    /// Asserts that the element's text contains <paramref name="expected"/> within the timeout.
    /// </summary>
    public void ToContainText(string expected, TimeSpan? timeout = null) =>
        PollAssertion(
            () =>
            {
                var element = _locator.TryResolve();
                if (element is null)
                {
                    return (null, "element not found");
                }

                var actual = Locator.ReadTextContent(element);
                return (actual.Contains(expected, StringComparison.Ordinal), $"'{actual}'");
            },
            expectedDescription: $"to contain text '{expected}'",
            timeout);

    /// <summary>
    /// Asserts that the input element's value equals <paramref name="expected"/> within the timeout.
    /// </summary>
    public void ToHaveValue(string expected, TimeSpan? timeout = null) =>
        PollAssertion(
            () =>
            {
                var element = _locator.TryResolve();
                if (element is null)
                {
                    return (null, "element not found");
                }

                var actual = Locator.ReadInputValue(element);
                return (string.Equals(actual, expected, StringComparison.Ordinal), $"'{actual}'");
            },
            expectedDescription: $"to have value '{expected}'",
            timeout);

    private void PollAssertion(
        Func<(bool? Matched, string Actual)> check,
        string expectedDescription,
        TimeSpan? timeout)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;
        Locator.PollWithBackoff<bool>(
            () =>
            {
                try
                {
                    var (matched, actual) = check();
                    if (matched is null)
                    {
                        return (false, false, actual);
                    }

                    var passed = _isNot ? !matched.Value : matched.Value;
                    return (passed, passed, actual);
                }
                catch (Exception ex)
                {
                    return (false, false, ex.Message);
                }
            },
            effectiveTimeout,
            Locator.ExpectBackoffMs,
            lastActual => new InvalidOperationException(
                $"Expect('{_locator.Description}') {(_isNot ? "not " : string.Empty)}{expectedDescription} failed after {effectiveTimeout.TotalMilliseconds}ms. Actual: {lastActual}"));
    }
}
