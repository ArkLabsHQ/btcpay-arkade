namespace BTCPayServer.Plugins.ArkPayServer.Exceptions;

public class ExternalProcessFailedException(string command, string msg)
    : Exception($"External process '{command}' failed: {msg}");