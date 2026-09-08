using System;
using System.Collections.Generic;
using System.Text;
using PadForge.Services;

namespace PadForge.Tests
{
    public class PsmPatchSnapshotTests
    {
        [Fact]
        public void CompleteCensusIncludesEveryRadioAndItsLiveState()
        {
            var queried = new List<int>();
            var snapshot = PsmPatchSnapshot.Read(buffer =>
            {
                int index = BitConverter.ToInt32(buffer);
                queried.Add(index);
                if (index == 2) return new(false, 0, 433);
                BitConverter.GetBytes(index == 0 ? 1u : 0u).CopyTo(buffer, 4);
                Encoding.Unicode.GetBytes("radio-" + index).CopyTo(buffer, 8);
                return new(true, 408, 0);
            });

            Assert.True(snapshot.Complete);
            Assert.Equal(new[] { 0, 1, 2 }, queried);
            Assert.Equal(1, snapshot.ArmedCount);
            Assert.Collection(snapshot.Radios,
                radio => Assert.Equal(new PsmRadioState(0, true, "radio-0"), radio),
                radio => Assert.Equal(new PsmRadioState(1, false, "radio-1"), radio));
        }

        [Fact]
        public void FailureAfterAnOffRadioIsAnIncompleteCensus()
        {
            var snapshot = PsmPatchSnapshot.Read(buffer =>
                BitConverter.ToInt32(buffer) == 0 ? new(true, 408, 0) : new(false, 0, 5));

            Assert.False(snapshot.Complete);
            Assert.Equal(5, snapshot.Error);
            Assert.Single(snapshot.Radios);
            Assert.Equal(0, snapshot.ArmedCount);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(8)]
        [InlineData(407)]
        [InlineData(409)]
        public void AShortOrInvalidReplyDoesNotProveTheRadioIsOff(int bytesReturned)
        {
            var snapshot = PsmPatchSnapshot.Read(_ => new(true, bytesReturned, 0));
            Assert.False(snapshot.Complete);
            Assert.Equal(13, snapshot.Error);
            Assert.Empty(snapshot.Radios);
        }

        [Fact]
        public void AReplyForAnotherIndexDoesNotProveTheRequestedRadioIsOff()
        {
            var snapshot = PsmPatchSnapshot.Read(buffer =>
            {
                BitConverter.GetBytes(9).CopyTo(buffer, 0);
                return new(true, 408, 0);
            });
            Assert.False(snapshot.Complete);
            Assert.Empty(snapshot.Radios);
        }

        [Fact]
        public void MissingNamesDoNotDiscardOtherwiseValidRadioState()
        {
            var snapshot = PsmPatchSnapshot.Read(buffer =>
                BitConverter.ToInt32(buffer) == 0 ? new(true, 408, 0) : new(false, 0, 433));
            Assert.True(snapshot.Complete);
            Assert.Equal(string.Empty, Assert.Single(snapshot.Radios).Name);
        }

        [Fact]
        public void EmptyCollectionRequiresTheExplicitEndStatus()
        {
            Assert.True(PsmPatchSnapshot.Read(_ => new(false, 0, 433)).Complete);
            Assert.False(PsmPatchSnapshot.Read(_ => new(false, 0, 5)).Complete);
            Assert.False(PsmPatchSnapshot.Unavailable(2).Complete);
            Assert.True(PsmPatchSnapshot.Read(_ => new(false, 0, 5)).ControlPresent);
            Assert.False(PsmPatchSnapshot.Unavailable(2).ControlPresent);
        }

        [Fact]
        public void HittingTheBoundWithoutAnEndStatusIsNotACompleteCensus()
        {
            int queries = 0;
            var snapshot = PsmPatchSnapshot.Read(_ =>
            {
                queries++;
                return new(true, 408, 0);
            });
            Assert.False(snapshot.Complete);
            Assert.Equal(PsmPatchSnapshot.MaximumRadios + 1, queries);
            Assert.Equal(PsmPatchSnapshot.MaximumRadios, snapshot.Radios.Count);
        }

        [Fact]
        public void TheBoundedCollectionCanStillEndNormally()
        {
            var snapshot = PsmPatchSnapshot.Read(buffer =>
                BitConverter.ToInt32(buffer) == PsmPatchSnapshot.MaximumRadios
                    ? new(false, 0, 433) : new(true, 408, 0));
            Assert.True(snapshot.Complete);
            Assert.Equal(PsmPatchSnapshot.MaximumRadios, snapshot.Radios.Count);
        }
    }
}
