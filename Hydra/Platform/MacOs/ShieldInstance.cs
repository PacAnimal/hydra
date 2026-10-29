using System.Diagnostics;

namespace Hydra.Platform.MacOs;

// one running shield: its pipes, and the means to end it; a seam so tests can run a fake one, the only way to make a
// shield die, stall or ack on cue
internal abstract class ShieldInstance : IDisposable
{
    // the shield's stdout, which Hydra reads
    public abstract Stream Output { get; }

    // the shield's stdin, which Hydra writes
    public abstract Stream Input { get; }

    // the shield's stderr, null when there is none
    public abstract TextReader? Errors { get; }

    // ends the shield, if it is still running, waiting a moment for it to go
    public abstract void Kill();

    public abstract void Dispose();

    // the real shield, a child process
    internal sealed class OfProcess(Process process) : ShieldInstance
    {
        public override Stream Output => process.StandardOutput.BaseStream;
        public override Stream Input => process.StandardInput.BaseStream;
        public override TextReader Errors => process.StandardError;

        public override void Kill()
        {
            try
            {
                if (process.HasExited) return;
                process.Kill();
                process.WaitForExit(2000);
            }
            catch (Exception) { /* already dead or not associated */ }
        }

        public override void Dispose()
        {
            try
            {
                process.StandardInput.Dispose();
            }
            catch (Exception) { /* the pipe is already gone */ }
            process.Dispose();
        }
    }
}
