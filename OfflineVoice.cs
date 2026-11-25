using System.Speech.Recognition;
using System.Speech.Synthesis;
using Avalonia.Controls;
using Avalonia.Threading;

namespace LocalDesk;

public sealed partial class MainWindow
{
    SpeechRecognitionEngine? dictation;
    SpeechSynthesizer? speaker;
    Button dictateButton = null!;

    void ToggleDictation()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (busy || sendingRequest) return;
        if (dictation != null)
        {
            dictation.RecognizeAsyncStop();
            dictateButton.IsEnabled = false;
            return;
        }
        SpeechRecognitionEngine? engine = null;
        try
        {
            var voices = SpeechRecognitionEngine.InstalledRecognizers();
            if (voices.Count == 0)
                throw new InvalidOperationException("Install a Windows desktop speech recognition language for offline dictation.");
            var voice = voices[0];
            engine = new SpeechRecognitionEngine(voice);
            dictation = engine;
            var chat = current;
            engine.SetInputToDefaultAudioDevice();
            engine.LoadGrammar(new DictationGrammar());
            engine.SpeechRecognized += (_, result) => Dispatcher.UIThread.Post(() =>
            {
                if (!OperatingSystem.IsWindows() || !ReferenceEquals(dictation, engine) || !ReferenceEquals(current, chat) || !CanEditDraft) return;
                input.Text = string.Join(" ", new[] { input.Text?.TrimEnd(), result.Result.Text }.Where(text => !string.IsNullOrEmpty(text)));
                input.CaretIndex = input.Text.Length;
            });
            engine.RecognizeCompleted += (_, result) => Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(dictation, engine)) return;
                EndDictation();
                if (result.Error != null) status.Text = result.Error.Message;
                else status.Text = "Dictation stopped. Review the text before sending.";
            });
            engine.RecognizeAsync(RecognizeMode.Multiple);
            dictateButton.Content = "Stop dictation";
            status.Text = "Listening with the installed Windows recognizer.";
        }
        catch (Exception error)
        {
            EndDictation();
            status.Text = "Offline dictation unavailable: " + error.Message;
        }
    }

    void EndDictation()
    {
        var engine = dictation;
        dictation = null;
        if (OperatingSystem.IsWindows()) engine?.Dispose();
        if (dictateButton != null)
        {
            dictateButton.Content = "Dictate";
            dictateButton.IsEnabled = true;
        }
    }

    void ReadAloud(string text)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            speaker ??= new SpeechSynthesizer();
            speaker.SetOutputToDefaultAudioDevice();
            speaker.SpeakAsyncCancelAll();
            speaker.SpeakAsync(text.Length > 16000 ? text[..16000] : text);
            status.Text = "Reading aloud. Use Stop reading in Quick actions to stop.";
        }
        catch (Exception error) { status.Text = "Offline read-aloud unavailable: " + error.Message; }
    }

    void StopReading()
    {
        if (OperatingSystem.IsWindows()) speaker?.SpeakAsyncCancelAll();
    }

    void DisposeSpeaker()
    {
        if (OperatingSystem.IsWindows()) speaker?.Dispose();
    }
}
