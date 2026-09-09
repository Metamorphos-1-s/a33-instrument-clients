using A33.Instrument.Core;
using A33.Instrument.Protocol;
using Xunit;

namespace A33.Instrument.Protocol.Tests;

public sealed class StrictMonitoringTests
{
    [Fact]
    public async Task AsyncFirstFrameRequiresExplicitFreshSnapshotBarrier()
    {
        var transport=new AsyncTransport();await using var monitoring=Service(transport);
        await monitoring.ConnectAsync(Options());await monitoring.StartMonitoringAsync();
        Assert.Null(monitoring.Snapshot);
        var ready=monitoring.WaitForFreshSnapshotAsync(TimeSpan.FromSeconds(1));
        Assert.False(ready.IsCompleted);transport.ReleaseFirstRealtime();await ready;
        Assert.NotNull(monitoring.Snapshot);Assert.Null(monitoring.StrictFault);Assert.Equal(0,transport.Fc16Count);
    }

    [Fact]
    public async Task FreshSnapshotTimeoutLatchesAndNeverWrites()
    {
        var transport=new AsyncTransport();await using var monitoring=Service(transport);
        await monitoring.ConnectAsync(Options());await monitoring.StartMonitoringAsync();
        await Assert.ThrowsAsync<TimeoutException>(()=>monitoring.WaitForFreshSnapshotAsync(TimeSpan.FromMilliseconds(20)));
        Assert.Equal("SnapshotTimeout",monitoring.StrictFault?.Category);Assert.Equal(0,transport.Fc16Count);
    }

    [Fact]
    public async Task CancellationAndUnexpectedDisconnectFailBarrierWithoutWrites()
    {
        var transport=new AsyncTransport();await using var monitoring=Service(transport);
        await monitoring.ConnectAsync(Options());await monitoring.StartMonitoringAsync();
        using(var cancellation=new CancellationTokenSource(20))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>monitoring.WaitForFreshSnapshotAsync(TimeSpan.FromSeconds(2),cancellation.Token));
        var waiting=monitoring.WaitForFreshSnapshotAsync(TimeSpan.FromSeconds(2));await monitoring.DisconnectAsync();
        await Assert.ThrowsAnyAsync<Exception>(()=>waiting);Assert.NotNull(monitoring.StrictFault);Assert.Equal(0,transport.Fc16Count);
    }

    [Theory]
    [InlineData("Timeout")][InlineData("CRC")][InlineData("MBAP")][InlineData("TID")]
    [InlineData("Unit")][InlineData("ModbusException")][InlineData("BadFrame")][InlineData("Transport")]
    public async Task FirstFrameProtocolFailureIsLatchedAndCannotRecover(string category)
    {
        var transport=new AsyncTransport{FirstRealtimeError=Error(category)};transport.ReleaseFirstRealtime();
        await using var monitoring=Service(transport);await monitoring.ConnectAsync(Options());await monitoring.StartMonitoringAsync();
        await Assert.ThrowsAnyAsync<Exception>(()=>monitoring.WaitForFreshSnapshotAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(category,monitoring.StrictFault?.Category);Assert.Equal(MonitoringConnectionState.Faulted,monitoring.State);
        Assert.Equal(1,ErrorCount(monitoring.Diagnostics.Snapshot()));Assert.Equal(0,transport.Fc16Count);Assert.Equal(1,transport.CreateGeneration);
    }

    [Fact]
    public async Task SuccessfulFc03DecoderFailureIsLatched()
    {
        var transport=new AsyncTransport{InvalidRealtime=true};transport.ReleaseFirstRealtime();
        await using var monitoring=Service(transport);await monitoring.ConnectAsync(Options());await monitoring.StartMonitoringAsync();
        await Assert.ThrowsAnyAsync<Exception>(()=>monitoring.WaitForFreshSnapshotAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal("Decode",monitoring.StrictFault?.Category);Assert.Contains(monitoring.OperationTrace,x=>x.FunctionCode==3&&x.Succeeded);
        Assert.Equal(1,ErrorCount(monitoring.Diagnostics.Snapshot()));Assert.Equal(0,transport.Fc16Count);
    }

    [Fact]
    public async Task QueuedWriteCannotPassDecodeFailurePublishedUnderGate()
    {
        var transport=new AsyncTransport();transport.ReleaseFirstRealtime();await using var monitoring=Service(transport);
        await monitoring.ConnectAsync(Options());await monitoring.StartMonitoringAsync();await monitoring.WaitForFreshSnapshotAsync(TimeSpan.FromSeconds(1));
        var configuration=new ConfigurationTransactionService(monitoring);await configuration.RefreshAsync();configuration.Edit("brightness",4);
        transport.InvalidRealtime=true;transport.HoldNextRealtime();await transport.WaitUntilHeldAsync();
        var queued=configuration.ValidateAsync();transport.ReleaseHeldRealtime();await Assert.ThrowsAsync<InvalidOperationException>(()=>queued);
        Assert.Equal("Decode",monitoring.StrictFault?.Category);Assert.Equal(1,ErrorCount(monitoring.Diagnostics.Snapshot()));Assert.Equal(0,transport.Fc16Count);
    }

    [Fact]
    public async Task QueuedWriteCannotPassMonitoringEventFailurePublishedUnderGate()
    {
        var transport=new AsyncTransport();transport.ReleaseFirstRealtime();await using var monitoring=Service(transport);
        await monitoring.ConnectAsync(Options());await monitoring.StartMonitoringAsync();await monitoring.WaitForFreshSnapshotAsync(TimeSpan.FromSeconds(1));
        var configuration=new ConfigurationTransactionService(monitoring);await configuration.RefreshAsync();configuration.Edit("brightness",4);
        transport.HoldNextRealtime();monitoring.Updated+=ThrowUpdate;await transport.WaitUntilHeldAsync();var queued=configuration.ValidateAsync();transport.ReleaseHeldRealtime();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>queued);monitoring.Updated-=ThrowUpdate;Assert.Equal("MonitorLoop",monitoring.StrictFault?.Category);Assert.Equal(1,ErrorCount(monitoring.Diagnostics.Snapshot()));Assert.Equal(0,transport.Fc16Count);
        void ThrowUpdate(object? sender,EventArgs args)=>throw new InvalidOperationException("updated handler failed");
    }

    [Fact]
    public async Task StrictStopDrainsInFlightPollBeforeCancellation()
    {
        var transport=new AsyncTransport();transport.ReleaseFirstRealtime();await using var monitoring=Service(transport);
        await monitoring.ConnectAsync(Options());await monitoring.StartMonitoringAsync();await monitoring.WaitForFreshSnapshotAsync(TimeSpan.FromSeconds(1));
        transport.HoldNextRealtime();await transport.WaitUntilHeldAsync();var stopping=monitoring.StopMonitoringAsync();Assert.False(stopping.IsCompleted);
        transport.ReleaseHeldRealtime();await stopping;Assert.Equal(MonitoringConnectionState.Connected,monitoring.State);Assert.Null(monitoring.StrictFault);Assert.DoesNotContain(monitoring.OperationTrace,x=>!x.Succeeded);Assert.Equal(0,ErrorCount(monitoring.Diagnostics.Snapshot()));
    }

    [Fact]
    public async Task LatchedReadFailureRejectsBeginBeforeTransport()
    {
        var transport=new AsyncTransport();transport.ReleaseFirstRealtime();await using var monitoring=Service(transport);
        await monitoring.ConnectAsync(Options());await monitoring.StartMonitoringAsync();await monitoring.WaitForFreshSnapshotAsync(TimeSpan.FromSeconds(1));
        var configuration=new ConfigurationTransactionService(monitoring);await configuration.RefreshAsync();configuration.Edit("brightness",4);
        transport.FailNextRead=new IOException("read path lost");
        await Assert.ThrowsAsync<IOException>(()=>monitoring.ReadHoldingAsync(0x0123,1));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>configuration.ValidateAsync());
        Assert.NotNull(monitoring.StrictFault);Assert.Equal(0,transport.Fc16Count);Assert.False(monitoring.Diagnostics.Snapshot().IsClean);
    }

    [Fact]
    public void ConfigurationWritesAllUseStrictExclusiveEntry()
    {
        var root=FindRepositoryRoot();var source=File.ReadAllText(Path.Combine(root,"apps","pc","src","A33.Instrument.Core","ConfigurationTransactionService.cs"));
        Assert.Equal(2,Count(source,"WithStrictWriteExclusiveAsync"));Assert.DoesNotContain("monitoring.WithCommandExclusiveAsync(c => c.WriteMultipleAsync",source,StringComparison.Ordinal);
        foreach(var command in new[]{9,10,11,12,13})Assert.Contains($"SubmitAsync({command}",source,StringComparison.Ordinal);
    }

    private static InstrumentMonitoringService Service(AsyncTransport transport)=>new(new Factory(transport));
    private static MonitoringOptions Options()=>new(TransportMode.Tcp,PollIntervalMs:100,RequestTimeoutMs:1000,AutoReconnectAttempts:0,StrictSession:true);
    private static Exception Error(string category)=>category switch
    {
        "Timeout"=>new OperationCanceledException("timeout"),"CRC"=>new ModbusFrameException(ModbusFrameError.Crc,"crc"),
        "MBAP"=>new ModbusFrameException(ModbusFrameError.ProtocolId,"mbap"),"TID"=>new ModbusFrameException(ModbusFrameError.TransactionId,"tid"),
        "Unit"=>new ModbusFrameException(ModbusFrameError.UnitId,"unit"),"ModbusException"=>new ModbusExceptionResponse(3,2),
        "BadFrame"=>new ModbusFrameException(ModbusFrameError.Malformed,"bad frame"),_=>new IOException("transport")
    };
    private static int Count(string value,string pattern)=>(value.Length-value.Replace(pattern,"",StringComparison.Ordinal).Length)/pattern.Length;
    private static long ErrorCount(CommunicationDiagnosticsSnapshot d)=>d.Timeouts+d.CrcErrors+d.MbapErrors+d.TidErrors+d.UnitErrors+d.ModbusExceptions+d.BadFrames+d.TransportErrors;
    private static async Task WaitUntilAsync(Func<bool> condition){var end=DateTime.UtcNow.AddSeconds(2);while(!condition()&&DateTime.UtcNow<end)await Task.Delay(5);Assert.True(condition());}
    private static string FindRepositoryRoot(){var directory=new DirectoryInfo(AppContext.BaseDirectory);while(directory is not null&&!Directory.Exists(Path.Combine(directory.FullName,"apps")))directory=directory.Parent;return directory?.FullName??throw new DirectoryNotFoundException();}

    private sealed class Factory(AsyncTransport transport):IModbusTransportFactory
    {
        public IModbusTransport Create(MonitoringOptions options){transport.CreateGeneration++;return transport;}
    }

    private sealed class AsyncTransport:IModbusTransport
    {
        private readonly TaskCompletionSource firstRealtime=new(TaskCreationOptions.RunContinuationsAsynchronously);private TaskCompletionSource? heldRealtime;private TaskCompletionSource? heldEntered;
        public bool IsOpen{get;private set;}public string Endpoint=>"memory://async";public int Fc16Count{get;private set;}public int CreateGeneration{get;set;}
        public Exception? FirstRealtimeError{get;init;}public Exception? FailNextRead{get;set;}public bool InvalidRealtime{get;set;}public int RealtimeCount{get;private set;}
        public void ReleaseFirstRealtime()=>firstRealtime.TrySetResult();
        public void HoldNextRealtime(){heldRealtime=new(TaskCreationOptions.RunContinuationsAsynchronously);heldEntered=new(TaskCreationOptions.RunContinuationsAsynchronously);}
        public Task WaitUntilHeldAsync()=>heldEntered!.Task.WaitAsync(TimeSpan.FromSeconds(2));
        public void ReleaseHeldRealtime()=>heldRealtime?.TrySetResult();
        public Task OpenAsync(CancellationToken cancellationToken){IsOpen=true;return Task.CompletedTask;}
        public Task CloseAsync(){IsOpen=false;firstRealtime.TrySetCanceled();return Task.CompletedTask;}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
        public async Task<ModbusExchangeResult> ExchangeAsync(byte unitId,ReadOnlyMemory<byte> pdu,CancellationToken cancellationToken)
        {
            await Task.Yield();var request=pdu.ToArray();if(request[0]==16){Fc16Count++;var write=new[]{(byte)16,request[1],request[2],request[3],request[4]};return new(write,request,write);}
            var address=(ushort)(request[1]<<8|request[2]);var count=(ushort)(request[3]<<8|request[4]);
            if(address==0){RealtimeCount++;await firstRealtime.Task.WaitAsync(cancellationToken);var held=heldRealtime;if(held is not null){heldEntered?.TrySetResult();await held.Task.WaitAsync(cancellationToken);heldRealtime=null;}if(FirstRealtimeError is not null)throw FirstRealtimeError;}
            if(FailNextRead is not null){var error=FailNextRead;FailNextRead=null;throw error;}
            var values=Read(address,count);var response=new byte[2+values.Length*2];response[0]=3;response[1]=(byte)(values.Length*2);
            for(var i=0;i<values.Length;i++){response[2+i*2]=(byte)(values[i]>>8);response[3+i*2]=(byte)values[i];}
            return new(response,request,response);
        }
        private ushort[] Read(ushort address,ushort count)
        {
            var baseline=Baseline();ushort[] source=address switch
            {
                0=>Realtime(),14=>[0x0104,0x050B],259=>[0],
                >=0x0100 and <=0x013F=>baseline.Skip(address-0x0100).Take(count).ToArray(),
                _=>new ushort[count]
            };
            return source.Take(count).Concat(Enumerable.Repeat((ushort)0,Math.Max(0,count-source.Length))).ToArray();
        }
        private ushort[] Realtime(){var values=new ushort[34];values[2]=3;values[3]=InvalidRealtime?(ushort)9:(ushort)1;values[4]=0x10;values[32]=0;values[33]=1;return values;}
        private static ushort[] Baseline(){var root=FindRepositoryRoot();return PersistenceBaselineContract.LoadFromRepository(root).Manifest.ActiveRegisters;}
    }
}
