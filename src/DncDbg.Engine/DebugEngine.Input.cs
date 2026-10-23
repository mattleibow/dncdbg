using System.Text;

namespace DncDbg.Engine;

public sealed partial class DebugEngine
{
    public bool WriteInputIfRunning(string text)
    {
        lock (_lock)
        {
            if (_stopped || _processExited || _launched == null)
                return false;
            byte[] bytes = Encoding.UTF8.GetBytes(text + Environment.NewLine);
            _launched.StdIn.Write(bytes);
            _launched.StdIn.Flush();
            return true;
        }
    }
}
