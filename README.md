# FlaUI Demo

Minimal WPF desktop application and UI automation test suite demonstrating a Playwright-inspired locator and assertion layer over [FlaUI](https://github.com/FlaUI/FlaUI) (`UIA3`).

## Getting Started

Prerequisites: .NET 10 SDK on Windows.

```powershell
# Run the WPF app
dotnet run

# Run the UI automation tests
dotnet test tests/flaui-demo.Tests.csproj
```

## Folder Structure

```text
flaui-demo/
├── App.xaml / App.xaml.cs               # WPF application entry point
├── MainWindow.xaml / MainWindow.xaml.cs # Counter UI and click handlers
├── flaui-demo.csproj                    # WPF app project (net10.0-windows)
└── tests/
    ├── AutomationElementExtensions.cs   # Drop-in Playwright-style locators & assertions for FlaUI
    ├── CounterTests.cs                  # NUnit UI automation tests
    └── flaui-demo.Tests.csproj          # Test project (NUnit + FlaUI.UIA3)
```

## Overview

### Counter App (`MainWindow`)
A simple WPF window with:
- `Heading` (`TextBlock`): title text.
- `Decrement` (`Button`): decrements the counter, floored at `0`.
- `Count` (`Label`): displays the current count (`0` initially).
- `Increment` (`Button`): increments the counter by `1`.

Each control sets `x:Name`, which WPF exposes as the UI Automation `AutomationId`.

### Extensions Design (`tests/AutomationElementExtensions.cs`)
Raw UI Automation tests are often flaky or verbose because elements render asynchronously, animate, or require different UIA patterns (`Invoke`, `Value`, `Toggle`, `SelectionItem`). `AutomationElementExtensions.cs` brings Playwright's ergonomics and reliability model to FlaUI in a **single, copy-pasteable file**:

1. **Zero-Config Drop-In (`namespace FlaUI.Core.AutomationElements`)**
   - Placed in `FlaUI.Core.AutomationElements` with `#nullable enable` and explicit `using` directives so copying the file into any FlaUI test project immediately lights up IntelliSense on `Window` and `AutomationElement` without extra imports.

2. **Lazy, Chainable Locators (`GetByTestId`, `GetByText`, `GetByRole`, `FindElement`)**
   - Calling `window.GetByTestId("Increment")` or chaining `parent.GetByRole(ControlType.Button, "+")` does not touch the UI tree. Resolution is deferred until an action or query executes, so chained locators never go stale if a parent container re-renders.

3. **Playwright Auto-Waiting & Backoff Polling**
   - **Actions** (`Click`, `Fill`, `Check`, `SelectOption`, `Press`, `Type`) poll with Playwright's backoff schedule (`[20, 50, 100, 100, 500, ...]` ms up to `30s` default) and verify actionability inside the retry loop:
     - **Attached**: present in the UIA tree.
     - **Visible**: not offscreen and has a non-zero bounding box.
     - **Stable**: bounding rectangle is unchanged across consecutive checks (`Click`, `Check`, `SelectOption`).
     - **Enabled & Editable**: `IsEnabled` (and not `IsReadOnly` for `Fill`/`Clear`).
   - **State Queries** (`IsVisible`, `IsHidden`) return immediately (`0ms` wait), while `Text()` / `TextContent()`, `InputValue()`, `IsEnabled()`, and `IsChecked()` wait for attachment and read once, pairing cleanly with `NExpect` (`Expect(window.GetByTestId("Count").Text()).To.Equal("1")`).

4. **Pattern-First Interaction with Mouse/Control Fallbacks**
   - `Click()` prefers UIA patterns (`Invoke`, `Toggle`, `SelectionItem`) for fast, deterministic execution even when another window is in the foreground, falling back to physical mouse clicks (`forceMouse: true` or when no pattern applies).
   - `Fill()` and `TextContent()` check `ValuePattern` and `TextPattern` before falling back to control wrappers or `Name`.
