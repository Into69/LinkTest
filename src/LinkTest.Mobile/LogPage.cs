namespace LinkTest.Mobile;

/// <summary>Event log (test summaries, tunnel transitions, updates).</summary>
sealed class LogPage : ContentPage
{
    readonly AppController _app = AppController.Instance;
    readonly Label _text = new() { FontFamily = "monospace", FontSize = 11, LineBreakMode = LineBreakMode.CharacterWrap };
    readonly ScrollView _scroll;
    bool _dirty;

    public LogPage()
    {
        Title = "Log";
        BackgroundColor = Colors.White;
        _scroll = new ScrollView { Content = new VerticalStackLayout { Padding = new Thickness(8), Children = { _text } } };
        Content = _scroll;
        _app.LogAdded += _ => { _dirty = true; if (IsVisible) Render(); };
        Render();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_dirty) Render();
    }

    void Render()
    {
        _dirty = false;
        var lines = _app.LogLines;
        _text.Text = lines.Count == 0 ? "No events yet." : string.Join("\n", lines.Skip(Math.Max(0, lines.Count - 300)));
        _ = _scroll.ScrollToAsync(0, double.MaxValue, false);
    }
}
