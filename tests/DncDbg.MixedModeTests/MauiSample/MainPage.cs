namespace DncDbg.MixedMode.Maui;

public sealed class MainPage : ContentPage
{
    private readonly Label _status;
    private int _runCount;

    public MainPage()
    {
        Title = "DNCDbg Mixed Mode";

        _status = new Label
        {
            AutomationId = "MixedModeStatus",
            Text = "Ready",
            FontSize = 18,
            HorizontalTextAlignment = TextAlignment.Center,
        };

        var run = new Button
        {
            AutomationId = "RunMixedMode",
            Text = "Run C# → C++ → C# callback",
        };
        run.Clicked += RunMixedMode;

        Content = new VerticalStackLayout
        {
            Padding = 32,
            Spacing = 20,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Label
                {
                    Text = "Managed + native callback fixture",
                    FontSize = 28,
                    HorizontalTextAlignment = TextAlignment.Center,
                },
                new Label
                {
                    Text = "19 + 23 = 42; callback returns 84; C++ returns 126",
                    HorizontalTextAlignment = TextAlignment.Center,
                },
                run,
                _status,
            },
        };
    }

    private void RunMixedMode(object? sender, EventArgs e)
    {
        int run = ++_runCount;
        _status.Text = $"Running:{run}";
        try
        {
            _status.Text = $"{MixedModeScenario.Run(19, 23)}:{run}";
        }
        catch (Exception error)
        {
            _status.Text = error.GetType().Name + ": " + error.Message;
        }
    }
}
