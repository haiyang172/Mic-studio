using System.Windows.Automation;
using MicStudio.WindowsAudio;

namespace MicStudio.App;

public partial class MainWindow
{
    private void RunTuningGuideChecks(Action<bool, string> check)
    {
        if (!UiSmoke || !LocalMode || engine.IsRunning || engine.IsRecording || engine.IsPlaying || calibrating)
            throw new InvalidOperationException("Tuning guide checks require an idle, isolated local workspace.");
        var previousPage = currentPage;
        var previousGain = Draft.InputGainDb;
        var previousSuggestion = suggestedGain;
        var previousVerification = State.DiscordVerifiedAt;
        var previousCapture = tuningSourceCapture;
        try
        {
            suggestedGain = null;
            ShowPage(0);
            SetSmokeDetails(true);
            UpdateLayout();
            T Find<T>(string id) where T : DependencyObject => Descendants<T>(PageContent)
                .Single(control => AutomationProperties.GetAutomationId(control) == id);

            var result = Find<TextBlock>("tuning-guide-result");
            var audition = Find<Button>("tuning-guide-audition");
            var apply = Find<Button>("tuning-guide-apply");
            var initiallyHidden = result.Visibility == Visibility.Collapsed;
            suggestedGain = previousGain == 8 ? 9 : 8;
            UpdateChrome();
            audition.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(initiallyHidden && result.Visibility == Visibility.Visible && apply.IsEnabled &&
                Draft.InputGainDb == previousGain && !engine.IsRunning && !engine.IsPlaying,
                "Tuning suggestion and preview request leave the gain and capture unchanged");

            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(Draft.InputGainDb == suggestedGain && !apply.IsEnabled && !engine.IsRunning && !engine.IsPlaying,
                "The tuning guide commits gain only on the explicit Apply action");

            var session = engine.SessionId;
            tuningSourceCapture = ("synthetic-ab-source.wav", session);
            CompleteTuningSourceCapture(new RecordingResult("different-source.wav", TimeSpan.Zero, "Synthetic", true) { SessionId = session });
            var keptAfterDifferentPath = tuningSourceCapture != null;
            CompleteTuningSourceCapture(new RecordingResult("synthetic-ab-source.wav", TimeSpan.Zero, "Synthetic", true) { SessionId = session - 1 });
            var keptAfterDifferentSession = tuningSourceCapture != null;
            CompleteTuningSourceCapture(new RecordingResult("synthetic-ab-source.wav", TimeSpan.Zero, "Synthetic", true) { SessionId = session });
            check(keptAfterDifferentPath && keptAfterDifferentSession && tuningSourceCapture == null && engine.SessionId == session,
                "Only a matching A/B recording path and session release the temporary capture reservation");
        }
        finally
        {
            vm.Change(settings => settings.InputGainDb = previousGain);
            suggestedGain = previousSuggestion;
            State.DiscordVerifiedAt = previousVerification;
            tuningSourceCapture = previousCapture;
            ShowPage(previousPage);
            NoticeBar.Visibility = Visibility.Collapsed;
        }
    }
}
