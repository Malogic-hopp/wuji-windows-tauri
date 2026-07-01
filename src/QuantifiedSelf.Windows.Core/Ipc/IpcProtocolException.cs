namespace QuantifiedSelf.Windows.Core.Ipc;

public sealed class IpcProtocolException : Exception
{
    public string ErrorCode { get; }

    public IpcProtocolException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public IpcProtocolException(string errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}
