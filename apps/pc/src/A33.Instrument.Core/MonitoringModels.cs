using A33.Instrument.Protocol;
using System.IO.Ports;

namespace A33.Instrument.Core;

public enum MonitoringConnectionState { Disconnected, Connecting, Connected, Monitoring, Degraded, Reconnecting, Faulted, Disconnecting }
public enum TransportMode { Tcp, Rtu }
public sealed record MonitoringOptions(TransportMode Mode,string Host="192.168.1.100",int Port=502,string SerialPort="",int BaudRate=115200,byte UnitId=1,int ConnectTimeoutMs=3000,int RequestTimeoutMs=1000,int PollIntervalMs=200,int DataBits=8,Parity Parity=Parity.None,StopBits StopBits=StopBits.One,int AutoReconnectAttempts=3);
public sealed record InstrumentSnapshot(long DisplayMassUg,long NetMassUg,long GrossMassUg,long TareMassUg,byte Unit,byte DecimalPlaces,byte Division,bool Stable,bool Zero,bool TareActive,bool Overload,int Raw,int FilteredRaw,uint Sequence,ushort CheckweighState,bool DisplayLocked,bool ConfigDirty,uint FaultMask,DateTimeOffset CapturedAt);

public sealed class CommunicationDiagnostics
{
    private readonly object sync=new();private readonly Queue<string> entries=new();
    public long TxRequests;public long RxResponses;public long Timeouts;public long CrcErrors;public long MbapErrors;public long UnitErrors;public long Exceptions;public long BadFrames;public long TransportErrors;public long Reconnects;
    public string LastRequestHex {get;private set;}="";public string LastResponseHex {get;private set;}="";public DateTimeOffset? LastSuccessAt{get;private set;}public string LastError{get;private set;}="";
    public IReadOnlyList<string> Entries{get{lock(sync)return entries.ToArray();}}
    public void RequestStarted()=>Interlocked.Increment(ref TxRequests);
    public void ExchangeCompleted(ModbusExchangeResult exchange){LastRequestHex=BoundedHex(exchange.RequestAdu);Interlocked.Increment(ref RxResponses);LastResponseHex=BoundedHex(exchange.ResponseAdu);LastSuccessAt=DateTimeOffset.Now;}
    public void Error(Exception error){LastError=error.Message;if(error is OperationCanceledException)Interlocked.Increment(ref Timeouts);else if(IsTransportError(error))Interlocked.Increment(ref TransportErrors);else if(error is ModbusExceptionResponse)Interlocked.Increment(ref Exceptions);else if(error is ModbusFrameException frame){if(frame.Error==ModbusFrameError.Crc)Interlocked.Increment(ref CrcErrors);else if(frame.Error is ModbusFrameError.TransactionId or ModbusFrameError.ProtocolId)Interlocked.Increment(ref MbapErrors);else if(frame.Error==ModbusFrameError.UnitId)Interlocked.Increment(ref UnitErrors);else Interlocked.Increment(ref BadFrames);}else Interlocked.Increment(ref BadFrames);Add($"{DateTimeOffset.Now:O} {error.GetType().Name}: {error.Message}");}
    public void Add(string text){lock(sync){entries.Enqueue(text);while(entries.Count>200)entries.Dequeue();}}
    public void Clear(){lock(sync)entries.Clear();TxRequests=RxResponses=Timeouts=CrcErrors=MbapErrors=UnitErrors=Exceptions=BadFrames=TransportErrors=Reconnects=0;LastRequestHex=LastResponseHex=LastError="";LastSuccessAt=null;}
    public string Summary()=>System.Text.Json.JsonSerializer.Serialize(new{TxRequests,RxResponses,Timeouts,CrcErrors,MbapErrors,UnitErrors,Exceptions,BadFrames,TransportErrors,Reconnects,LastRequestHex,LastResponseHex,LastSuccessAt,LastError},new System.Text.Json.JsonSerializerOptions{WriteIndented=true});
    private static string BoundedHex(byte[] bytes)=>Convert.ToHexString(bytes.AsSpan(0,Math.Min(bytes.Length,128)));
    private static bool IsTransportError(Exception error)=>error is IOException or UnauthorizedAccessException or ObjectDisposedException or System.Net.Sockets.SocketException||(error is InvalidOperationException&&error.Message.Contains("closed",StringComparison.OrdinalIgnoreCase));
}

public interface IModbusTransportFactory { IModbusTransport Create(MonitoringOptions options); }
public sealed class ModbusTransportFactory : IModbusTransportFactory
{
    public IModbusTransport Create(MonitoringOptions options)=>options.Mode==TransportMode.Tcp?new ModbusTcpTransport(options.Host,options.Port):new ModbusRtuTransport(options.SerialPort,options.BaudRate,options.DataBits,options.Parity,options.StopBits);
}
