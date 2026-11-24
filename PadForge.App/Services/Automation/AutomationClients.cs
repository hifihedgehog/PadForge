using System;
using System.Collections.Generic;

namespace PadForge.Services.Automation
{
    /// <summary>What kind of program holds a connection open.</summary>
    internal enum AutomationClientKind
    {
        /// <summary>A script, in any language that can open a pipe.</summary>
        Script,

        /// <summary>An assistant, through the copy of PadForge.exe it
        /// started.</summary>
        Assistant,

        /// <summary>A plugin PadForge started.</summary>
        Plugin,
    }

    /// <summary>A program connected over the long-lived pipe.</summary>
    /// <param name="Id">PadForge's own number for the connection. It is not
    /// used again in the session.</param>
    /// <param name="Name">What the program calls itself.</param>
    /// <param name="Kind">What kind of program it says it is.</param>
    /// <param name="ProcessId">The process at the other end as Windows names
    /// it, or 0.</param>
    /// <param name="ImageName">The file name of that process's image, or
    /// null.</param>
    /// <param name="ConnectedSince">When the connection opened.</param>
    internal sealed record AutomationClient(
        int Id, string Name, AutomationClientKind Kind, int ProcessId, string ImageName, DateTimeOffset ConnectedSince);

    /// <summary>
    /// The programs connected over the long-lived pipe (#597), in the order
    /// they connected. Any thread may change and read it.
    /// </summary>
    internal sealed class AutomationClients
    {
        private readonly object _sync = new object();
        private readonly List<AutomationClient> _clients = new List<AutomationClient>();
        private readonly Func<DateTimeOffset> _now;
        private int _lastId;

        /// <param name="now">The clock a connection is stamped by.</param>
        public AutomationClients(Func<DateTimeOffset> now = null)
        {
            _now = now ?? (() => DateTimeOffset.Now);
        }

        /// <summary>Raised after a program joins or leaves, on the thread
        /// that made the change. A listener that draws goes to its own
        /// thread.</summary>
        public event EventHandler Changed;

        /// <summary>How many programs are connected.</summary>
        public int Count
        {
            get { lock (_sync) return _clients.Count; }
        }

        /// <summary>Lists a program that connected and gives it its
        /// number.</summary>
        public AutomationClient Add(string name, AutomationClientKind kind, int processId, string imageName)
        {
            AutomationClient client;
            lock (_sync)
            {
                client = new AutomationClient(++_lastId, name, kind, processId, imageName, _now());
                _clients.Add(client);
            }
            Changed?.Invoke(this, EventArgs.Empty);
            return client;
        }

        /// <summary>Takes a program off the list. False when it was not on
        /// it.</summary>
        public bool Remove(int id)
        {
            lock (_sync)
            {
                int at = _clients.FindIndex(c => c.Id == id);
                if (at < 0) return false;
                _clients.RemoveAt(at);
            }
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        /// <summary>The programs connected now, as a list of the caller's
        /// own.</summary>
        public IReadOnlyList<AutomationClient> Snapshot()
        {
            lock (_sync) return _clients.ToArray();
        }
    }
}
