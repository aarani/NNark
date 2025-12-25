namespace NArk;

public class AlreadyLockedVtxoException : Exception;

public class UnableToSignUnknownContracts(string msg) : Exception(msg);

public class AdditionalInformationRequiredException(string msg) : Exception(msg);