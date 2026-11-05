using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.RemoteLink;
using PadForge.ViewModels;
using Xunit.Abstractions;

namespace PadForge.Tests;

[Collection("AudioProfileLifecycle")]
public sealed class EffectDeliveryTests(ITestOutputHelper output)
{
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    const BindingFlags Static=BindingFlags.Static|BindingFlags.NonPublic;
    static void StopAudioWorker()
    {
        AudioPassthroughService.Shutdown();
        foreach(string name in new[]{"_workerThread","_btThread"})
            if(typeof(AudioPassthroughService).GetField(name,Static)!.GetValue(null) is Thread t&&t.IsAlive)Assert.True(t.Join(5000));
    }
    [Fact] public void RejectedRelayWriteReportsNoDelivery()
    {
        string path="peer://delivery-"+Guid.NewGuid();var sender=RemoteLinkOutputRouter.SendOutput;
        try
        {
            RemoteLinkOutputRouter.SendOutput=null;
            var profile=HMaestroProfileCatalog.GetProfileById("dualsense");Assert.NotNull(profile);
            var fields=Ds5EffectSynthesizer.BuildFields(new DeviceSlotConfig());
            bool failed=PlayStationEffectWriter.Write(path,profile,fields);
            int writes=0;RemoteLinkOutputRouter.Register(path,"delivery",0);RemoteLinkOutputRouter.SendOutput=(_,_,_)=>writes++;
            bool sent=PlayStationEffectWriter.Write(path,profile,fields);
            output.WriteLine($"rejected result={failed} accepted result={sent} sent frames={writes}");
            Assert.True(sent);Assert.Equal(1,writes);Assert.False(failed);
        }
        finally{RemoteLinkOutputRouter.Unregister(path);RemoteLinkOutputRouter.SendOutput=sender;}
    }

    [Theory] [InlineData("local")] [InlineData("relay")] [InlineData("claimed")]
    public void RejectedEffectRetainsItsTransitionUntilSuccessfulRetry(string mode)
    {
        StopAudioWorker();
        string dir=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"PadForge-EffectDelivery-"+Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);string file=System.IO.Path.Combine(dir,"BTHENUM-report.bin");
        string path=mode=="relay"?"peer://delivery-"+Guid.NewGuid():file;
        var oldDevices=SettingsManager.UserDevices;var oldSettings=SettingsManager.UserSettings;
        var sender=RemoteLinkOutputRouter.SendOutput;var cfgs=UserEffectsDispatcher.SlotPerDeviceConfigsProvider;
        var id=Guid.NewGuid();UserEffectsDispatcher dispatcher=null;
        var clears=(HashSet<Guid>)typeof(AudioPassthroughService).GetField("_speakerPathCleared",Static)!.GetValue(null);
        var gate=typeof(AudioPassthroughService).GetField("_lock",Static)!.GetValue(null);
        try
        {
            SettingsManager.UserDevices=new();SettingsManager.UserSettings=new();UserEffectsDispatcher.SlotPerDeviceConfigsProvider=null;
            var device=new UserDevice{InstanceGuid=id,VendorId=0x054c,ProdId=0x0ce6,IsOnline=true,IsEnabled=true,DevicePath=path};
            SettingsManager.UserDevices.Items.Add(device);SettingsManager.UserSettings.Items.Add(new UserSetting{InstanceGuid=id,MapTo=15});
            var cfg=new DeviceSlotConfig{LightbarMode=LightbarMode.Off,AudioOutputPath=AudioOutputPath.Automatic};
            dispatcher=new UserEffectsDispatcher(15,cfg,startTimer:false);
            var bt=(System.Collections.Concurrent.ConcurrentDictionary<Guid,byte>)typeof(UserEffectsDispatcher).GetField("_btBarReleasePending",Private)!.GetValue(dispatcher);
            lock(gate)clears.Add(id);
            if(mode=="claimed"){System.IO.File.WriteAllBytes(file,[]);Assert.True(RemoteLinkOutputRouter.ClaimOutput(path));}
            if(mode=="relay")RemoteLinkOutputRouter.SendOutput=null;
            dispatcher.ApplyOnce();
            bool firstSpeaker=AudioPassthroughService.PeekSpeakerPathCleared(id),firstBt=bt.ContainsKey(id);
            bool retryArmed=(bool)typeof(UserEffectsDispatcher).GetField("_animTickActive",Private)!.GetValue(dispatcher);
            (typeof(UserEffectsDispatcher).GetField("_animTimer",Private)!.GetValue(dispatcher) as Timer)?.Change(Timeout.Infinite,Timeout.Infinite);
            int delivered=0;var reports=new List<byte[]>();
            if(mode=="relay"){RemoteLinkOutputRouter.Register(path,"delivery",0);RemoteLinkOutputRouter.SendOutput=(_,_,bytes)=>{delivered++;Assert.True(OutputEffectCodec.TryDecode(bytes,out var e));reports.Add(e.SonyBody);};}
            else{RemoteLinkOutputRouter.ReleaseDevice(path);System.IO.File.WriteAllBytes(file,[]);}
            typeof(UserEffectsDispatcher).GetMethod("OnAnimTickCore",Private)!.Invoke(dispatcher,[null]);
            int retryDelivered=mode=="relay"?delivered:System.IO.File.ReadAllBytes(file).Length;
            dispatcher.ApplyOnce();
            byte[] local=mode!="relay"?System.IO.File.ReadAllBytes(file):[];
            if(mode!="relay")delivered=local.Length;
            output.WriteLine($"mode={mode} pending speaker={firstSpeaker} pending BT={firstBt} retry armed={retryArmed} retry bytes/frames={retryDelivered} control bytes/frames={delivered}");
            Assert.True(delivered>0);Assert.False(AudioPassthroughService.PeekSpeakerPathCleared(id));Assert.DoesNotContain(id,bt.Keys);
            Assert.True(firstSpeaker);if(mode!="relay")Assert.True(firstBt);
            Assert.True(retryArmed);Assert.True(retryDelivered>0);
            if(mode=="relay")Assert.NotEqual(0,reports[0][0]&3);
        }
        finally
        {
            dispatcher?.Dispose();lock(gate)clears.Remove(id);RemoteLinkOutputRouter.Unregister(path);RemoteLinkOutputRouter.ReleaseDevice(path);
            RemoteLinkOutputRouter.SendOutput=sender;UserEffectsDispatcher.SlotPerDeviceConfigsProvider=cfgs;
            SettingsManager.UserDevices=oldDevices;SettingsManager.UserSettings=oldSettings;
            // A virus scanner can still hold the file the frames landed in. Nothing the test
            // says depends on the folder going away.
            try{System.IO.Directory.Delete(dir,true);}catch(System.IO.IOException){}
        }
    }

    [Fact] public void FrameBuiltBeforeFailureCannotAcknowledgeRecovery()
    {
        StopAudioWorker();
        var devices=SettingsManager.UserDevices;var settings=SettingsManager.UserSettings;
        var configs=UserEffectsDispatcher.SlotPerDeviceConfigsProvider;var sender=RemoteLinkOutputRouter.SendScopedOutput;
        string path="peer://prebuilt-"+Guid.NewGuid();var id=Guid.NewGuid();UserEffectsDispatcher dispatcher=null;
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        var successful=new List<byte[]>();int attempts=0;Exception errorA=null,errorB=null;Thread a=null,b=null;
        try
        {
            SettingsManager.UserDevices=new();SettingsManager.UserSettings=new();UserEffectsDispatcher.SlotPerDeviceConfigsProvider=null;
            SettingsManager.UserDevices.Items.Add(new UserDevice{InstanceGuid=id,VendorId=0x054c,ProdId=0x0ce6,IsOnline=true,DevicePath=path});
            SettingsManager.UserSettings.Items.Add(new UserSetting{InstanceGuid=id,MapTo=15});
            dispatcher=new UserEffectsDispatcher(15,new DeviceSlotConfig{LightbarMode=LightbarMode.Off},startTimer:false);
            var connection=new LinkConnectionLifetime("delivery",new LinkExposureSnapshot([]),()=>true,(_,_,_,_)=>true);
            RemoteLinkOutputRouter.Register(path,"delivery",0,connection,null);
            RemoteLinkOutputRouter.SendScopedOutput=(_,_,_,bytes)=>
            {
                if(Interlocked.Increment(ref attempts)==1){entered.Set();if(!release.Wait(5000))throw new TimeoutException();return false;}
                Assert.True(OutputEffectCodec.TryDecode(bytes,out var effect));successful.Add(effect.SonyBody);return true;
            };
            a=new Thread(()=>{try{dispatcher.ApplyOnce();}catch(Exception e){errorA=e;}});
            b=new Thread(()=>{try{dispatcher.ApplyOnce();}catch(Exception e){errorB=e;}});
            a.Start();Assert.True(entered.Wait(5000));
            var sequence=typeof(UserEffectsDispatcher).GetField("s_dispatchSeq",Static)!;long first=(long)sequence.GetValue(null);
            b.Start();Assert.True(SpinWait.SpinUntil(()=>(long)sequence.GetValue(null)>first,5000));
            release.Set();Assert.True(a.Join(5000));Assert.True(b.Join(5000));
            (typeof(UserEffectsDispatcher).GetField("_animTimer",Private)!.GetValue(dispatcher) as Timer)?.Change(Timeout.Infinite,Timeout.Infinite);
            Assert.Null(errorA);Assert.Null(errorB);
            typeof(UserEffectsDispatcher).GetMethod("OnAnimTickCore",Private)!.Invoke(dispatcher,[null]);
            Assert.NotEmpty(successful);Assert.NotEqual(0,successful[0][0]&3);
        }
        finally
        {
            release.Set();if(a?.IsAlive==true)Assert.True(a.Join(5000));if(b?.IsAlive==true)Assert.True(b.Join(5000));
            dispatcher?.Dispose();RemoteLinkOutputRouter.Unregister(path);RemoteLinkOutputRouter.SendScopedOutput=sender;
            UserEffectsDispatcher.SlotPerDeviceConfigsProvider=configs;SettingsManager.UserDevices=devices;SettingsManager.UserSettings=settings;
        }
    }

    // A change-gated audio assert (headphone volume, output path, the DualShock 4
    // microphone gain) is spent only when a frame carrying it lands. A frame the
    // stale-skip drops leaves it owed, so the next frame carries it.
    [Theory] [InlineData("volume")] [InlineData("path")] [InlineData("ds4-volume")] [InlineData("ds4-mic")]
    public void ASupersededFrameLeavesItsAudioAssertOwed(string change)
    {
        StopAudioWorker();
        bool ds4=change.StartsWith("ds4");
        string dir=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"PadForge-AudioAssert-"+Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);string file=System.IO.Path.Combine(dir,"BTHENUM-report.bin");
        var oldDevices=SettingsManager.UserDevices;var oldSettings=SettingsManager.UserSettings;var cfgs=UserEffectsDispatcher.SlotPerDeviceConfigsProvider;
        var seqs=(System.Collections.Concurrent.ConcurrentDictionary<string,long>)typeof(UserEffectsDispatcher).GetField("s_lastWriteSeq",Static)!.GetValue(null)!;
        var mics=(System.Collections.IDictionary)typeof(AudioPassthroughService).GetField("_ds4Mic",Static)!.GetValue(null)!;
        var id=Guid.NewGuid();UserEffectsDispatcher dispatcher=null;
        try
        {
            SettingsManager.UserDevices=new();SettingsManager.UserSettings=new();UserEffectsDispatcher.SlotPerDeviceConfigsProvider=null;
            System.IO.File.WriteAllBytes(file,[]);
            SettingsManager.UserDevices.Items.Add(new UserDevice{InstanceGuid=id,VendorId=0x054c,ProdId=(ushort)(ds4?0x09cc:0x0ce6),IsOnline=true,IsEnabled=true,DevicePath=file});
            SettingsManager.UserSettings.Items.Add(new UserSetting{InstanceGuid=id,MapTo=15});
            var cfg=new DeviceSlotConfig{LightbarMode=LightbarMode.Off,AudioOutputPath=AudioOutputPath.StereoHeadset,HeadphoneVolume=80};
            dispatcher=new UserEffectsDispatcher(15,cfg,startTimer:false);
            void Apply(){dispatcher.ApplyOnce();(typeof(UserEffectsDispatcher).GetField("_animTimer",Private)!.GetValue(dispatcher) as Timer)?.Change(Timeout.Infinite,Timeout.Infinite);}
            byte Flags(){var r=System.IO.File.ReadAllBytes(file);Assert.True(r.Length>3,"no frame landed");return r[3];}
            // Owed bits: DualSense valid_flag0 0x10 headphone volume and 0x80
            // audio control, DualShock 4 byte 3 0x30 headphone volume and 0x40
            // microphone gain.
            byte bit=change switch{"volume"=>0x10,"path"=>0x80,"ds4-volume"=>0x30,_=>0x40};
            Apply();
            ((Dictionary<Guid,int>)typeof(UserEffectsDispatcher).GetField("_audioMuteBurstLeft",Private)!.GetValue(dispatcher)!)[id]=0;
            Apply();
            System.IO.File.WriteAllBytes(file,[]);Apply();
            Assert.Equal(0,Flags()&bit); // a steady frame owes nothing

            seqs[file]=long.MaxValue; // every frame now reads as superseded
            System.IO.File.WriteAllBytes(file,[]);
            if(change=="volume"||change=="ds4-volume")cfg.HeadphoneVolume=40;
            else if(change=="path")cfg.AudioOutputPath=AudioOutputPath.SpeakerOnly;
            else
            {
                typeof(AudioPassthroughService).GetMethod("NoteDs4MicPlugged",Static)!.Invoke(null,[id,true]);
                typeof(AudioPassthroughService).GetMethod("SetDs4MicWanted",Static)!.Invoke(null,[id,-1,false,true]);
                Assert.True(AudioPassthroughService.IsDs4MicArmed(id));
            }
            Apply();
            Assert.Empty(System.IO.File.ReadAllBytes(file));

            seqs.TryRemove(file,out _);
            Apply();
            output.WriteLine($"change={change} flags=0x{Flags():X2}");
            Assert.Equal(bit,Flags()&bit);
        }
        finally
        {
            dispatcher?.Dispose();seqs.TryRemove(file,out _);mics.Remove(id);
            UserEffectsDispatcher.SlotPerDeviceConfigsProvider=cfgs;SettingsManager.UserDevices=oldDevices;SettingsManager.UserSettings=oldSettings;
            // A virus scanner can still hold the file the frames landed in. Nothing the test
            // says depends on the folder going away.
            try{System.IO.Directory.Delete(dir,true);}catch(System.IO.IOException){}
        }
    }

    // A frame captured before a re-arm, or before a newer frame's commit,
    // lands late and must not undo either. A DualShock 4's USB route (#546)
    // follows the same rules, and a frame with no route keeps the last one.
    [Fact] public void ALateCommitCannotUndoAReArmOrANewerCommit()
    {
        var dispatcher=(UserEffectsDispatcher)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(UserEffectsDispatcher));
        foreach(var f in new[]{"_audioPrevGate","_audioCommitSeq","_prevHeadphoneVolume","_prevAudioOutputPath","_prevDs4MicArmed","_prevDs4UsbRoute","_usbRouteRetryAt"})
        {
            var field=typeof(UserEffectsDispatcher).GetField(f,Private)!;
            field.SetValue(dispatcher,Activator.CreateInstance(field.FieldType));
        }
        var volumes=(Dictionary<Guid,int>)typeof(UserEffectsDispatcher).GetField("_prevHeadphoneVolume",Private)!.GetValue(dispatcher)!;
        var routes=(Dictionary<Guid,int>)typeof(UserEffectsDispatcher).GetField("_prevDs4UsbRoute",Private)!.GetValue(dispatcher)!;
        var commit=typeof(UserEffectsDispatcher).GetMethod("CommitDeliveredAudio",Private)!;
        var rearm=typeof(UserEffectsDispatcher).GetMethod("RearmAudioAsserts",Private)!;
        long now=(long)typeof(UserEffectsDispatcher).GetField("s_dispatchSeq",Static)!.GetValue(null)!;
        var id=Guid.NewGuid();
        commit.Invoke(dispatcher,[id,now-4,(50,1,false,3)]);
        commit.Invoke(dispatcher,[id,now-6,(20,1,false,0)]);
        Assert.Equal((50,3),(volumes[id],routes[id]));
        commit.Invoke(dispatcher,[id,now-3,(50,1,false,-1)]);
        Assert.Equal(3,routes[id]);
        rearm.Invoke(dispatcher,[id]);
        commit.Invoke(dispatcher,[id,now-1,(20,1,false,0)]);
        Assert.False(volumes.ContainsKey(id));
        Assert.False(routes.ContainsKey(id));
    }

    [Theory] [InlineData("unassign")] [InlineData("offline")] [InlineData("owner")] [InlineData("dispose")]
    public void RetiringTheOutputTargetStopsItsRetry(string transition)
    {
        StopAudioWorker();
        var devices=SettingsManager.UserDevices;var settings=SettingsManager.UserSettings;
        var configs=UserEffectsDispatcher.SlotPerDeviceConfigsProvider;
        UserEffectsDispatcher dispatcher=null,owner=null;var id=Guid.NewGuid();
        try
        {
            SettingsManager.UserDevices=new();SettingsManager.UserSettings=new();UserEffectsDispatcher.SlotPerDeviceConfigsProvider=null;
            var device=new UserDevice{InstanceGuid=id,VendorId=0x054c,ProdId=0x0ce6,IsOnline=true,DevicePath="peer://unavailable-"+id};
            SettingsManager.UserDevices.Items.Add(device);SettingsManager.UserSettings.Items.Add(new UserSetting{InstanceGuid=id,MapTo=15});
            dispatcher=new UserEffectsDispatcher(15,new DeviceSlotConfig{LightbarMode=LightbarMode.Off},startTimer:false);
            dispatcher.ApplyOnce();
            var active=typeof(UserEffectsDispatcher).GetField("_animTickActive",Private)!;
            Assert.True((bool)active.GetValue(dispatcher));
            (typeof(UserEffectsDispatcher).GetField("_animTimer",Private)!.GetValue(dispatcher) as Timer)?.Change(Timeout.Infinite,Timeout.Infinite);
            if(transition=="unassign")SettingsManager.UserSettings.Items.Clear();
            else if(transition=="offline")device.IsOnline=false;
            else if(transition=="owner")
            {
                owner=new UserEffectsDispatcher(14,new DeviceSlotConfig{LightbarMode=LightbarMode.Off},startTimer:false);
                SettingsManager.UserSettings.Items.Add(new UserSetting{InstanceGuid=id,MapTo=14});
            }
            else dispatcher.Dispose();
            var tick=typeof(UserEffectsDispatcher).GetMethod("OnAnimTickCore",Private)!;
            tick.Invoke(dispatcher,[null]);tick.Invoke(dispatcher,[null]);
            Assert.False((bool)active.GetValue(dispatcher));
            var failures=(System.Collections.Concurrent.ConcurrentDictionary<Guid,long>)typeof(UserEffectsDispatcher).GetField("_failedDeliveries",Private)!.GetValue(dispatcher);
            Assert.Empty(failures);
        }
        finally
        {
            owner?.Dispose();dispatcher?.Dispose();UserEffectsDispatcher.SlotPerDeviceConfigsProvider=configs;
            SettingsManager.UserDevices=devices;SettingsManager.UserSettings=settings;
        }
    }
}
