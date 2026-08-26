using System.Buffers.Binary;
using NAudio.Wave;

namespace ClypDat.App.Services;

// One answer to "what shape are these microphone samples?", shared by the Mic
// Test meter and the live noise-suppression stage.
//
// The trap this exists to close: WASAPI hands back the driver's mix format,
// and a driver describing itself as WAVE_FORMAT_EXTENSIBLE reports
// WaveFormat.Encoding as Extensible - NOT IeeeFloat - with the real encoding
// hidden behind a subformat GUID. Code that tests Encoding directly therefore
// rejects a perfectly ordinary 32-bit float microphone. The meter and the
// denoiser used to make that decision separately, so a device could end up
// metered but not filtered, or the reverse, with nothing in the log saying so.
internal static class AudioSampleFormat
{
    // KSDATAFORMAT_SUBTYPE_PCM / _IEEE_FLOAT.
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>
    /// The encoding a caller should actually branch on, with
    /// WAVE_FORMAT_EXTENSIBLE resolved to the subformat it wraps.
    /// </summary>
    public static WaveFormatEncoding ResolveEncoding(WaveFormat format)
    {
        if (format is not WaveFormatExtensible extensible) return format.Encoding;
        if (extensible.SubFormat == PcmSubFormat) return WaveFormatEncoding.Pcm;
        if (extensible.SubFormat == IeeeFloatSubFormat) return WaveFormatEncoding.IeeeFloat;
        return format.Encoding;
    }

    /// <summary>
    /// 32-bit float, which is what the noise-suppression pipe feeds ffmpeg as
    /// f32le. Anything else fed down that pipe would be reinterpreted as
    /// floats and come out as noise rather than as a diagnosable error.
    /// </summary>
    public static bool IsFloat32(WaveFormat format) =>
        ResolveEncoding(format) == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32;

    /// <summary>
    /// Largest absolute sample in the buffer, normalised to 0..1. False when
    /// the format is one this cannot read, which callers show as "no level"
    /// rather than as silence.
    /// </summary>
    public static bool TryGetPeak(WaveFormat format, byte[] buffer, int bytesRecorded, out float peak)
    {
        peak = 0;
        if (buffer is null || bytesRecorded <= 0) return false;

        var samples = buffer.AsSpan(0, Math.Clamp(bytesRecorded, 0, buffer.Length));
        var encoding = ResolveEncoding(format);

        if (encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            for (var offset = 0; offset + 4 <= samples.Length; offset += 4)
            {
                // Guarded because a float buffer can carry NaN/Inf from a
                // misbehaving driver, and one of those poisons every
                // comparison after it.
                var value = Math.Abs(BitConverter.ToSingle(samples.Slice(offset, 4)));
                if (float.IsFinite(value) && value > peak) peak = value;
            }

            return true;
        }

        if (encoding != WaveFormatEncoding.Pcm) return false;

        switch (format.BitsPerSample)
        {
            case 8:
                // 8-bit PCM is unsigned, centred on 128.
                foreach (var sample in samples)
                {
                    var value = Math.Abs((sample - 128) / 128f);
                    if (value > peak) peak = value;
                }

                return true;

            case 16:
                for (var offset = 0; offset + 2 <= samples.Length; offset += 2)
                {
                    var value = Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(samples.Slice(offset, 2)) / 32768f);
                    if (value > peak) peak = value;
                }

                return true;

            case 24:
                for (var offset = 0; offset + 3 <= samples.Length; offset += 3)
                {
                    var sample = samples[offset] | samples[offset + 1] << 8 | samples[offset + 2] << 16;
                    // Sign-extend the 24-bit value into the int's top byte.
                    if ((sample & 0x00800000) != 0) sample |= unchecked((int)0xff000000);
                    var value = Math.Abs(sample / 8_388_608f);
                    if (value > peak) peak = value;
                }

                return true;

            case 32:
                for (var offset = 0; offset + 4 <= samples.Length; offset += 4)
                {
                    var value = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(samples.Slice(offset, 4)) / 2_147_483_648f);
                    if (value > peak) peak = value;
                }

                return true;

            default:
                return false;
        }
    }
}
