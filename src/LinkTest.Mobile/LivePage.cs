using LinkTest.Core;

namespace LinkTest.Mobile;

/// <summary>Live tiles, charts and the artificial-loss slider.</summary>
sealed class LivePage : ContentPage
{
    readonly AppController _app = AppController.Instance;
    readonly Label _status = new() { FontSize = 12, TextColor = Ui.Muted };
    readonly Tile _up = new("Upload (client→server)"), _down = new("Download (server→client)"), _rtt = new("Round-trip time"),
        _jitter = new("Jitter ↑ / ↓ (ms)"), _loss = new("Loss ↑ / ↓ (%)"), _gap = new("Longest gap ↑ / ↓"),
        _retrans = new("TCP retransmits ↑ / ↓"), _mos = new("VoIP MOS (est.)"), _bloat = new("Bufferbloat"), _time = new("Elapsed");
    readonly ChartDrawable _tput = new() { Title = "Throughput", Unit = "Mbps" };
    readonly ChartDrawable _lat = new() { Title = "Latency", Unit = "ms" };
    readonly ChartDrawable _lossChart = new() { Title = "Packet loss", Unit = "%" };
    readonly GraphicsView _tputView, _latView, _lossView;
    readonly Slider _lossSlider = new() { Minimum = 0, Maximum = 50, MinimumTrackColor = Ui.Accent };
    readonly Label _lossValue = new() { FontSize = 13, VerticalOptions = LayoutOptions.Center, WidthRequest = 52 };

    public LivePage()
    {
        Title = "Live";
        IconImageSource = null;
        BackgroundColor = Ui.PageBg;

        _tput.AddSeries("Up", Color.FromArgb("#0078D4"));
        _tput.AddSeries("Down", Color.FromArgb("#10893E"));
        _lat.AddSeries("RTT", Color.FromArgb("#CA5010"));
        _lat.AddSeries("Jitter ↑", Color.FromArgb("#0078D4"));
        _lat.AddSeries("Jitter ↓", Color.FromArgb("#10893E"));
        _lossChart.AddSeries("Loss ↑", Color.FromArgb("#0078D4"));
        _lossChart.AddSeries("Loss ↓", Color.FromArgb("#C42B1C"));
        _tputView = new GraphicsView { Drawable = _tput, HeightRequest = 170 };
        _latView = new GraphicsView { Drawable = _lat, HeightRequest = 170 };
        _lossView = new GraphicsView { Drawable = _lossChart, HeightRequest = 150 };

        var tiles = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 6, RowSpacing = 6 };
        Tile[] all = { _up, _down, _rtt, _jitter, _loss, _gap, _retrans, _mos, _bloat, _time };
        for (int i = 0; i < all.Length; i++)
        {
            if (i % 2 == 0) tiles.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            tiles.Add(all[i], i % 2, i / 2);
        }

        _lossSlider.Value = _app.Settings.ArtificialLossPct;
        _lossValue.Text = $"{_lossSlider.Value:0}%";
        _lossSlider.ValueChanged += (_, e) =>
        {
            double v = Math.Round(e.NewValue);
            _lossValue.Text = $"{v:0}%";
            _app.SetArtificialLoss(v);
        };
        var lossRow = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) } };
        lossRow.Add(_lossSlider, 0);
        lossRow.Add(_lossValue, 1);

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(10),
                Spacing = 6,
                Children =
                {
                    _status, tiles,
                    Ui.Card(new VerticalStackLayout
                    {
                        Children = { new Label { Text = "Artificial loss (testing aid, adjustable while running)", FontSize = 12, TextColor = Ui.Muted }, lossRow },
                    }),
                    Ui.Card(_tputView), Ui.Card(_latView), Ui.Card(_lossView),
                },
            },
        };

        _app.Sampled += OnSample;
        _app.StateChanged += OnState;
        OnState();
    }

    void OnState()
    {
        _status.Text = _app.Status;
        if (_app.Connecting)
        {
            _tput.Clear(); _lat.Clear(); _lossChart.Clear();
            foreach (var t in new[] { _up, _down, _rtt, _jitter, _loss, _gap, _retrans, _mos, _bloat, _time }) t.Set("–");
            Invalidate();
        }
    }

    void OnSample(StatSample s)
    {
        var cfg = _app.Session?.Config;
        _up.Set(Ui.Mbps(s.UpMbps), s.UpReportStale ? $"no report {Ui.Fmt(s.UpReportAgeSec, "0.0")} s"
            : s.Mixed ? $"UDP {Ui.Mbps(s.UpMbpsUdp)} · TCP {Ui.Mbps(s.UpMbpsTcp)}" : $"sent {Ui.Mbps(s.TxMbps)}", s.UpReportStale ? Ui.Bad : null);
        _down.Set(Ui.Mbps(s.DownMbps), s.Mixed ? $"UDP {Ui.Mbps(s.DownMbpsUdp)} · TCP {Ui.Mbps(s.DownMbpsTcp)}" : "");
        _rtt.Set(Ui.Ms(s.RttMs), $"min {Ui.Fmt(s.RttMinMs, "0.0")} / max {Ui.Fmt(s.RttMaxMs, "0.0")}", Ui.Level(s.RttMs, 100, 250));
        _jitter.Set($"{Ui.Fmt(s.UpJitterMs, "0.0")} / {Ui.Fmt(s.DownJitterMs, "0.0")}", "", Ui.Level(Math.Max(Ui.Z(s.UpJitterMs), s.DownJitterMs), 10, 30));
        _loss.Set($"{Ui.Fmt(s.UpLossPct, "0.##")} / {Ui.Fmt(s.DownLossPct, "0.##")}",
            $"total {Ui.Fmt(s.UpLossTotalPct, "0.##")} / {Ui.Fmt(s.DownLossTotalPct, "0.##")}", Ui.Level(Math.Max(Ui.Z(s.UpLossPct), s.DownLossPct), 0.5, 3));
        _gap.Set($"{Ui.Fmt(s.UpMaxGapMs, "0")} / {Ui.Fmt(s.DownMaxGapMs, "0")}", "ms", Ui.Level(Math.Max(Ui.Z(s.UpMaxGapMs), s.DownMaxGapMs), 250, 1000));
        _retrans.Set(cfg?.UsesTcp == true ? $"{s.UpRetransBytes / 1024.0:0.#} / {s.DownRetransBytes / 1024.0:0.#}" : "n/a", cfg?.UsesTcp == true ? "KB/s" : "UDP only");
        _mos.Set(Ui.Fmt(s.Mos, "0.0"), "", s.Mos >= 4 ? Ui.Good : s.Mos >= 3.6 ? Ui.Warn : Ui.Bad);
        if (s.BaselinePhase) _bloat.Set("…", "measuring idle RTT");
        else if (s.BloatGrade != null)
            _bloat.Set(s.BloatGrade, $"+{s.BloatIncreaseMs:0.0} ms under load", s.BloatGrade is "A+" or "A" ? Ui.Good : s.BloatGrade is "B" or "C" ? Ui.Warn : Ui.Bad);
        else _bloat.Set("–", cfg?.BaselineSec == 0 ? "off / server < 1.6" : "measuring…");
        var el = TimeSpan.FromSeconds(s.ElapsedSec);
        _time.Set($"{(int)el.TotalHours}:{el.Minutes:00}:{el.Seconds:00}", cfg?.DurationSec > 0 ? $"of {cfg.DurationSec} s" : "until stopped");

        _tput.Push(s.UpMbps, s.DownMbps);
        _lat.Push(s.RttMs, s.UpJitterMs, s.DownJitterMs);
        _lossChart.Push(s.UpLossPct, s.DownLossPct);
        Invalidate();
    }

    void Invalidate()
    {
        _tputView.Invalidate();
        _latView.Invalidate();
        _lossView.Invalidate();
    }
}
