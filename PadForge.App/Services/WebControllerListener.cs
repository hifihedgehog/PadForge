using System.Net;

namespace PadForge.Services
{
    internal interface IWebControllerListener
    {
        bool IsListening { get; }
        void Start(string prefix);
        HttpListenerContext GetContext();
        void Stop();
        void Close();
    }

    internal sealed class HttpWebControllerListener : IWebControllerListener
    {
        private readonly HttpListener _listener = new();
        public bool IsListening => _listener.IsListening;
        public void Start(string prefix)
        {
            _listener.Prefixes.Add(prefix);
            _listener.Start();
        }
        public HttpListenerContext GetContext() => _listener.GetContext();
        public void Stop() => _listener.Stop();
        public void Close() => _listener.Close();
    }
}
