using NAudio.Wave;

namespace Eve.App.Services;

public static class ClipNotificationSound
{
    public static void Play(string volumeLevel)
    {
        var gain = volumeLevel switch
        {
            "Low" => 0.15f,
            "High" => 0.6f,
            _ => 0.35f
        };

        try
        {
            var waveOut = new WaveOutEvent();
            var provider = new ClipChimeSampleProvider(gain);
            waveOut.Init(provider);
            waveOut.PlaybackStopped += (_, _) =>
            {
                waveOut.Dispose();
            };
            waveOut.Play();
        }
        catch (Exception error)
        {
            AppLog.Error("Clip notification sound failed to play", error);
        }
    }

    // Two quick ascending tones with fast fade in/out, similar in shape to a
    // typical "clip captured" chime, without embedding any third-party audio.
    private sealed class ClipChimeSampleProvider : ISampleProvider
    {
        private const int SampleRate = 44100;
        private readonly float _gain;
        private int _sampleIndex;
        private readonly int _totalSamples;
        private readonly (double frequency, int startSample, int lengthSamples)[] _notes;

        public ClipChimeSampleProvider(float gain)
        {
            _gain = gain;
            var noteLength = (int)(SampleRate * 0.09);
            var gap = (int)(SampleRate * 0.03);
            _notes = new[]
            {
                (880.0, 0, noteLength),
                (1320.0, noteLength + gap, noteLength)
            };
            _totalSamples = noteLength + gap + noteLength + (int)(SampleRate * 0.05);
        }

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var written = 0;
            while (written < count && _sampleIndex < _totalSamples)
            {
                buffer[offset + written] = SampleAt(_sampleIndex);
                _sampleIndex++;
                written++;
            }

            return written;
        }

        private float SampleAt(int sampleIndex)
        {
            var value = 0.0;
            foreach (var (frequency, startSample, lengthSamples) in _notes)
            {
                var localIndex = sampleIndex - startSample;
                if (localIndex < 0 || localIndex >= lengthSamples) continue;

                var t = localIndex / (double)SampleRate;
                var envelope = Envelope(localIndex, lengthSamples);
                value += Math.Sin(2 * Math.PI * frequency * t) * envelope;
            }

            return (float)(value * _gain);
        }

        private static double Envelope(int localIndex, int lengthSamples)
        {
            var fadeSamples = Math.Min(lengthSamples / 4, (int)(SampleRate * 0.012));
            if (localIndex < fadeSamples) return localIndex / (double)fadeSamples;
            if (localIndex > lengthSamples - fadeSamples) return (lengthSamples - localIndex) / (double)fadeSamples;
            return 1.0;
        }
    }
}
