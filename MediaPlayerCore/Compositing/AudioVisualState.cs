using System;

namespace MediaPlayerCore.Compositing
{
    /// <summary>Smoothed audio levels fed to world-screen visual effects (desktop loopback or VLC PCM).</summary>
    public sealed class AudioVisualState
    {
        public float Rms { get; private set; }
        public float Bass { get; private set; }
        public float Mid { get; private set; }
        public float Treble { get; private set; }
        public float Band0 { get; private set; }
        public float Band1 { get; private set; }
        public float Band2 { get; private set; }
        public float Band3 { get; private set; }
        public float Band4 { get; private set; }
        public float Band5 { get; private set; }
        public float Band6 { get; private set; }
        public float Band7 { get; private set; }

        public void Reset()
        {
            Rms = Bass = Mid = Treble = 0f;
            Band0 = Band1 = Band2 = Band3 = Band4 = Band5 = Band6 = Band7 = 0f;
        }

        public void UpdateFromPcm16Mono(ReadOnlySpan<byte> pcmBytes)
        {
            int sampleCount = pcmBytes.Length / 2;
            if (sampleCount < 64)
            {
                return;
            }

            double sumSq = 0d;
            int bandSize = Math.Max(8, sampleCount / 8);
            Span<double> bandEnergy = stackalloc double[8];

            for (int i = 0; i < sampleCount; i++)
            {
                int byteIndex = i * 2;
                short raw = (short)(pcmBytes[byteIndex] | (pcmBytes[byteIndex + 1] << 8));
                float sample = raw / 32768f;
                sumSq += sample * sample;

                int band = Math.Min(7, i / bandSize);
                bandEnergy[band] += sample * sample;
            }

            float rms = (float)Math.Sqrt(sumSq / sampleCount);
            float b0 = (float)Math.Sqrt(bandEnergy[0] / bandSize);
            float b1 = (float)Math.Sqrt(bandEnergy[1] / bandSize);
            float b2 = (float)Math.Sqrt(bandEnergy[2] / bandSize);
            float b3 = (float)Math.Sqrt(bandEnergy[3] / bandSize);
            float b4 = (float)Math.Sqrt(bandEnergy[4] / bandSize);
            float b5 = (float)Math.Sqrt(bandEnergy[5] / bandSize);
            float b6 = (float)Math.Sqrt(bandEnergy[6] / bandSize);
            float b7 = (float)Math.Sqrt(bandEnergy[7] / Math.Max(1, sampleCount - bandSize * 7));

            float bass = (b0 + b1) * 0.5f;
            float mid = (b2 + b3 + b4 + b5) * 0.25f;
            float treble = (b6 + b7) * 0.5f;

            const float attack = 0.45f;
            const float release = 0.12f;
            Rms = Smooth(Rms, rms, attack, release);
            Bass = Smooth(Bass, bass, attack, release);
            Mid = Smooth(Mid, mid, attack, release);
            Treble = Smooth(Treble, treble, attack, release);
            Band0 = Smooth(Band0, b0, attack, release);
            Band1 = Smooth(Band1, b1, attack, release);
            Band2 = Smooth(Band2, b2, attack, release);
            Band3 = Smooth(Band3, b3, attack, release);
            Band4 = Smooth(Band4, b4, attack, release);
            Band5 = Smooth(Band5, b5, attack, release);
            Band6 = Smooth(Band6, b6, attack, release);
            Band7 = Smooth(Band7, b7, attack, release);
        }

        private static float Smooth(float current, float target, float attack, float release)
        {
            float t = target > current ? attack : release;
            return current + (target - current) * t;
        }
    }
}
