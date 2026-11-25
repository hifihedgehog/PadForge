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

        /// <summary>A web assistant, through the web helper PadForge
        /// started (#600 item 11), named by the address its sign-in
        /// returned to.</summary>
        Web,
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
    /// <param name="ClientName">The name its hello gave, alone: for an
    /// assistant, its MCP client's own name, such as claude-code, without the
    /// host <paramref name="Name"/> adds. Null when the hello gave none.</param>
    /// <param name="HostPath">The full path of the program behind the
    /// connection: for an assistant, the program that started its copy of
    /// PadForge.exe, else the connected process's image. Null when Windows
    /// did not say. Two programs can share an image's name, so a program is
    /// matched by this path.</param>
    internal sealed record AutomationClient(
        int Id, string Name, AutomationClientKind Kind, int ProcessId, string ImageName, DateTimeOffset ConnectedSince,
        string ClientName = null, string HostPath = null);

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
        public AutomationClient Add(string name, AutomationClientKind kind, int processId, string imageName,
            string clientName = null, string hostPath = null)
        {
            AutomationClient client;
            lock (_sync)
            {
                client = new AutomationClient(++_lastId, name, kind, processId, imageName, _now(), clientName, hostPath);
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
