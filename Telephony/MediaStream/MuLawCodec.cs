using System;

namespace PruvaVoice.Api.Telephony.MediaStream;

/// <summary>
/// High-performance table-driven G.711 μ-law (PCMU) codec and linear audio resampler.
/// Used for sub-millisecond transcoding between 8kHz 8-bit carrier audio and 24kHz/16-bit AI voice engine.
/// </summary>
public static class MuLawCodec
{
    private static readonly short[] MuLawToLinearTable = new short[256];
    private static readonly byte[] LinearToMuLawTable = new byte[65536];

    static MuLawCodec()
    {
        // Precompute μ-law to 16-bit linear PCM lookup table
        for (int i = 0; i < 256; i++)
        {
            byte b = (byte)i;
            int input = ~b;
            int sign = input & 0x80;
            int exponent = (input >> 4) & 0x07;
            int mantissa = input & 0x0F;
            int sample = ((mantissa << 3) + 0x84) << exponent;
            sample -= 0x84;
            MuLawToLinearTable[i] = (short)(sign != 0 ? -sample : sample);
        }

        // Precompute 16-bit linear PCM to μ-law lookup table
        for (int i = 0; i < 65536; i++)
        {
            short pcm = (short)(i - 32768);
            LinearToMuLawTable[i] = EncodePcmSample(pcm);
        }
    }

    private static byte EncodePcmSample(short sample)
    {
        const int BIAS = 0x84;
        const int CLIP = 32635;

        int sign = (sample >> 8) & 0x80;
        if (sign != 0) sample = (short)-sample;
        if (sample > CLIP) sample = CLIP;
        sample = (short)(sample + BIAS);

        int exponent = 7;
        for (int expMask = 0x4000; (sample & expMask) == 0 && exponent > 0; expMask >>= 1)
        {
            exponent--;
        }

        int mantissa = (sample >> (exponent + 3)) & 0x0F;
        byte muLawByte = (byte)(sign | (exponent << 4) | mantissa);
        return (byte)~muLawByte;
    }

    /// <summary>
    /// Decodes a byte array of 8-bit μ-law audio (8kHz) into 16-bit Linear PCM (8kHz).
    /// </summary>
    public static short[] DecodeMuLawToPcm(ReadOnlySpan<byte> muLawData)
    {
        var pcm = new short[muLawData.Length];
        for (int i = 0; i < muLawData.Length; i++)
        {
            pcm[i] = MuLawToLinearTable[muLawData[i]];
        }
        return pcm;
    }

    /// <summary>
    /// Encodes a 16-bit Linear PCM sample array (8kHz) into 8-bit μ-law audio (8kHz).
    /// </summary>
    public static byte[] EncodePcmToMuLaw(ReadOnlySpan<short> pcmData)
    {
        var muLaw = new byte[pcmData.Length];
        for (int i = 0; i < pcmData.Length; i++)
        {
            int index = pcmData[i] + 32768;
            muLaw[i] = LinearToMuLawTable[index];
        }
        return muLaw;
    }

    /// <summary>
    /// Fast linear interpolation upsampling from 8,000 Hz to 24,000 Hz (3x upsampling).
    /// </summary>
    public static short[] Upsample8kTo24k(ReadOnlySpan<short> input8k)
    {
        if (input8k.Length == 0) return Array.Empty<short>();

        int outputLength = input8k.Length * 3;
        var output24k = new short[outputLength];

        for (int i = 0; i < input8k.Length - 1; i++)
        {
            short s0 = input8k[i];
            short s1 = input8k[i + 1];

            output24k[i * 3] = s0;
            output24k[i * 3 + 1] = (short)(s0 + (s1 - s0) / 3);
            output24k[i * 3 + 2] = (short)(s0 + ((s1 - s0) * 2) / 3);
        }

        // Last sample replication
        short last = input8k[^1];
        output24k[^3] = last;
        output24k[^2] = last;
        output24k[^1] = last;

        return output24k;
    }

    /// <summary>
    /// Decimates/downsamples 16-bit Linear PCM from 24,000 Hz to 8,000 Hz (3:1 downsampling with averaging filter).
    /// </summary>
    public static short[] Downsample24kTo8k(ReadOnlySpan<short> input24k)
    {
        int outputLength = input24k.Length / 3;
        var output8k = new short[outputLength];

        for (int i = 0; i < outputLength; i++)
        {
            int idx = i * 3;
            int sum = input24k[idx] + input24k[idx + 1] + input24k[idx + 2];
            output8k[i] = (short)(sum / 3);
        }

        return output8k;
    }
}
