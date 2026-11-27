using System;
using System.Collections.Generic;
using PadForge.Services;
using PadForge.Services.Plugins;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The DSX port files are the user's: C:\Temp\DualSenseX, which any
    /// signed-in program may replace with a link, and the user's own
    /// %LOCALAPPDATA%\DSX. PadForge writes them in its own process. Every
    /// way out is a seam here, so nothing on the PC is written or started.
    /// </summary>
    public class GameModPortFilesAsUserTests
    {
        [Fact]
        public void TheFilesAreWrittenHere()
        {
            int here = 0;
            GameModPortFiles.Write(7000, new GameModPortFiles.Seams
            {
                RealDsxRunning = () => false,
                WriteHere = _ => here++,
            });
            Assert.Equal(1, here);
        }
    }
}
