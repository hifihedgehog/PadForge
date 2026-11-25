using System;
using System.IO;
using System.Text;

namespace PadForge.Engine.Recording
{
    /// <summary>
    /// A reader of a recording that hands out lines no longer than one limit
    /// and no more text in all than another, reading a character at a time
    /// so an endless line is never held whole (#600). A recording waits in a
    /// folder the user can write, so the review reads it as a file from
    /// anyone. Past a limit a read throws <see cref="InvalidDataException"/>.
    /// The replay's own reader (ReplayFile.OpenBounded) keeps the same
    /// limits.
    /// </summary>
    public sealed class BoundedLineReader : TextReader
    {
        /// <summary>The recorder stops itself at a 20 MB zip with about 120
        /// MB of text inside it, so a file it made is always within these.</summary>
        public const long MostTextChars = 512L * 1024 * 1024;
        public const int LongestLine = 64 * 1024;

        private readonly TextReader _inner;
        private readonly int _longest;
        private readonly long _most;
        private long _read;
        private readonly StringBuilder _line = new();

        public BoundedLineReader(TextReader inner, int longest = LongestLine, long most = MostTextChars)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _longest = longest;
            _most = most;
        }

        public override int Peek() => _inner.Peek();

        public override int Read()
        {
            int c = _inner.Read();
            if (c >= 0 && ++_read > _most) throw new InvalidDataException("too-large");
            return c;
        }

        public override string ReadLine()
        {
            _line.Clear();
            while (true)
            {
                int c = Read();
                if (c < 0) return _line.Length == 0 ? null : _line.ToString();
                if (c == '\n') return _line.ToString();
                if (c == '\r')
                {
                    if (_inner.Peek() == '\n') Read();
                    return _line.ToString();
                }
                if (_line.Length >= _longest) throw new InvalidDataException("line-too-long");
                _line.Append((char)c);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
