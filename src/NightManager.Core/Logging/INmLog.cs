namespace NightManager.Core.Logging;

/// <summary>Logging seam so Core stays host-independent; the NINA adapter forwards to NINA's logger.</summary>
public interface INmLog {
    void Debug(string message);

    void Info(string message);

    void Warning(string message);

    void Error(string message, Exception? exception = null);
}

public sealed class NullLog : INmLog {
    public static readonly NullLog Instance = new();

    public void Debug(string message) {
    }

    public void Info(string message) {
    }

    public void Warning(string message) {
    }

    public void Error(string message, Exception? exception = null) {
    }
}
