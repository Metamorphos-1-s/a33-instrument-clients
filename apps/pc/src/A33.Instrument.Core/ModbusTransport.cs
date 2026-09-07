using A33.Instrument.Protocol;

namespace A33.Instrument.Core;

public sealed record ModbusExchangeResult(ReadOnlyMemory<byte> Pdu, byte[] RequestAdu, byte[] ResponseAdu);
public sealed record ModbusOperationTrace(DateTimeOffset StartedAtUtc,DateTimeOffset CompletedAtUtc,byte FunctionCode,ushort Address,ushort Quantity,ushort? MailboxCommandId,bool Succeeded,string RequestHex,string ResponseHex,string? ErrorCategory,byte? ModbusExceptionCode,string? Error);

public interface IModbusTransport : IAsyncDisposable
{
    bool IsOpen { get; }
    string Endpoint { get; }
    Task OpenAsync(CancellationToken cancellationToken);
    Task CloseAsync();
    Task<ModbusExchangeResult> ExchangeAsync(byte unitId, ReadOnlyMemory<byte> pdu, CancellationToken cancellationToken);
}

public sealed class ReadOnlyModbusClient(IModbusTransport transport, byte unitId, TimeSpan requestTimeout, Action<ModbusOperationTrace>? observer = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public ModbusExchangeResult? LastExchange { get; private set; }

    public async Task<ushort[]> ReadHoldingAsync(ushort address, ushort count, CancellationToken cancellationToken = default)
    {
        var started=DateTimeOffset.UtcNow;var pdu=ModbusRequestCodec.ReadHoldingPdu(address,count);
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(requestTimeout);
            LastExchange = await transport.ExchangeAsync(unitId, pdu, timeout.Token);
            var values = ModbusResponseCodec.ReadHolding(LastExchange.Pdu.Span);
            if (values.Length != count) throw new ModbusFrameException(ModbusFrameError.ByteCount, $"Expected {count} registers, received {values.Length}.");
            observer?.Invoke(new(started,DateTimeOffset.UtcNow,3,address,count,null,true,Convert.ToHexString(LastExchange.RequestAdu),Convert.ToHexString(LastExchange.ResponseAdu),null,null,null));
            return values;
        }
        catch(Exception error){var classified=Classify(error);observer?.Invoke(new(started,DateTimeOffset.UtcNow,3,address,count,null,false,Convert.ToHexString(pdu),"",classified.Category,classified.ExceptionCode,error.Message));throw;}
        finally { gate.Release(); }
    }

    public async Task<ModbusExchangeResult> WriteMultipleAsync(ushort address, ReadOnlyMemory<ushort> values, CancellationToken cancellationToken = default)
    {
        var started=DateTimeOffset.UtcNow;var pdu=ModbusRequestCodec.WriteMultiple(unitId,address,values.Span).AsSpan(1).ToArray();var command=address==0x0040&&values.Length>1?values.Span[1]:(ushort?)null;
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);timeout.CancelAfter(requestTimeout);
            LastExchange=await transport.ExchangeAsync(unitId,pdu,timeout.Token);var echo=ModbusResponseCodec.ValidateWriteMultiple(LastExchange.Pdu.Span);if(echo.Address!=address||echo.Quantity!=values.Length)throw new ModbusFrameException(ModbusFrameError.Malformed,"FC16 echo mismatch.");observer?.Invoke(new(started,DateTimeOffset.UtcNow,16,address,(ushort)values.Length,command,true,Convert.ToHexString(LastExchange.RequestAdu),Convert.ToHexString(LastExchange.ResponseAdu),null,null,null));return LastExchange;
        }
        catch(Exception error){var classified=Classify(error);observer?.Invoke(new(started,DateTimeOffset.UtcNow,16,address,(ushort)values.Length,command,false,Convert.ToHexString(pdu),"",classified.Category,classified.ExceptionCode,error.Message));throw;}
        finally{gate.Release();}
    }
    private static (string Category,byte? ExceptionCode) Classify(Exception error)=>error switch
    {
        OperationCanceledException=>("Timeout",null),
        ModbusExceptionResponse response=>("ModbusException",response.ExceptionCode),
        ModbusFrameException frame when frame.Error==ModbusFrameError.Crc=>("CRC",null),
        ModbusFrameException frame when frame.Error==ModbusFrameError.TransactionId=>("TID",null),
        ModbusFrameException frame when frame.Error==ModbusFrameError.ProtocolId=>("MBAP",null),
        ModbusFrameException frame when frame.Error==ModbusFrameError.UnitId=>("Unit",null),
        IOException or System.Net.Sockets.SocketException=>("Transport",null),
        _=>("BadFrame",null)
    };
}
