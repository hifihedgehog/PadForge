using System;
using PadForge.Common.Input;
using Valve.VR;
using Xunit;

namespace PadForge.Tests
{
    // Locks the VR consumer lane's pure contracts (#287), each against its
    // reference: the runtime-path discovery (the exact file
    // vrpathregistry_public.cpp reads, including the escaped-backslash JSON
    // shape observed live on the bench), the pose-matrix math (OpenVR's
    // right-handed +X right / +Y up / -Z forward frame, HmdMatrix34_t
    // row-major with device basis vectors in the columns), the controller
    // axis classification (Prop_AxisNType_Int32 with the second trigger-typed
    // axis being the analog grip), and the button/trigger scaling.
    [Collection("SettingsManagerStatics")]
    public class OpenVrConsumerTests
    {
        // ── runtime discovery ───────────────────────────────────────────────────

        [Fact]
        public void VrPathJson_ParsesTheRealRegistryShape()
        {
            // Byte-shape of the file observed on this bench (escaped
            // backslashes, multiple sections, runtime not first).
            string json = "{\n\t\"config\" : \n\t[\n\t\t\"C:\\\\SteamVR-config\"\n\t],\n"
                + "\t\"runtime\" : \n\t[\n\t\t\"C:\\\\SteamVR\"\n\t],\n\t\"version\" : 1\n}";
            Assert.Equal(@"C:\SteamVR", OpenVrConsumerService.ParseRuntimePathFromVrPathJson(json));
        }

        [Fact]
        public void VrPathJson_RefusesMalformedInput()
        {
            Assert.Null(OpenVrConsumerService.ParseRuntimePathFromVrPathJson(null));
            Assert.Null(OpenVrConsumerService.ParseRuntimePathFromVrPathJson(""));
            Assert.Null(OpenVrConsumerService.ParseRuntimePathFromVrPathJson("{}"));
            Assert.Null(OpenVrConsumerService.ParseRuntimePathFromVrPathJson("{\"runtime\": []}"));
            Assert.Null(OpenVrConsumerService.ParseRuntimePathFromVrPathJson("{\"runtime\""));
        }

        [Fact]
        public void RuntimeDllPath_ComposesOrRefuses()
        {
            Assert.Equal(@"C:\SteamVR\bin\win64\openvr_api.dll",
                OpenVrConsumerService.RuntimeDllPath(@"C:\SteamVR"));
            Assert.Null(OpenVrConsumerService.RuntimeDllPath(null));
            Assert.Null(OpenVrConsumerService.RuntimeDllPath(""));
        }

        // ── pose math ───────────────────────────────────────────────────────────

        private static HmdMatrix34_t Matrix(
            float m0, float m1, float m2,
            float m4, float m5, float m6,
            float m8, float m9, float m10)
        {
            return new HmdMatrix34_t
            {
                m0 = m0, m1 = m1, m2 = m2, m3 = 0,
                m4 = m4, m5 = m5, m6 = m6, m7 = 0,
                m8 = m8, m9 = m9, m10 = m10, m11 = 0,
            };
        }

        [Fact]
        public void Euler_IdentityIsZero()
        {
            var m = Matrix(1, 0, 0, 0, 1, 0, 0, 0, 1);
            var (yaw, pitch, roll) = OpenVrConsumerService.EulerFromPoseMatrix(in m);
            Assert.Equal(0f, yaw, 3);
            Assert.Equal(0f, pitch, 3);
            Assert.Equal(0f, roll, 3);
        }

        [Fact]
        public void Euler_YawRightIsPositive()
        {
            // Rotation about +Y by -90 deg (a right turn): device basis
            // columns X=(0,0,1), Y=(0,1,0), Z=(-1,0,0); forward = -Z = +X.
            var m = Matrix(0, 0, -1, 0, 1, 0, 1, 0, 0);
            var (yaw, pitch, roll) = OpenVrConsumerService.EulerFromPoseMatrix(in m);
            Assert.Equal(90f, yaw, 3);
            Assert.Equal(0f, pitch, 3);
            Assert.Equal(0f, roll, 3);
        }

        [Fact]
        public void Euler_PitchUpIsPositive()
        {
            // Rotation about +X by +90 deg: columns X=(1,0,0), Y=(0,0,1),
            // Z=(0,-1,0); forward = -Z = (0,1,0), straight up.
            var m = Matrix(1, 0, 0, 0, 0, -1, 0, 1, 0);
            var (yaw, pitch, roll) = OpenVrConsumerService.EulerFromPoseMatrix(in m);
            Assert.Equal(90f, pitch, 3);
        }

        [Fact]
        public void Euler_RollRightIsPositive()
        {
            // Rotation about +Z by -90 deg (head tilts right): columns
            // X=(0,-1,0), Y=(1,0,0), Z=(0,0,1).
            var m = Matrix(0, 1, 0, -1, 0, 0, 0, 0, 1);
            var (yaw, pitch, roll) = OpenVrConsumerService.EulerFromPoseMatrix(in m);
            Assert.Equal(90f, roll, 3);
        }

        [Fact]
        public void WorldToDevice_UndoesTheDeviceRotation()
        {
            // Same right-turned pose: world +X is the device's forward (-Z).
            var m = Matrix(0, 0, -1, 0, 1, 0, 1, 0, 0);
            var (x, y, z) = OpenVrConsumerService.WorldToDevice(in m, 1f, 0f, 0f);
            Assert.Equal(0f, x, 3);
            Assert.Equal(0f, y, 3);
            Assert.Equal(-1f, z, 3);
        }

        [Fact]
        public void AxisScaling_ClampsAndCenters()
        {
            Assert.Equal(0, OpenVrConsumerService.AxisFromScaled(0f, 0.35f));
            Assert.Equal(32767, OpenVrConsumerService.AxisFromScaled(0.35f, 0.35f));
            Assert.Equal(32767, OpenVrConsumerService.AxisFromScaled(5f, 0.35f));
            Assert.Equal(-32767, OpenVrConsumerService.AxisFromScaled(-5f, 0.35f));
            Assert.Equal(16384, OpenVrConsumerService.AxisFromScaled(0.175f, 0.35f), 0.0);
        }

        [Fact]
        public void WrapDegrees_KeepsDeltasSmall()
        {
            Assert.Equal(10f, OpenVrConsumerService.WrapDegrees(10f), 3);
            Assert.Equal(-10f, OpenVrConsumerService.WrapDegrees(350f), 3);
            Assert.Equal(10f, OpenVrConsumerService.WrapDegrees(-350f), 3);
            Assert.Equal(180f, OpenVrConsumerService.WrapDegrees(180f), 3);
            Assert.Equal(180f, OpenVrConsumerService.WrapDegrees(-180f), 3);
        }

        // ── controller surface ──────────────────────────────────────────────────

        [Fact]
        public void AxisClassification_SecondTriggerIsTheGrip()
        {
            // A WMR/Index-style layout: joystick, then two trigger-typed axes.
            var roles = OpenVrConsumerService.ClassifyAxes(new[] { 2, 3, 3, 0, 0 });
            Assert.Equal(OpenVrConsumerService.VrAxisRole.Joystick, roles[0]);
            Assert.Equal(OpenVrConsumerService.VrAxisRole.Trigger, roles[1]);
            Assert.Equal(OpenVrConsumerService.VrAxisRole.Grip, roles[2]);

            // A Vive-style layout: trackpad first, single trigger.
            roles = OpenVrConsumerService.ClassifyAxes(new[] { 1, 3, 0, 0, 0 });
            Assert.Equal(OpenVrConsumerService.VrAxisRole.TrackPad, roles[0]);
            Assert.Equal(OpenVrConsumerService.VrAxisRole.Trigger, roles[1]);
            Assert.Equal(OpenVrConsumerService.VrAxisRole.None, roles[2]);
        }

        [Fact]
        public void ButtonBits_IncludingTheAxisClickRange()
        {
            // EVRButtonId: System=0, ApplicationMenu=1, Grip=2, A=7,
            // Axis0 click = 32 (openvr_api.cs:5457-5466).
            Assert.True(OpenVrConsumerService.ButtonPressed(1ul << 0, 0));
            Assert.True(OpenVrConsumerService.ButtonPressed(1ul << 7, 7));
            Assert.True(OpenVrConsumerService.ButtonPressed(1ul << 33, 33));
            Assert.False(OpenVrConsumerService.ButtonPressed(1ul << 33, 32));
        }

        [Fact]
        public void TriggerScale_RestsAtMinFullAtMax()
        {
            Assert.Equal(-32767, OpenVrConsumerService.TriggerAxis01(0f));
            Assert.Equal(32767, OpenVrConsumerService.TriggerAxis01(1f));
            Assert.Equal(-32767, OpenVrConsumerService.TriggerAxis01(-3f));
            Assert.Equal(32767, OpenVrConsumerService.TriggerAxis01(9f));
        }

        [Fact]
        public void SelfFilter_ExcludesOurOwnHandsUnlessOverridden()
        {
            // Our OpenVR driver stamps Manufacturer "HIDMaestro"
            // (controller_device.cpp:70); consuming our own virtual hands
            // would loop a slot's output back into its input.
            Assert.True(OpenVrConsumerService.IsSelfEmitted("HIDMaestro", consumeSelfOverride: false));
            Assert.True(OpenVrConsumerService.IsSelfEmitted("hidmaestro", consumeSelfOverride: false));
            Assert.False(OpenVrConsumerService.IsSelfEmitted("HIDMaestro", consumeSelfOverride: true));
            Assert.False(OpenVrConsumerService.IsSelfEmitted("HP", consumeSelfOverride: false));
            Assert.False(OpenVrConsumerService.IsSelfEmitted(null, consumeSelfOverride: false));
        }
    }
}
