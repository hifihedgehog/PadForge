// PersonaVerify: consumer-side integration check for HIDMaestro composite
// USB personas (PadForge #255, HM#39).
//
// Why this exists: on 2026-07-31 every internal telemetry point in the
// PadForge audio bridge read healthy (frames flowing, decode succeeding,
// buffers draining) while Windows received full-scale noise. Internal
// counters cannot see corruption that happens below them. The only
// measurements that produced truth were taken at the consumer: WASAPI
// against the persona's own endpoints. This tool makes that repeatable,
// so an HIDMaestro bump can be verified in one command instead of an
// evening of bisecting.
//
// It also synthesizes what a DualSense-aware game does, rendering four
// channels with speaker on 1/2 and authored haptics on 3/4. That
// exercises the haptics lane with no game required.
//
// Usage:  dotnet run --project tools/PersonaVerify -- [diagLogPath]
// PadForge should be running with PADFORGE_DIAG pointed at that same path
// for the log-backed checks; without it the audio checks still run.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

internal static class Program
{
    private const string PersonaMatch = "Wireless Controller";
    private static readonly List<(string Name, bool Pass, string Detail)> Results = new();

    private static int Main(string[] args)
    {
        string diagPath = args.Length > 0 ? args[0] : @"C:\PadForge\persona-diag.log";
        Console.WriteLine("PersonaVerify: composite USB persona, consumer-side checks");
        Console.WriteLine($"diag log: {diagPath}{(File.Exists(diagPath) ? "" : "  (absent, log checks skipped)")}\n");

        var en = new MMDeviceEnumerator();
        var render = FindEndpoint(en, DataFlow.Render);
        var capture = FindEndpoint(en, DataFlow.Capture);

        Check("render endpoint present", render != null,
            render?.FriendlyName ?? "no persona render endpoint (is a composite VC active?)");
        Check("capture endpoint present", capture != null,
            capture?.FriendlyName ?? "no persona capture endpoint");

        if (render != null)
        {
            var f = render.AudioClient.MixFormat;
            Check("render is 4-channel 48 kHz", f.Channels == 4 && f.SampleRate == 48000,
                $"{f.Channels} ch @ {f.SampleRate} Hz  (speaker 1/2 + haptics 3/4 needs 4)");
        }
        if (capture != null)
        {
            var f = capture.AudioClient.MixFormat;
            Check("capture is 48 kHz", f.SampleRate == 48000, $"{f.Channels} ch @ {f.SampleRate} Hz");
        }

        long logStart = LogLength(diagPath);

        // Capture FIRST, in a quiet window. The pad's microphone sits
        // centimetres from its speaker, so measuring capture right after
        // rendering a test tone reads the tone back acoustically: on
        // 2026-07-31 that produced rms 0.5751 at crest 1.7x, which is the
        // signature of a sine (1.41x), not of noise (3-4x), and tripped
        // the noise heuristic. The coupling is real and is itself weak
        // evidence both lanes work, but it must not contaminate the
        // capture verdict.
        if (capture != null) CaptureCheck(capture);
        if (render != null) RenderPhases(render, diagPath, logStart);

        Console.WriteLine("\n──────── RESULTS ────────");
        foreach (var (name, pass, detail) in Results)
            Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name,-34} {detail}");
        int failed = Results.Count(r => !r.Pass);
        Console.WriteLine(failed == 0
            ? "\nAll automated checks passed. Still requires a human: haptics FEEL and voice INTELLIGIBILITY."
            : $"\n{failed} check(s) FAILED.");
        return failed == 0 ? 0 : 1;
    }

    private static MMDevice FindEndpoint(MMDeviceEnumerator en, DataFlow flow)
    {
        foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
            if (d.FriendlyName.IndexOf(PersonaMatch, StringComparison.OrdinalIgnoreCase) >= 0)
                return d;
        return null;
    }

    /// <summary>Render two phases into the persona endpoint: speaker-only,
    /// then haptics-only. A correct bridge reports frames throughout and a
    /// haptics ONSET during phase 2 only, which is exactly the
    /// discrimination a game's authored track needs.</summary>
    private static void RenderPhases(MMDevice dev, string diagPath, long logStart)
    {
        var fmt = dev.AudioClient.MixFormat;
        int ch = fmt.Channels;
        try
        {
            using var outp = new WasapiOut(dev, AudioClientShareMode.Shared, true, 60);
            var speaker = new PhaseTone(fmt, 440, spkAmp: 0.30f, hapAmp: 0f);
            outp.Init(speaker);
            outp.Play();
            Thread.Sleep(2500);
            outp.Stop();

            using var outp2 = new WasapiOut(dev, AudioClientShareMode.Shared, true, 60);
            var haptic = new PhaseTone(fmt, 80, spkAmp: 0f, hapAmp: 0.45f);
            outp2.Init(haptic);
            outp2.Play();
            Thread.Sleep(2500);
            outp2.Stop();

            Check("render accepted by endpoint", true, $"{ch} ch, two phases played");
        }
        catch (Exception ex)
        {
            Check("render accepted by endpoint", false, ex.Message);
            return;
        }

        if (!File.Exists(diagPath)) return;
        string tail = ReadTail(diagPath, logStart);
        Check("bridge saw rendered frames", tail.Contains("PERSONA rx"),
            "PadForge PERSONA rx heartbeat during render");
        Check("authored-haptics detected on ch 3/4", tail.Contains("PERSONA haptics ONSET"),
            tail.Contains("PERSONA haptics ONSET")
                ? "sniffer fired on the haptics phase"
                : "no ONSET, so ch 3/4 never reached the bridge");
    }

    private static void CaptureCheck(MMDevice dev)
    {
        var cap = new WasapiCapture(dev);
        bool isFloat = cap.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat;
        int step = isFloat ? 4 : 2;
        double peak = 0, sumSq = 0; long n = 0; long big = 0;
        cap.DataAvailable += (_, a) =>
        {
            for (int i = 0; i + step <= a.BytesRecorded; i += step)
            {
                double v = isFloat ? BitConverter.ToSingle(a.Buffer, i)
                                   : BitConverter.ToInt16(a.Buffer, i) / 32768.0;
                double av = Math.Abs(v);
                if (av > peak) peak = av;
                if (av > 0.9) big++;
                sumSq += v * v; n++;
            }
        };
        cap.StartRecording();
        Thread.Sleep(3500);
        cap.StopRecording();
        Thread.Sleep(250);
        cap.Dispose();

        double rms = n > 0 ? Math.Sqrt(sumSq / n) : 0;
        double crest = rms > 1e-9 ? peak / rms : 0;
        double bigPct = n > 0 ? 100.0 * big / n : 0;

        // The three failure shapes seen on hardware, each with a distinct
        // signature. Full-scale noise: high rms AND a low crest factor
        // (~4x), because randomized samples fill the range uniformly. Real
        // capture keeps a high crest factor even when quiet. Silence is a
        // dead lane (muted pad, or nothing feeding it).
        // Crest factor is the discriminator: randomized samples fill the
        // range uniformly (3-4x), real capture stays peaky even when
        // quiet (>6x). A loud PURE TONE also sits low (~1.41x), so this
        // check only runs in the quiet window before any render.
        bool silence = rms < 0.0005;
        bool noise = rms > 0.25 && crest < 6.0;
        Check("capture is not silence", !silence, $"rms={rms:F4}");
        Check("capture is not full-scale noise", !noise,
            $"rms={rms:F4} peak={peak:F3} crest={crest:F1}x nearFull={bigPct:F1}%");
        Check("capture level is usable", !silence && rms > 0.002,
            rms < 0.002 ? $"rms={rms:F4}, very quiet. Check the mic gain mapping" : $"rms={rms:F4}");
    }

    private static void Check(string name, bool pass, string detail)
    {
        Results.Add((name, pass, detail));
        Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name}: {detail}");
    }

    private static long LogLength(string p)
    {
        try { return File.Exists(p) ? new FileInfo(p).Length : 0; } catch { return 0; }
    }

    private static string ReadTail(string p, long from)
    {
        try
        {
            using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (from > 0 && from < fs.Length) fs.Seek(from, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }
        catch { return ""; }
    }
}

/// <summary>Four-channel generator placing independent tones on the
/// speaker pair (0/1) and the haptic pair (2/3), so the two lanes can be
/// driven and observed separately.
///
/// Deliberately an IWaveProvider carrying the endpoint's OWN mix format
/// verbatim, not an ISampleProvider built from
/// CreateIeeeFloatWaveFormat. Shared-mode WASAPI rejects a
/// non-extensible format beyond stereo with "Value does not fall within
/// the expected range" (E_INVALIDARG), and the persona endpoint is
/// extensible float 48k 4ch. PadForge's own UsbFrameProvider carries the
/// same note for the same reason.</summary>
internal sealed class PhaseTone : IWaveProvider
{
    private readonly float _spk, _hap;
    private readonly double _step;
    private double _phase;
    private float[] _scratch = Array.Empty<float>();

    public PhaseTone(WaveFormat mixFormat, double hz, float spkAmp, float hapAmp)
    {
        WaveFormat = mixFormat;
        _spk = spkAmp; _hap = hapAmp;
        _step = 2 * Math.PI * hz / mixFormat.SampleRate;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(byte[] buffer, int offset, int count)
    {
        int ch = WaveFormat.Channels;
        int frames = count / (4 * ch);
        int need = frames * ch;
        if (_scratch.Length < need) _scratch = new float[need];
        for (int f = 0; f < frames; f++)
        {
            float v = (float)Math.Sin(_phase);
            _phase += _step;
            if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
            int o = f * ch;
            for (int c = 0; c < ch; c++)
                _scratch[o + c] = c < 2 ? v * _spk : v * _hap;
        }
        Buffer.BlockCopy(_scratch, 0, buffer, offset, need * 4);
        return need * 4;
    }
}
