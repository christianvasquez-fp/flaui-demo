using FlaUI.Core.AutomationElements;

namespace flaui_demo.Tests;

public class Counter(Window window)
{

    public string Count
    {
        get => window.GetByTestId("Count").Text();
    }

    public void Increment()
    {
        window.GetByTestId("Increment").Click();
    }

    public void Decrement()
    {
        window.GetByTestId("Decrement").Click();
    }
}
