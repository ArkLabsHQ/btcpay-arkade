namespace BTCPayServer.Plugins.Arkade.Exceptions;

public class MalformedPaymentDestination : Exception;

public class IncompleteArkadeSetupException(string msg): Exception(msg);

public class ArkadePaymentFailedException(string failureMessage): Exception(failureMessage);