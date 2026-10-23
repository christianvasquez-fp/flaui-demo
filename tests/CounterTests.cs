using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using NUnit.Framework;
using NExpect;
using static NExpect.Expectations;

namespace flaui_demo.Tests;

public class CounterTests
{
    Application app = null!;
    UIA3Automation automation = null!;
    Window window = null!;

    [OneTimeSetUp]
    public void SetUp()
    {
        app = Application.Launch(Path.Combine(AppContext.BaseDirectory, "flaui-demo.exe"));
        automation = new UIA3Automation();
        window = app.GetMainWindow(automation)!;
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        automation.Dispose();
        app.Close();
    }

    [Test]
    public void Increment_DisplaysOne()
    {
        window.GetByTestId("Increment").Click();

        Expect(window.GetByTestId("Count").Text()).To.Equal("1");
    }

    [Test]
    public void Decrement_DisplaysZero()
    {
        window.GetByTestId("Decrement").Click();

        Expect(window.GetByTestId("Count").Text()).To.Equal("0");
    }
}
