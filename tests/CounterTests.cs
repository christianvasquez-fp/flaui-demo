using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using NUnit.Framework;

namespace flaui_demo.Tests;

public class CounterTests
{
    private Application _app = null!;
    private UIA3Automation _automation = null!;
    private Window _window = null!;

    [OneTimeSetUp]
    public void SetUp()
    {
        _app = Application.Launch(Path.Combine(AppContext.BaseDirectory, "flaui-demo.exe"));
        _automation = new UIA3Automation();
        _window = _app.GetMainWindow(_automation)!;
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _automation.Dispose();
        _app.Close();
    }

    [Test]
    public void PlusButton_IncrementsCounter()
    {
        _window.FindFirstDescendant(condition => condition.ByAutomationId("Button_Plus"))?.AsButton().Invoke();

        var count = _window.FindFirstDescendant(condition => condition.ByAutomationId("Count"))?.AsLabel().Text;
        Assert.That(count, Is.EqualTo("1"));
    }

    [Test]
    public void MinusButton_DoesNotDecrementBelowZero()
    {
        _window.FindFirstDescendant(condition => condition.ByAutomationId("Button_Minus"))?.AsButton().Invoke();

        var count = _window.FindFirstDescendant(condition => condition.ByAutomationId("Count"))?.AsLabel().Text;
        Assert.That(count, Is.EqualTo("0"));
    }
}
