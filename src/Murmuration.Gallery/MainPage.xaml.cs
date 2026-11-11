using Microsoft.UI.Text;

namespace Murmuration.Gallery;

/// <summary>
/// One tab per control. All data here is sample data and every "network" delay is simulated.
/// </summary>
public sealed partial class MainPage : Page
{
    const string StreamText =
        "A murmuration starts small. A few hundred starlings arrive over the reedbed about half an hour before sunset, and more groups join them from every direction.\n" +
        "As the light fades the flock tightens and begins to turn as one. Dark bands roll across it whenever the birds bunch together. Then, all at once, they drop into the reeds and the sky is empty.";
    const string AnswerText =
        "Go to the reedbed, east hide. It has held the largest flock all month, 62,000 at last count.\n" +
        "Wind is light from the south-west, so the birds should wheel over the water. Arrive by 15:55.";

    static readonly (string place, string when, string count)[][] Logs =
    {
        new[] { ("Reedbed, east hide", "14 Nov, 16:08", "62,000"), ("Old pier", "11 Nov, 16:14", "38,500"), ("Gasworks", "9 Nov, 16:19", "21,000"), ("Quarry lake", "6 Nov, 16:25", "9,400") },
        new[] { ("Reedbed, east hide", "15 Nov, 16:06", "71,500"), ("Town bridge", "15 Nov, 16:20", "4,200"), ("Old pier", "13 Nov, 16:11", "40,100"), ("Gasworks", "12 Nov, 16:17", "18,800") },
    };

    int _uploadRun, _log, _chatRun;

    public MainPage()
    {
        this.InitializeComponent();
        Bar.DisplayedValueChanged += (_, v) => Pct.Text = $"{Math.Round(v * 100)}%";
        Bar.Completed += (_, _) => ProgressMessage.Text = "24 of 24 uploaded";
        Stream.ReturnTo = StreamingPrompt;
        Answer.ReturnTo = ThinkingPrompt;
        FillRows(Logs[0]);
        Loaded += (_, _) =>
        {
            // MURMURATION_DEMO=progress|rewind|thinking|streaming|loading opens that tab and plays it, for unattended checks.
            switch (Environment.GetEnvironmentVariable("MURMURATION_DEMO"))
            {
                case "progress": OnRunUpload(this, new RoutedEventArgs()); break;
                case "rewind": OnRunUpload(this, new RoutedEventArgs()); _ = Task.Delay(9000).ContinueWith(_ => DispatcherQueue.TryEnqueue(() => OnRunUpload(this, new RoutedEventArgs()))); break;
                case "thinking": TabThinking.IsChecked = true; OnSendThinking(this, new RoutedEventArgs()); break;
                case "streaming": TabStreaming.IsChecked = true; OnSendStreaming(this, new RoutedEventArgs()); break;
                case "loading": TabLoading.IsChecked = true; OnRefresh(this, new RoutedEventArgs()); break;
            }
        };
    }

    void OnTab(object sender, RoutedEventArgs e)
    {
        if (ProgressPanel == null) return;
        ProgressPanel.Visibility = TabProgress.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ThinkingPanel.Visibility = TabThinking.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        StreamingPanel.Visibility = TabStreaming.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        LoadingPanel.Visibility = TabLoading.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnSlow(object sender, RoutedEventArgs e) => Host.TimeScale = Slow.IsChecked == true ? 0.25 : 1;

    // ---------- Progress: a simulated upload with a stall in the middle ----------
    async void OnRunUpload(object sender, RoutedEventArgs e)
    {
        int run = ++_uploadRun;
        (double t, double p)[] keys = { (0, 0), (1.1, 0), (2.3, 0.36), (3.0, 0.56), (4.1, 0.58), (5.0, 0.84), (5.7, 1) };
        if (Bar.Value > 0) { Bar.Value = 0; await Task.Delay(700); }
        ProgressMessage.Text = "Uploading 24 photos";
        var start = DateTime.UtcNow;
        while (run == _uploadRun)
        {
            double t = (DateTime.UtcNow - start).TotalSeconds * Host.TimeScale, p = 1;
            for (int k = 1; k < keys.Length; k++)
                if (t < keys[k].t) { var a = keys[k - 1]; var b = keys[k]; p = a.p + (b.p - a.p) * (t - a.t) / (b.t - a.t); break; }
            Bar.Value = p;
            if (p >= 1) break;
            await Task.Delay(50);
        }
    }

    void OnResetUpload(object sender, RoutedEventArgs e)
    {
        _uploadRun++; Bar.Value = 0; ProgressMessage.Text = "Ready";
    }

    // ---------- Thinking: the prompt lifts off, the flock changes shape per stage, then writes the answer ----------
    async void OnSendThinking(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ThinkingPrompt.Text)) return;
        int run = ++_chatRun;
        Answer.Clear();
        await Thinking.StartAsync(ThinkingPrompt);   // captures the typed text as birds
        ThinkingPrompt.Text = "";                     // then the composer clears, as it would on send
        ThinkingStatus.Text = "Reading the question";
        if (!await Wait(1900, run)) return;
        Thinking.State = FlockThinkingState.Tools; ThinkingStatus.Text = "Checking wind and four roosts";
        if (!await Wait(1700, run)) return;
        Thinking.State = FlockThinkingState.Synthesizing; ThinkingStatus.Text = "Putting it together";
        if (!await Wait(1100, run)) return;
        Thinking.HandOffTo(Answer); ThinkingStatus.Text = "Writing";
        await StreamInto(Answer, AnswerText, run);
        if (run == _chatRun) { ThinkingStatus.Text = "Answered (sample response)"; ThinkingPrompt.Text = "Why do starlings flock?"; }
    }

    // ---------- Streaming: the prompt becomes the write head ----------
    async void OnSendStreaming(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(StreamingPrompt.Text)) return;
        int run = ++_chatRun;
        string prompt = StreamingPrompt.Text;
        Stream.Clear();
        await Stream.BeginAsync(StreamingPrompt);
        StreamingPrompt.Text = "";
        StreamingStatus.Text = "Waiting for first token";
        if (!await Wait(1700, run)) return;
        StreamingStatus.Text = "Streaming";
        await StreamInto(Stream, StreamText, run);
        if (run == _chatRun) { StreamingStatus.Text = "Done (sample response)"; StreamingPrompt.Text = prompt; }
    }

    async Task StreamInto(FlockStreamingText target, string text, int run)
    {
        var rng = new Random();
        foreach (string paragraph in text.Split('\n'))
        {
            foreach (string word in paragraph.Split(' '))
            {
                if (run != _chatRun) return;
                target.Append(word + " ");
                int gap = 70 + rng.Next(60) + (word.EndsWith('.') || word.EndsWith(',') ? 160 : 0);
                await Task.Delay((int)(gap / Host.TimeScale));
            }
            target.Append("\n");
            if (!await Wait(900, run)) return;   // a pause between paragraphs: the flock keeps circling
        }
        target.Complete();
    }

    async Task<bool> Wait(int ms, int run)
    {
        await Task.Delay((int)(ms / Host.TimeScale));
        return run == _chatRun;
    }

    // ---------- Loading: stale rows lift off, new rows land ----------
    async void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (Loader.IsLoading) return;
        Loader.IsLoading = true;
        await Task.Delay((int)(2200 / Host.TimeScale));
        _log = 1 - _log; FillRows(Logs[_log]);
        Loader.IsLoading = false;
    }

    void FillRows((string place, string when, string count)[] rows)
    {
        Rows.Children.Clear();
        foreach (var (place, when, count) in rows)
        {
            var grid = new Grid();
            var left = new StackPanel { Spacing = 2 };
            left.Children.Add(new TextBlock { Text = place, FontWeight = FontWeights.SemiBold, FontSize = 16 });
            left.Children.Add(new TextBlock { Text = when, FontSize = 12, Opacity = 0.7 });
            grid.Children.Add(left);
            grid.Children.Add(new TextBlock { Text = count, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
            Rows.Children.Add(grid);
        }
    }
}
