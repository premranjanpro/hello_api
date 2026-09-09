namespace PruvaVoice.Api.Telephony.MediaStream;

public enum AmdStatus
{
    InProgress,
    Human,
    Machine,
    SilenceTimeout
}

public record AmdResult(
    AmdStatus Status,
    double Confidence,
    int GreetingDurationMs,
    string Reason
);

/// <summary>
/// High-performance stream cadence analyzer to distinguish Human vs Voicemail/Machine
/// during the initial 1.5 - 3.5 seconds of an outbound carrier PSTN call.
/// </summary>
public class AnsweringMachineDetector
{
    private readonly int _energyThreshold;
    private readonly int _maxGreetingMs;
    private readonly int _minSilenceAfterGreetingMs;
    private readonly int _silenceTimeoutMs;

    private int _elapsedMs;
    private int _speechDurationMs;
    private int _silenceAfterSpeechMs;
    private bool _hasSpoken;
    private bool _isResolved;
    private AmdResult _finalResult;

    public AnsweringMachineDetector(
        int energyThreshold = 350,
        int maxGreetingMs = 2400,
        int minSilenceAfterGreetingMs = 450,
        int silenceTimeoutMs = 4500
    )
    {
        _energyThreshold = energyThreshold;
        _maxGreetingMs = maxGreetingMs;
        _minSilenceAfterGreetingMs = minSilenceAfterGreetingMs;
        _silenceTimeoutMs = silenceTimeoutMs;
        _finalResult = new AmdResult(AmdStatus.InProgress, 0, 0, "Analyzing initial utterance cadence");
    }

    public bool IsResolved => _isResolved;

    public AmdResult ProcessPcmFrame(short[] pcmSamples, int frameDurationMs = 20)
    {
        if (_isResolved) return _finalResult;

        _elapsedMs += frameDurationMs;

        // Calculate Root-Mean-Square (RMS) audio frame energy
        long sumSquares = 0;
        for (int i = 0; i < pcmSamples.Length; i++)
        {
            sumSquares += (long)pcmSamples[i] * pcmSamples[i];
        }
        double rms = Math.Sqrt((double)sumSquares / pcmSamples.Length);
        bool isSpeech = rms > _energyThreshold;

        if (isSpeech)
        {
            _hasSpoken = true;
            _speechDurationMs += frameDurationMs;
            _silenceAfterSpeechMs = 0; // Reset silence counter while speaking

            // If continuous uninterrupted speech exceeds 2.4 - 3.0 seconds, it is an automated machine / voicemail greeting
            if (_speechDurationMs >= _maxGreetingMs)
            {
                _isResolved = true;
                _finalResult = new AmdResult(
                    Status: AmdStatus.Machine,
                    Confidence: 0.94,
                    GreetingDurationMs: _speechDurationMs,
                    Reason: $"Continuous speech duration of {_speechDurationMs}ms exceeded human greeting threshold ({_maxGreetingMs}ms)"
                );
                return _finalResult;
            }
        }
        else // Silence frame
        {
            if (_hasSpoken)
            {
                _silenceAfterSpeechMs += frameDurationMs;

                // Human cadence: short greeting (e.g. 300ms - 2000ms "Hello?", "Yes?") followed by conversational pause (> 450ms)
                if (_speechDurationMs >= 200 && _speechDurationMs < _maxGreetingMs && _silenceAfterSpeechMs >= _minSilenceAfterGreetingMs)
                {
                    _isResolved = true;
                    _finalResult = new AmdResult(
                        Status: AmdStatus.Human,
                        Confidence: 0.92,
                        GreetingDurationMs: _speechDurationMs,
                        Reason: $"Short natural greeting ({_speechDurationMs}ms) followed by conversational pause ({_silenceAfterSpeechMs}ms)"
                    );
                    return _finalResult;
                }
            }
            else
            {
                // Absolute initial silence timeout (no one spoke within 4.5s)
                if (_elapsedMs >= _silenceTimeoutMs)
                {
                    _isResolved = true;
                    _finalResult = new AmdResult(
                        Status: AmdStatus.SilenceTimeout,
                        Confidence: 0.85,
                        GreetingDurationMs: 0,
                        Reason: $"No speech detected within {_silenceTimeoutMs}ms of call answer"
                    );
                    return _finalResult;
                }
            }
        }

        return _finalResult;
    }

    public void Reset()
    {
        _elapsedMs = 0;
        _speechDurationMs = 0;
        _silenceAfterSpeechMs = 0;
        _hasSpoken = false;
        _isResolved = false;
        _finalResult = new AmdResult(AmdStatus.InProgress, 0, 0, "Analyzing initial utterance cadence");
    }
}
