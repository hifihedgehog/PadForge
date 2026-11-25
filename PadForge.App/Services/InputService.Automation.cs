using System;
using System.Globalization;
using System.Text;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Services.Automation;

namespace PadForge.Services
{
    /// <summary>
    /// The engine's side of the Automation pipe (#597): the devices programs
    /// add join the engine's device list while it runs, and a program reads
    /// what a virtual controller sends.
    /// </summary>
    public partial class InputService
    {
        private AutomationHost _automationHost;

        /// <summary>Gives the input service Automation's host, made after the
        /// settings load. An engine that already runs is handed to it at
        /// once, and every later start hands it again.</summary>
        internal void AttachAutomation(AutomationHost host)
        {
            _automationHost = host;
            if (host == null) return;
            host.EngineState = ReadEngineState;
            // A program's device nobody assigned leaves the Devices page with
            // its row, through the walk every device change takes.
            host.Sessions.Devices.Forgotten += () =>
            {
                var manager = _inputManager;
                if (manager != null) OnDevicesUpdated(manager, EventArgs.Empty);
                else _dispatcher.BeginInvoke(new Action(SyncDevicesList));
            };
            AttachAutomationToEngine();
        }

        private void AttachAutomationToEngine()
        {
            var manager = _inputManager;
            if (manager == null) return;
            _automationHost?.AttachEngine(new AutomationEngineLink(this, manager));
        }

        private void DetachAutomationFromEngine() => _automationHost?.DetachEngine();

        /// <summary>The engine as one run of it: a link made at a start
        /// speaks to that start's input manager only.</summary>
        private sealed class AutomationEngineLink : IAutomationEngine
        {
            private readonly InputService _service;
            private readonly InputManager _manager;

            public AutomationEngineLink(InputService service, InputManager manager)
            {
                _service = service;
                _manager = manager;
            }

            public void Register(ProgramDevice device) => _manager.RegisterProgramDevice(device);

            public void Unregister(ProgramDevice device) => _manager.UnregisterProgramDevice(device);

            public string Outputs(int number) => _service.AutomationOutputs(_manager, number);
        }

        /// <summary>
        /// What virtual controller <paramref name="number"/> sends to the
        /// game now, read off the poll thread from the combined output the
        /// poll thread writes (DisplayFrameFor's rule: struct copies and
        /// whole-written fields). Null when no created controller has that
        /// number.
        /// </summary>
        /// <summary>The rate the engine runs at now, or 0 while it is
        /// stopped, for an assistant's status (#600).</summary>
        internal double EngineFrequency => _inputManager?.CurrentFrequency ?? 0;

        /// <summary>What virtual controller <paramref name="number"/> sends to
        /// the game now, as the outputs line words it, or null while the
        /// engine is stopped or no controller has that number. For an
        /// assistant's list_controllers (#597).</summary>
        internal string AutomationOutputsNow(int number)
        {
            var manager = _inputManager;
            return manager == null || !manager.IsRunning ? null : AutomationOutputs(manager, number);
        }

        internal string AutomationOutputs(InputManager manager, int number)
        {
            int slot = SettingsManager.SlotOrders.PadIndexForGlobalSlotNumber(number);
            if (slot < 0 || slot >= InputManager.MaxPads) return null;
            var created = SettingsManager.SlotCreated;
            if (slot >= created.Length || !created[slot]) return null;
            var pads = _mainVm.Pads;
            var pad = slot < pads.Count ? pads[slot] : null;
            if (pad == null) return null;
            return OutputsLine(pad.OutputType, manager.CombinedOutputStates[slot], manager.CombinedRawHidStates[slot],
                manager.CombinedKbmRawStates[slot], manager.CombinedMidiRawStates[slot], manager.CombinedVrRawStates[slot]);
        }

        /// <summary>One controller's output as the words after <c>ok</c>:
        /// its type, then its buttons as hex and its axes in the virtual
        /// controller's own ranges.</summary>
        internal static string OutputsLine(VirtualControllerType type, Gamepad gamepad, RawHidState raw,
            KbmRawState kbm, MidiRawState midi, VrRawState vr)
        {
            var sb = new StringBuilder(256);
            sb.Append("type=").Append(AutomationCommands.Word(type));
            switch (type)
            {
                case VirtualControllerType.Extended:
                {
                    var axes = raw.Axes;
                    var buttons = raw.Buttons;
                    var povs = raw.Povs;
                    sb.Append(" a=");
                    if (axes == null || axes.Length == 0) sb.Append('-');
                    else for (int i = 0; i < axes.Length; i++) sb.Append(i > 0 ? "," : string.Empty).Append(axes[i]);
                    sb.Append(" b=");
                    if (buttons == null || buttons.Length == 0) sb.Append('-');
                    else foreach (uint word in buttons) sb.Append(word.ToString("x8", CultureInfo.InvariantCulture));
                    sb.Append(" h=");
                    if (povs == null || povs.Length == 0) sb.Append('-');
                    else for (int i = 0; i < povs.Length; i++) sb.Append(i > 0 ? "," : string.Empty).Append(povs[i]);
                    break;
                }
                case VirtualControllerType.KeyboardMouse:
                    sb.Append(" keys=")
                      .Append(kbm.Keys0.ToString("x16", CultureInfo.InvariantCulture))
                      .Append(kbm.Keys1.ToString("x16", CultureInfo.InvariantCulture))
                      .Append(kbm.Keys2.ToString("x16", CultureInfo.InvariantCulture))
                      .Append(kbm.Keys3.ToString("x16", CultureInfo.InvariantCulture))
                      .Append(" mouse=").Append(kbm.MouseButtons.ToString("x2", CultureInfo.InvariantCulture))
                      .Append(" dx=").Append(kbm.MouseDeltaX)
                      .Append(" dy=").Append(kbm.MouseDeltaY)
                      .Append(" wheel=").Append(kbm.ScrollDelta)
                      .Append(" hwheel=").Append(kbm.ScrollDeltaH);
                    break;
                case VirtualControllerType.Midi:
                {
                    var notes = midi.Notes;
                    var cc = midi.CcValues;
                    sb.Append(" notes=");
                    if (notes == null || notes.Length == 0) sb.Append('-');
                    else
                    {
                        for (int b = 0; b < (notes.Length + 7) / 8; b++)
                        {
                            int v = 0;
                            for (int bit = 0; bit < 8; bit++)
                                if (b * 8 + bit < notes.Length && notes[b * 8 + bit]) v |= 1 << bit;
                            sb.Append(v.ToString("x2", CultureInfo.InvariantCulture));
                        }
                    }
                    sb.Append(" cc=");
                    if (cc == null || cc.Length == 0) sb.Append('-');
                    else for (int i = 0; i < cc.Length; i++) sb.Append(i > 0 ? "," : string.Empty).Append(cc[i]);
                    break;
                }
                case VirtualControllerType.Vr:
                    sb.Append(" left=").Append(vr.Left.Buttons).Append(',').Append(vr.Left.Trigger).Append(',')
                      .Append(vr.Left.Grip).Append(',').Append(vr.Left.StickX).Append(',').Append(vr.Left.StickY)
                      .Append(" right=").Append(vr.Right.Buttons).Append(',').Append(vr.Right.Trigger).Append(',')
                      .Append(vr.Right.Grip).Append(',').Append(vr.Right.StickX).Append(',').Append(vr.Right.StickY);
                    break;
                default:
                    sb.Append(" buttons=").Append(gamepad.Buttons.ToString("x4", CultureInfo.InvariantCulture))
                      .Append(" lt=").Append(gamepad.LeftTrigger)
                      .Append(" rt=").Append(gamepad.RightTrigger)
                      .Append(" lx=").Append(gamepad.ThumbLX)
                      .Append(" ly=").Append(gamepad.ThumbLY)
                      .Append(" rx=").Append(gamepad.ThumbRX)
                      .Append(" ry=").Append(gamepad.ThumbRY);
                    break;
            }
            return sb.ToString();
        }
    }
}
