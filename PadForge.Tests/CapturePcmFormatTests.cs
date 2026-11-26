using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using HIDMaestro;
using NAudio.Wave;
using PadForge.Common.Input;
using PadForge.Services;

namespace PadForge.Tests;

[Collection("AudioProfileLifecycle")]
public sealed class CapturePcmFormatTests
{
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    const BindingFlags Static=BindingFlags.Static|BindingFlags.NonPublic;
    static (WaveFormat Format,byte[] Data) Sample(string kind)=>kind switch
    {
        "pcm8"=>(new WaveFormat(48000,8,2),[192,64]),
        "pcm16"=>(new WaveFormat(48000,16,2),[0,64,0,192]),
        "pcm24"=>(new WaveFormat(48000,24,2),[0,0,64,0,0,192]),
        "pcm32"=>(new WaveFormat(48000,32,2),[0,0,0,64,0,0,0,192]),
        "float32"=>(WaveFormat.CreateIeeeFloatWaveFormat(48000,2),[0,0,0,63,0,0,0,191]),
        "float64"=>(WaveFormat.CreateCustomFormat(WaveFormatEncoding.IeeeFloat,48000,2,768000,16,64),[0,0,0,0,0,0,224,63,0,0,0,0,0,0,224,191]),
        "extended24"=>(new WaveFormatExtensible(48000,24,2),[0,0,64,0,0,192]),
        "extendedFloat"=>(new WaveFormatExtensible(48000,32,2),[0,0,0,63,0,0,0,191]),
        "unsupported"=>(WaveFormat.CreateCustomFormat(WaveFormatEncoding.ALaw,48000,2,96000,2,8),[0,0]),
        _=>throw new ArgumentException(kind)
    };
    sealed class Capture(WaveFormat format):IWaveIn
    {
        public WaveFormat WaveFormat{get;set;}=format;
        public event EventHandler<WaveInEventArgs> DataAvailable;
        public event EventHandler<StoppedEventArgs> RecordingStopped;
        public int Starts,Disposals;public bool FailStart;
        public void StartRecording(){Starts++;if(FailStart)throw new InvalidOperationException("Capture start rejected.");}
        public void StopRecording(){}
        public void Dispose(){Disposals++;RecordingStopped?.Invoke(this,new StoppedEventArgs());}
        public void Emit(byte[] data)=>DataAvailable?.Invoke(this,new WaveInEventArgs(data,data.Length));
    }
    [Theory]
    [InlineData("pcm8")] [InlineData("pcm16")] [InlineData("pcm24")] [InlineData("pcm32")]
    [InlineData("float32")] [InlineData("float64")] [InlineData("extended24")] [InlineData("extendedFloat")] [InlineData("unsupported")]
    public void LoopbackConsumesItsDeclaredFormat(string kind)
    {
        var (format,data)=Sample(kind);var capture=new Capture(format);
        var entry=AudioPassthroughService.CreateCaptureEntry("managed-loopback",Guid.Empty,capture);
        try
        {
            if(kind=="unsupported"){Assert.Null(entry);Assert.Equal(0,capture.Starts);Assert.Equal(1,capture.Disposals);return;}
            Assert.NotNull(entry);Assert.Equal(1,capture.Starts);Assert.Same(capture,entry.Cap);
            capture.Emit(data);Assert.Equal(1,entry.Write);Assert.Equal(.5f,entry.Ring[0]);Assert.Equal(-.5f,entry.Ring[1]);
        }
        finally{if(capture.Disposals==0)capture.Dispose();}
    }
    [Fact] public void FailedCaptureStartReleasesItsClient()
    {
        var capture=new Capture(WaveFormat.CreateIeeeFloatWaveFormat(48000,2)){FailStart=true};
        Assert.Null(AudioPassthroughService.CreateCaptureEntry("failed-loopback",Guid.Empty,capture));
        Assert.Equal(1,capture.Starts);Assert.Equal(1,capture.Disposals);
        var voice=new Capture(capture.WaveFormat){FailStart=true};
        Assert.Null(VoiceMacroService.StartEndpointCapture(voice,new VoiceSink(),()=>false,"failed-voice"));
        Assert.Equal(1,voice.Starts);Assert.Equal(1,voice.Disposals);
    }
    [Fact] public void MonoLoopbackKeepsStereoDuplicationAndRateConversion()
    {
        var capture=new Capture(new WaveFormat(24000,8,1));
        var entry=AudioPassthroughService.CreateCaptureEntry("mono-loopback",Guid.Empty,capture);
        try
        {
            Assert.NotNull(entry);capture.Emit([192,64]);
            Assert.Equal(4,entry.Write);
            Assert.Equal(new float[]{.5f,.5f,.5f,.5f,-.5f,-.5f,-.5f,-.5f},entry.Ring.Take(8).ToArray());
        }
        finally{capture.Dispose();}
    }

    static byte[] PositiveFrames(WaveFormat format,byte[] frame)
    {
        var value=(byte[])frame.Clone();int width=format.BitsPerSample/8;
        Array.Copy(value,0,value,width,width);
        return value.Concat(value).Concat(value).ToArray();
    }

    [Theory]
    [InlineData("pcm8")] [InlineData("pcm16")] [InlineData("pcm24")] [InlineData("pcm32")]
    [InlineData("float32")] [InlineData("float64")] [InlineData("extended24")] [InlineData("extendedFloat")] [InlineData("unsupported")]
    public void BassAnalysisReadsTheSameSampleFormats(string kind)
    {
        var (format,data)=Sample(kind);using var detector=new AudioBassDetector();
        using var capture=new Capture(format);
        var callback=typeof(AudioBassDetector).GetMethod("OnDataAvailable",Private)!;
        byte[] block=PositiveFrames(format,data);callback.Invoke(detector,[capture,new WaveInEventArgs(block,block.Length)]);
        Assert.Equal(kind=="unsupported"?0f:.5f,detector.FullSpectrumPeak);
        var (controlFormat,control)=Sample("float32");using var controlCapture=new Capture(controlFormat);
        block=PositiveFrames(controlFormat,control);callback.Invoke(detector,[controlCapture,new WaveInEventArgs(block,block.Length)]);
        Assert.Equal(.5f,detector.FullSpectrumPeak);
    }

    sealed class VoiceSink:IVoicePcmSink
    {
        public readonly List<byte> Data=new();
        public void Write(ReadOnlySpan<byte> bytes)=>Data.AddRange(bytes.ToArray());
        public void Dispose(){}
    }

    [Theory]
    [InlineData("pcm8")] [InlineData("pcm16")] [InlineData("pcm24")] [InlineData("pcm32")]
    [InlineData("float32")] [InlineData("float64")] [InlineData("extended24")] [InlineData("extendedFloat")] [InlineData("unsupported")]
    public void VoiceCaptureDecodesBeforeRateConversion(string kind)
    {
        int mode=VoiceMacroService.ListeningMode;VoiceMacroService.ListeningMode=0;
        var (format,data)=Sample(kind);var capture=new Capture(format);var sink=new VoiceSink();
        try
        {
            var active=VoiceMacroService.StartEndpointCapture(capture,sink,()=>false,"voice-format");
            if(kind=="unsupported"){Assert.Null(active);Assert.Equal(0,capture.Starts);Assert.Equal(1,capture.Disposals);return;}
            Assert.Same(capture,active);capture.Emit(PositiveFrames(format,data));
            Assert.Equal(2,sink.Data.Count);Assert.Equal(16383,BitConverter.ToInt16(sink.Data.ToArray(),0));
        }
        finally{VoiceMacroService.ListeningMode=mode;if(capture.Disposals==0)capture.Dispose();}
    }

    [Fact] public void VoiceCapturePreservesListeningAndProducerGates()
    {
        int mode=VoiceMacroService.ListeningMode;
        var held=typeof(VoiceMacroService).GetField("_listenHeldUntil",Static)!;object oldHeld=held.GetValue(null);
        var (format,data)=Sample("pcm24");using var capture=new Capture(format);var sink=new VoiceSink();bool muted=true;
        try
        {
            VoiceMacroService.ListeningMode=0;
            Assert.Same(capture,VoiceMacroService.StartEndpointCapture(capture,sink,()=>muted,"voice-gates"));
            byte[] block=PositiveFrames(format,data);capture.Emit(block);Assert.Empty(sink.Data);
            muted=false;VoiceMacroService.ListeningMode=1;held.SetValue(null,0L);capture.Emit(block);Assert.Empty(sink.Data);
            VoiceMacroService.NoteListenHeld();capture.Emit(block);
            Assert.Equal(2,sink.Data.Count);Assert.Equal(16383,BitConverter.ToInt16(sink.Data.ToArray(),0));
        }
        finally{VoiceMacroService.ListeningMode=mode;held.SetValue(null,oldHeld);}
    }

    static void Backing(object obj,string name,object value)=>obj.GetType().GetField("<"+name+">k__BackingField",Private)!.SetValue(obj,value);
    sealed class MicRig : IDisposable
    {
        public readonly Capture Capture;
        public readonly AudioPassthroughService.PersonaFeed Feed;
        public readonly HMMicrophoneInput Microphone;
        public readonly byte[] Ring=new byte[128];
        readonly IDictionary feeds;
        readonly object gate,previous;
        readonly Func<string,IWaveIn> factory;
        public MicRig(WaveFormat format,int channels=2)
        {
            Capture=new Capture(format);
            var audio=(HMUsbAudio)RuntimeHelpers.GetUninitializedObject(typeof(HMUsbAudio));
            Microphone=(HMMicrophoneInput)RuntimeHelpers.GetUninitializedObject(typeof(HMMicrophoneInput));
            Backing(audio,"Microphone",Microphone);Backing(Microphone,"Channels",channels);Backing(Microphone,"SampleRateHz",48000);Backing(Microphone,"BitsPerSample",16);
            var engineField=typeof(HMMicrophoneInput).GetField("_engine",Private)!;
            var engine=RuntimeHelpers.GetUninitializedObject(engineField.FieldType);
            engine.GetType().GetField("_lock",Private)!.SetValue(engine,new object());
            // HIDMaestro 2.0.0 reads IsStreaming from the stream table: empty reads as not streaming.
            engine.GetType().GetField("_altSetting",Private)!.SetValue(engine,new Dictionary<byte,byte>());
            engine.GetType().GetField("_micRing",Private)!.SetValue(engine,Ring);
            engine.GetType().GetField("_micFrameBytes",Private)?.SetValue(engine,channels*2);engineField.SetValue(Microphone,engine);
            Feed=new AudioPassthroughService.PersonaFeed{Owner=new(),Audio=audio,Slot=14,Published=true,RouteGeneration=1};
            feeds=(IDictionary)typeof(AudioPassthroughService).GetField("_personaFeeds",Static)!.GetValue(null);
            gate=typeof(AudioPassthroughService).GetField("_personaFeedLock",Static)!.GetValue(null);
            factory=AudioPassthroughService.PersonaMicCaptureFactory;
            lock(gate){previous=feeds[14];feeds[14]=Feed;}
            AudioPassthroughService.PersonaMicCaptureFactory=_=>Capture;
        }
        public void Start()=>typeof(AudioPassthroughService).GetMethod("StartPersonaMic",Static)!.Invoke(null,[Feed,Guid.NewGuid(),"managed-input",14,1]);
        public void Dispose()
        {
            Feed.Retired=true;if(Capture.Disposals==0)Capture.Dispose();AudioPassthroughService.PersonaMicCaptureFactory=factory;
            lock(gate){if(previous==null)feeds.Remove(14);else feeds[14]=previous;}
        }
    }
    [Theory]
    [InlineData("pcm8")] [InlineData("pcm16")] [InlineData("pcm24")] [InlineData("pcm32")]
    [InlineData("float32")] [InlineData("float64")] [InlineData("extended24")] [InlineData("extendedFloat")] [InlineData("unsupported")]
    public void MicrophoneSubmitsTheDecodedSamples(string kind)
    {
        var (format,data)=Sample(kind);using var rig=new MicRig(format);rig.Start();
        if(kind=="unsupported"){Assert.Null(rig.Feed.Mic);Assert.Equal(0,rig.Capture.Starts);Assert.Equal(1,rig.Capture.Disposals);return;}
        Assert.Same(rig.Capture,rig.Feed.Mic);rig.Capture.Emit(data);
        Assert.Equal(4,rig.Microphone.BufferedBytes);
        Assert.Equal(16383,BitConverter.ToInt16(rig.Ring,0));Assert.Equal(-16383,BitConverter.ToInt16(rig.Ring,2));
    }
    [Fact] public void MonoMicrophonePreservesDownmixGainAndMute()
    {
        var (format,data)=Sample("pcm24");using var rig=new MicRig(format,1);rig.Start();
        // A host that holds the stream open, so the ring takes every block:
        // with none open it holds one block of lead (MicTakesBlock).
        var engine=typeof(HMMicrophoneInput).GetField("_engine",Private)!.GetValue(rig.Microphone)!;
        ((Dictionary<byte,byte>)engine.GetType().GetField("_altSetting",Private)!.GetValue(engine)!)[0]=1;
        rig.Capture.Emit(data);Assert.Equal(0,BitConverter.ToInt16(rig.Ring,0));
        rig.Feed.MicGain=.5f;rig.Capture.Emit([0,0,64,0,0,64]);Assert.Equal(8191,BitConverter.ToInt16(rig.Ring,2));
        rig.Feed.MicMuted=true;rig.Capture.Emit([0,0,64,0,0,64]);Assert.Equal(0,BitConverter.ToInt16(rig.Ring,4));
        Assert.Equal(6,rig.Microphone.BufferedBytes);
    }
}
