using System;
using System.Collections.Generic;
using PadForge.Engine.RemoteLink;

namespace PadForge.Tests
{
    public sealed class OwnedLinkCallbacksTests
    {
        [Fact]
        public void CanceledControlOwnerCannotRemoveItsReplacementOrAnotherChannel()
        {
            var mux = new OwnedLinkCallbacks<uint, Action<byte[]>>();
            var delivered = new List<string>();
            var old = mux.Register(17, _ => delivered.Add("old"));
            var current = mux.Register(17, _ => delivered.Add("current"));
            var sibling = mux.Register(18, _ => delivered.Add("sibling"));
            old.Dispose();
            Assert.True(mux.TryGetValue(17, out var receive));
            receive(new byte[1]);
            Assert.True(mux.TryGetValue(18, out receive));
            receive(new byte[1]);
            Assert.Equal(new[] { "current", "sibling" }, delivered);
            current.Dispose();
            Assert.False(mux.TryGetValue(17, out _));
            Assert.True(mux.TryGetValue(18, out _));
            sibling.Dispose();
            Assert.True(mux.IsEmpty);
        }

        [Fact]
        public void RegistrationIdentityIsIndependentOfDelegateEquality()
        {
            var mux = new OwnedLinkCallbacks<string, Action>();
            int calls = 0;
            Action sameCallback = () => calls++;
            var old = mux.Register("identity", sameCallback);
            var current = mux.Register("identity", sameCallback);
            old.Dispose();
            old.Dispose();
            Assert.True(mux.TryGetValue("identity", out var receive));
            receive();
            Assert.Equal(1, calls);
            current.Dispose();
            Assert.True(mux.IsEmpty);
        }

        [Fact]
        public void RelayCleanupKeepsTheNewSourceAwareHandler()
        {
            var mux = new OwnedLinkCallbacks<uint, Action<byte[], byte[]>>();
            int oldCalls = 0, currentCalls = 0;
            var old = mux.Register(7, (_, _) => oldCalls++);
            var current = mux.Register(7, (peer, bytes) =>
            {
                Assert.Equal(new byte[] { 2 }, peer);
                Assert.Equal(new byte[] { 3 }, bytes);
                currentCalls++;
            });
            old.Dispose();
            Assert.True(mux.TryGetValue(7, out var receive));
            receive(new byte[] { 2 }, new byte[] { 3 });
            Assert.Equal(0, oldCalls);
            Assert.Equal(1, currentCalls);
            current.Dispose();
            Assert.True(mux.IsEmpty);
        }
    }
}
