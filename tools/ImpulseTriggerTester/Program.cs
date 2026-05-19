// PadForge Impulse Trigger Tester — drives all four XInput motors
// (low-freq, high-freq, left-trigger, right-trigger) via
// Windows.Gaming.Input.GamepadVibration, the same public API Forza /
// Gears / Halo use to write impulse trigger feedback. PadForge's
// virtual Xbox slot picks up these writes the same way it would from a
// real game.
//
// Pick the slot index that holds PadForge's virtual Xbox VC. With the
// VC online, hitting Q/W (impulse triggers) or E/R (main motors)
// pumps the corresponding motor and you'll feel it on every assigned
// physical pad mapped to that slot — Xbox controllers via the raw HID
// impulse writer, DualSense via the AT Vibration auto-route.

using System;
using System.Threading;
using Windows.Gaming.Input;

namespace ImpulseTriggerTester
{
    public static class Program
    {
        // Per-motor amplitudes (0..1 — GamepadVibration's normalized range).
        private static double _lMain, _rMain, _lTrig, _rTrig;
        private static int _slotIndex;
        private static readonly object _renderLock = new();

        public static int Main(string[] args)
        {
            if (args.Length > 0 && (args[0] == "-h" || args[0] == "--help" || args[0] == "/?"))
            {
                PrintHelp();
                return 0;
            }

            Console.Title = "PadForge Impulse Trigger Tester";
            Console.CursorVisible = false;
            try
            {
                Console.Clear();
                Console.WriteLine("PadForge Impulse Trigger Tester");
                Console.WriteLine("================================");
                Console.WriteLine();
                Console.WriteLine("Drives all four motors (main L/R + impulse-trigger L/R) of");
                Console.WriteLine("the chosen XInput slot via Windows.Gaming.Input. PadForge's");
                Console.WriteLine("virtual Xbox VC at that slot index picks these writes up the");
                Console.WriteLine("same way it would from Forza / Gears / Halo / etc.");
                Console.WriteLine();
                PrintControls();
                Console.WriteLine();

                // Render once before the first key so the user sees current state.
                Render();

                while (true)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.Escape)
                        break;
                    HandleKey(key);
                    Push();
                    Render();
                }
            }
            finally
            {
                // Zero everything on the way out so we don't leave the
                // slot's motors stuck on.
                _lMain = _rMain = _lTrig = _rTrig = 0;
                Push();
                Console.CursorVisible = true;
                Console.WriteLine();
                Console.WriteLine("Motors zeroed. Bye.");
            }
            return 0;
        }

        private static void PrintControls()
        {
            Console.WriteLine("Controls:");
            Console.WriteLine("  Q / A  - Left  impulse trigger motor   +/- 10%");
            Console.WriteLine("  W / S  - Right impulse trigger motor   +/- 10%");
            Console.WriteLine("  E / D  - Left  main (low-freq) motor   +/- 10%");
            Console.WriteLine("  R / F  - Right main (high-freq) motor  +/- 10%");
            Console.WriteLine("  L      - Pulse left  trigger full for 1 second");
            Console.WriteLine("  T      - Pulse right trigger full for 1 second");
            Console.WriteLine("  B      - Pulse both  triggers full for 1 second");
            Console.WriteLine("  M      - Pulse both  main motors full for 1 second");
            Console.WriteLine("  X      - Zero all motors");
            Console.WriteLine("  0..3   - Switch XInput slot (Windows.Gaming.Input enumerates");
            Console.WriteLine("           system XInput slots 0..3 only — pick the slot that");
            Console.WriteLine("           your PadForge Xbox VC occupies)");
            Console.WriteLine("  Esc    - Quit (motors zero'd on exit)");
        }

        private static void PrintHelp()
        {
            Console.WriteLine("Usage: ImpulseTriggerTester [-h|--help]");
            Console.WriteLine();
            PrintControls();
        }

        private static void HandleKey(ConsoleKeyInfo k)
        {
            const double step = 0.10;
            switch (k.Key)
            {
                case ConsoleKey.Q: _lTrig = Math.Clamp(_lTrig + step, 0, 1); break;
                case ConsoleKey.A: _lTrig = Math.Clamp(_lTrig - step, 0, 1); break;
                case ConsoleKey.W: _rTrig = Math.Clamp(_rTrig + step, 0, 1); break;
                case ConsoleKey.S: _rTrig = Math.Clamp(_rTrig - step, 0, 1); break;
                case ConsoleKey.E: _lMain = Math.Clamp(_lMain + step, 0, 1); break;
                case ConsoleKey.D: _lMain = Math.Clamp(_lMain - step, 0, 1); break;
                case ConsoleKey.R: _rMain = Math.Clamp(_rMain + step, 0, 1); break;
                case ConsoleKey.F: _rMain = Math.Clamp(_rMain - step, 0, 1); break;
                case ConsoleKey.X: _lMain = _rMain = _lTrig = _rTrig = 0; break;
                case ConsoleKey.L: Pulse(lt: 1, rt: 0, lm: 0, rm: 0); break;
                case ConsoleKey.T: Pulse(lt: 0, rt: 1, lm: 0, rm: 0); break;
                case ConsoleKey.B: Pulse(lt: 1, rt: 1, lm: 0, rm: 0); break;
                case ConsoleKey.M: Pulse(lt: 0, rt: 0, lm: 1, rm: 1); break;
                case ConsoleKey.D0: case ConsoleKey.NumPad0: _slotIndex = 0; break;
                case ConsoleKey.D1: case ConsoleKey.NumPad1: _slotIndex = 1; break;
                case ConsoleKey.D2: case ConsoleKey.NumPad2: _slotIndex = 2; break;
                case ConsoleKey.D3: case ConsoleKey.NumPad3: _slotIndex = 3; break;
            }
        }

        // Sets the motors to the given values, pushes once, sleeps 1 s,
        // then zeros and pushes again. Synchronous — blocks input until
        // the pulse completes, which matches how a "test fires for 1 s"
        // affordance feels from the user's perspective.
        private static void Pulse(double lt, double rt, double lm, double rm)
        {
            _lTrig = lt; _rTrig = rt; _lMain = lm; _rMain = rm;
            Push();
            Render();
            Thread.Sleep(1000);
            _lTrig = _rTrig = _lMain = _rMain = 0;
            Push();
        }

        private static void Push()
        {
            var gp = GetSlotGamepad();
            if (gp == null) return;
            try
            {
                gp.Vibration = new GamepadVibration
                {
                    LeftMotor    = _lMain,
                    RightMotor   = _rMain,
                    LeftTrigger  = _lTrig,
                    RightTrigger = _rTrig,
                };
            }
            catch
            {
                // Gamepad may have disconnected between enumeration and
                // write. Next Render() will reflect "no gamepad".
            }
        }

        private static Gamepad GetSlotGamepad()
        {
            try
            {
                var gps = Gamepad.Gamepads;
                if (_slotIndex < 0 || _slotIndex >= gps.Count) return null;
                return gps[_slotIndex];
            }
            catch
            {
                return null;
            }
        }

        private static void Render()
        {
            lock (_renderLock)
            {
                var gp = GetSlotGamepad();
                int gpCount;
                try { gpCount = Gamepad.Gamepads.Count; } catch { gpCount = 0; }

                // Status block starts at row 21 (after the 20-line preamble).
                const int row = 21;
                WriteAt(row + 0, $"Slot:              {_slotIndex}   ({gpCount} gamepad(s) visible, slot {(gp == null ? "EMPTY" : "ready")})");
                WriteAt(row + 1, $"L Main Motor:      {Bar(_lMain)}");
                WriteAt(row + 2, $"R Main Motor:      {Bar(_rMain)}");
                WriteAt(row + 3, $"L Impulse Trigger: {Bar(_lTrig)}");
                WriteAt(row + 4, $"R Impulse Trigger: {Bar(_rTrig)}");
            }
        }

        private static void WriteAt(int row, string text)
        {
            try
            {
                Console.SetCursorPosition(0, row);
                // Pad to console width so leftover characters from a
                // longer previous render don't trail.
                int width = Math.Max(Console.WindowWidth - 1, text.Length);
                Console.Write(text.PadRight(width));
            }
            catch
            {
                // Console redirect / terminal resize edge cases — just
                // fall back to a plain line so the user still sees output.
                Console.WriteLine(text);
            }
        }

        private static string Bar(double v)
        {
            const int width = 20;
            int filled = (int)Math.Round(v * width);
            return $"[{new string('#', filled)}{new string('-', width - filled)}] {(int)Math.Round(v * 100),3}%";
        }
    }
}
