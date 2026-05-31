namespace BTCPayServer.Plugins.ArkPayServer.Exceptions;

public class MalformedPaymentDestination : Exception
{
    public MalformedPaymentDestination() : base("Destination is malformed or unsupported.") { }
    public MalformedPaymentDestination(string message) : base(message) { }
}

public class IncompleteArkadeSetupException(string msg): Exception(msg);

public class ArkadePaymentFailedException(string failureMessage): Exception(failureMessage);