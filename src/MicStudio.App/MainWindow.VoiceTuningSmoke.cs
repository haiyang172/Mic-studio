using System.Threading;
using System.Windows.Automation;

namespace MicStudio.App;

public partial class MainWindow
{
    private void RunVoiceTuningChecks(Action<bool, string> check)
    {
        var saved = Draft.Clone();
        var mode = State.Mode;
        var savedPage = currentPage;
        try
        {
            State.Mode = 1;
            ShowPage(2);
            UpdateLayout();
            var strength = Descendants<Slider>(PageContent).Single(s => AutomationProperties.GetName(s) == "去齿音强度");
            check(strength.Value == saved.DeEsser.StrengthPercent && !Descendants<Expander>(PageContent).Single().IsExpanded,
                "De-esser strength is available with advanced controls collapsed");
            strength.Value = 42;
            check(Draft.DeEsser.StrengthPercent == 42 && !engine.IsRunning,
                "De-esser slider updates the draft without starting capture");
            SetSmokeDetails(true);
            UpdateLayout();
            var threshold = Descendants<Slider>(PageContent).Single(s => AutomationProperties.GetName(s) == "齿音门限");
            threshold.Value = -27;
            vm.Save();
            var restored = new StudioViewModel().State.Draft.DeEsser;
            check(restored.StrengthPercent == 42 && restored.ThresholdDb == -27,
                "De-esser parameters persist across workspace reload");

            ShowPage(0);
            SetSmokeDetails(true);
            UpdateLayout();
            var start = Descendants<System.Windows.Controls.Button>(PageContent)
                .Single(button => AutomationProperties.GetAutomationId(button) == "loop-tuning-start");
            start.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            check(LoopTuningBar.Visibility == Visibility.Collapsed && !engine.IsPlaying && !engine.IsRunning &&
                NoticeText.Text.Contains("A/B", StringComparison.Ordinal),
                "Loop preview with no completed source leaves capture and playback stopped");

            using var pending = new CancellationTokenSource();
            loopPreparation = pending;
            ShowPage(1);
            check(LoopTuningBar.Visibility == Visibility.Visible && !LoopCompareButton.IsEnabled,
                "Pending loop preview keeps its cancel control on the equalizer page");
            LoopStopButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            check(pending.IsCancellationRequested && loopPreparation == null &&
                LoopTuningBar.Visibility == Visibility.Collapsed && !engine.IsPlaying && !engine.IsRunning,
                "Cancelling pending preview clears its strip without opening audio");
        }
        finally
        {
            CancelLoopTuningPreparation();
            State.Mode = mode;
            vm.Replace(saved);
            NoticeBar.Visibility = Visibility.Collapsed;
            ShowPage(savedPage);
        }
    }
}
