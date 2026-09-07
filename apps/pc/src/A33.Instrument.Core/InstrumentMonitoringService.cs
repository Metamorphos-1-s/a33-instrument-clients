using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using A33.Instrument.Protocol;

namespace A33.Instrument.Core;

public sealed class InstrumentMonitoringService(IModbusTransportFactory? transportFactory = null) : IAsyncDisposable, IReadOnlyRegisterAccess
{
    private readonly IModbusTransportFactory factory = transportFactory ?? new ModbusTransportFactory();
    private readonly object traceSync=new();private readonly List<ModbusOperationTrace> operationTrace=[];
    private readonly SemaphoreSlim lifecycle = new(1,1);private readonly SemaphoreSlim commandGate=new(1,1);private IModbusTransport? transport;private ReadOnlyModbusClient? client;private MonitoringOptions? options;private CancellationTokenSource? monitorCancellation;private Task? monitorTask;private long generation;private bool commandInProgress;
    public MonitoringConnectionState State {get;private set;}=MonitoringConnectionState.Disconnected;public InstrumentSnapshot? Snapshot{get;private set;}public CommunicationDiagnostics Diagnostics{get;}=new();public WordOrder WordOrder{get;private set;}=WordOrder.HighWordFirst;public ushort MapVersion{get;private set;}public string Endpoint=>transport?.Endpoint??"Not connected";public MonitoringOptions? CurrentOptions=>options;public bool IsStale=>Snapshot is null||DateTimeOffset.Now-Snapshot.CapturedAt>TimeSpan.FromSeconds(3);public IReadOnlyList<ModbusOperationTrace> OperationTrace{get{lock(traceSync)return operationTrace.ToArray();}}public event EventHandler? Updated;
    private void Observe(ModbusOperationTrace item){lock(traceSync)operationTrace.Add(item);}
    private void SetState(MonitoringConnectionState state){State=state;Updated?.Invoke(this,EventArgs.Empty);}

    public async Task ConnectAsync(MonitoringOptions connectionOptions,CancellationToken cancellationToken=default)
    {
        await lifecycle.WaitAsync(cancellationToken);try{if(State!=MonitoringConnectionState.Disconnected&&State!=MonitoringConnectionState.Faulted)return;options=Normalize(connectionOptions);lock(traceSync)operationTrace.Clear();var currentGeneration=++generation;SetState(MonitoringConnectionState.Connecting);transport=factory.Create(options);using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);timeout.CancelAfter(options.ConnectTimeoutMs);await transport.OpenAsync(timeout.Token);client=new ReadOnlyModbusClient(transport,options.UnitId,TimeSpan.FromMilliseconds(options.RequestTimeoutMs),Observe);await VerifyContractAsync(currentGeneration,timeout.Token);SetState(MonitoringConnectionState.Connected);}catch(Exception error){if(!(error is OperationCanceledException&&cancellationToken.IsCancellationRequested))Diagnostics.Error(error);if(transport is not null)await transport.CloseAsync();SetState(error is OperationCanceledException&&cancellationToken.IsCancellationRequested?MonitoringConnectionState.Disconnected:MonitoringConnectionState.Faulted);throw;}finally{lifecycle.Release();}
    }

    public Task StartMonitoringAsync(CancellationToken cancellationToken=default)
    {
        if(State!=MonitoringConnectionState.Connected)throw new InvalidOperationException("Connect and pass the map-version gate before monitoring.");if(monitorTask is {IsCompleted:false})return Task.CompletedTask;monitorCancellation=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);var currentGeneration=generation;SetState(MonitoringConnectionState.Monitoring);monitorTask=MonitorLoopAsync(currentGeneration,monitorCancellation.Token);return Task.CompletedTask;
    }
    public async Task StopMonitoringAsync(){monitorCancellation?.Cancel();if(monitorTask is not null)try{await monitorTask;}catch(OperationCanceledException){}monitorCancellation?.Dispose();monitorCancellation=null;monitorTask=null;if(State is MonitoringConnectionState.Monitoring or MonitoringConnectionState.Degraded or MonitoringConnectionState.Reconnecting)SetState(MonitoringConnectionState.Connected);}
    public async Task DisconnectAsync(){await lifecycle.WaitAsync();try{SetState(MonitoringConnectionState.Disconnecting);generation++;monitorCancellation?.Cancel();if(monitorTask is not null)try{await monitorTask;}catch(OperationCanceledException){}if(transport is not null){await transport.CloseAsync();await transport.DisposeAsync();}transport=null;client=null;monitorTask=null;monitorCancellation?.Dispose();monitorCancellation=null;SetState(MonitoringConnectionState.Disconnected);}finally{lifecycle.Release();}}

    private async Task VerifyContractAsync(long expectedGeneration,CancellationToken token)
    {
        if(expectedGeneration!=generation)throw new OperationCanceledException();var map=RegisterMap.Get("register_map_version");MapVersion=(await ReadAsync(map.Address,map.RegisterCount,token))[0];if(MapVersion!=RegisterMap.Version)throw new InvalidOperationException($"Incompatible register map 0x{MapVersion:X4}; expected 0x{RegisterMap.Version:X4}.");var order=RegisterMap.Get("active_word_order");WordOrder=(await ReadAsync(order.Address,order.RegisterCount,token))[0] switch{0=>WordOrder.HighWordFirst,1=>WordOrder.LowWordFirst,_=>throw new InvalidOperationException("Unsupported Modbus word order.")};
    }

    private async Task MonitorLoopAsync(long expectedGeneration,CancellationToken token)
    {
        var clock=Stopwatch.StartNew();long nextFast=0,nextSlow=0;
        while(!token.IsCancellationRequested&&expectedGeneration==generation)
        {
            try{if(commandInProgress){await Task.Delay(10,token);continue;}var now=clock.ElapsedMilliseconds;if(now>=nextFast){await PollRealtimeAsync(token);nextFast=now+options!.PollIntervalMs;}if(now>=nextSlow){await PollSlowAndCheckweighAsync(token);nextSlow=now+1000;}if(State==MonitoringConnectionState.Degraded)SetState(MonitoringConnectionState.Monitoring);var delay=Math.Max(10,Math.Min(nextFast,nextSlow)-clock.ElapsedMilliseconds);await Task.Delay(TimeSpan.FromMilliseconds(delay),token);}
            catch(OperationCanceledException)when(token.IsCancellationRequested){break;}
            catch(OperationCanceledException error){Diagnostics.Error(error);SetState(MonitoringConnectionState.Degraded);await Task.Delay(50,token);}
            catch(Exception error)when(error is IOException or SocketException or InvalidOperationException){Diagnostics.Error(error);if(!await TryReconnectAsync(expectedGeneration,token))break;}
            catch(Exception error){Diagnostics.Error(error);SetState(MonitoringConnectionState.Degraded);await Task.Delay(50,token);}
        }
    }

    private async Task PollRealtimeAsync(CancellationToken token)
    {
        var first=RegisterMap.Get("display_weight");var last=RegisterMap.Get("sample_sequence");var count=(ushort)(last.Address+last.RegisterCount-first.Address);var values=await ReadAsync(first.Address,count,token);Snapshot=InstrumentRegisterDecoder.DecodeRealtime(first.Address,values,WordOrder,Snapshot);Updated?.Invoke(this,EventArgs.Empty);
    }
    private async Task PollSlowAndCheckweighAsync(CancellationToken token)
    {
        if(Snapshot is null)return;var dirty=RegisterMap.Get("config_dirty");var fault=RegisterMap.Get("fault_mask");var display=RegisterMap.Get("display_condition_state");var displayMass=RegisterMap.Get("conditioned_display_mass_ug");var alarmFirst=RegisterMap.Get("alarm_limit_enable");var alarmLast=RegisterMap.Get("alarm_config_dirty");
        var dirtyValue=(await ReadAsync(dirty.Address,dirty.RegisterCount,token))[0]!=0;var faultValue=RegisterValueCodec.UInt32(await ReadAsync(fault.Address,fault.RegisterCount,token),WordOrder);var displayValues=await ReadAsync(display.Address,(ushort)(displayMass.Address+displayMass.RegisterCount-display.Address),token);var alarmValues=await ReadAsync(alarmFirst.Address,(ushort)(alarmLast.Address+alarmLast.RegisterCount-alarmFirst.Address),token);
        // 0x0000 is the authoritative final panel value. The display-condition
        // block is diagnostic and must not overwrite it with a slower snapshot.
        Snapshot=Snapshot with{DisplayLocked=displayValues[RegisterMap.Get("display_locked").Address-display.Address]!=0,ConfigDirty=dirtyValue,FaultMask=faultValue,CheckweighState=alarmValues[RegisterMap.Get("checkweigh_state").Address-alarmFirst.Address]};Updated?.Invoke(this,EventArgs.Empty);
    }
    private async Task<ushort[]> ReadAsync(ushort address,ushort count,CancellationToken token){if(client is null)throw new InvalidOperationException("Client is not connected.");Diagnostics.RequestStarted();var result=await client.ReadHoldingAsync(address,count,token);if(client.LastExchange is not null)Diagnostics.ExchangeCompleted(client.LastExchange);return result;}
    public Task<ushort[]> ReadHoldingAsync(ushort address, ushort count, CancellationToken cancellationToken = default) =>
        WithCommandExclusiveAsync(_ => ReadAsync(address, count, cancellationToken), cancellationToken);
    internal void ReplaceSnapshot(InstrumentSnapshot snapshot){Snapshot=snapshot;Updated?.Invoke(this,EventArgs.Empty);}
    internal async Task<T> WithCommandExclusiveAsync<T>(Func<ReadOnlyModbusClient,Task<T>> operation,CancellationToken token=default){if(State!=MonitoringConnectionState.Monitoring||Snapshot is null||IsStale||MapVersion!=RegisterMap.Version)throw new InvalidOperationException("Runtime operation requires a fresh compatible monitoring connection.");await commandGate.WaitAsync(token);commandInProgress=true;try{if(client is null)throw new InvalidOperationException("Client is not connected.");return await operation(client);}finally{commandInProgress=false;commandGate.Release();}}

    private async Task<bool> TryReconnectAsync(long expectedGeneration,CancellationToken token)
    {
        if(options is null)return false;for(var attempt=1;attempt<=options.AutoReconnectAttempts&&expectedGeneration==generation&&!token.IsCancellationRequested;attempt++){SetState(MonitoringConnectionState.Reconnecting);Interlocked.Increment(ref Diagnostics.Reconnects);try{if(transport is not null){await transport.CloseAsync();await transport.DisposeAsync();}await Task.Delay(TimeSpan.FromMilliseconds(attempt*500),token);transport=factory.Create(options);using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(options.ConnectTimeoutMs);await transport.OpenAsync(timeout.Token);client=new ReadOnlyModbusClient(transport,options.UnitId,TimeSpan.FromMilliseconds(options.RequestTimeoutMs),Observe);await VerifyContractAsync(expectedGeneration,timeout.Token);SetState(MonitoringConnectionState.Monitoring);return true;}catch(OperationCanceledException)when(token.IsCancellationRequested){return false;}catch(Exception error){Diagnostics.Error(error);}}
        if(token.IsCancellationRequested||expectedGeneration!=generation)return false;SetState(MonitoringConnectionState.Faulted);return false;
    }
    private static MonitoringOptions Normalize(MonitoringOptions value)=>value with{Port=Math.Clamp(value.Port,1,65535),UnitId=(byte)Math.Clamp((int)value.UnitId,1,247),ConnectTimeoutMs=Math.Clamp(value.ConnectTimeoutMs,250,30000),RequestTimeoutMs=Math.Clamp(value.RequestTimeoutMs,100,30000),PollIntervalMs=Math.Clamp(value.PollIntervalMs,100,5000),AutoReconnectAttempts=Math.Clamp(value.AutoReconnectAttempts,0,3)};
    public async ValueTask DisposeAsync(){await DisconnectAsync();lifecycle.Dispose();}
}
