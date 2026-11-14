> **Moved to uno-lab.** This project now lives in the `murmur/` folder of [noknowscode/uno-lab](https://github.com/noknowscode/uno-lab/tree/main/murmur) (private). This repository is archived and kept for its history.

# Murmuration for Uno Platform

Four controls that use a flock of birds as a status indicator. See `SPEC.md` for the design.

## Run the gallery

```powershell
dotnet run --project src/Murmuration.Gallery -f net10.0-desktop
```

## Use in an app

Reference `src/Murmuration`. The app must render with Skia. Wrap the region the birds may cross in one `FlockHost`:

```xml
<m:FlockHost xmlns:m="using:Murmuration">
    <StackPanel Spacing="16">
        <m:FlockProgressBar Height="200" Value="{x:Bind Upload.Progress, Mode=OneWay}" />
        <m:FlockThinkingIndicator x:Name="Thinking" Height="160" />
        <m:FlockStreamingText x:Name="Answer" />
        <m:FlockLoader IsLoading="{x:Bind Feed.IsBusy, Mode=OneWay}">
            <ItemsRepeater ItemsSource="{x:Bind Feed.Items}" />
        </m:FlockLoader>
        <TextBox x:Name="Prompt" />
    </StackPanel>
</m:FlockHost>
```

```csharp
// The typed prompt becomes the flock, thinks, then writes the answer.
await Thinking.StartAsync(Prompt);   // capture the text as birds
Prompt.Text = "";                    // then clear the composer
Thinking.State = FlockThinkingState.Tools;
Thinking.State = FlockThinkingState.Synthesizing;
Thinking.HandOffTo(Answer);
await foreach (var token in stream) Answer.Append(token);
Answer.Complete();
```

| Control | Inputs | Outputs |
|---|---|---|
| `FlockProgressBar` | `Value` (0 to 1), `BirdCount`, `Foreground`, `AccentBrush`, `ReduceMotion` | `DisplayedValue`, `DisplayedValueChanged`, `Completed` |
| `FlockThinkingIndicator` | `State`, `BirdCount`, `Foreground`, `StartAsync(source)`, `Stop()`, `HandOffTo(text)` | `IsFlockAvailable` |
| `FlockStreamingText` | `Append(token)`, `Complete()`, `Clear()`, `BeginAsync(source)`, `ReturnTo`, font properties, `ReduceMotion` | `Text`, `Completed` |
| `FlockLoader` | `IsLoading`, `Content`, `BirdCount`, `ReduceMotion` | `Revealed` |

`FlockHost.TimeScale = 0.25` slows everything for study.
