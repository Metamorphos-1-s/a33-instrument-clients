using System.Text.Json;

namespace A33.Instrument.Core;

public enum PersistencePhase { Initial, PreflightComplete, TestValueAppliedRam, TestValueSaveRequested, TestValueSaveConfirmed, WaitingForFirstReboot, TestValuePersistenceVerified, OriginalValueAppliedRam, OriginalValueSaveRequested, OriginalValueSaveConfirmed, WaitingForSecondReboot, FinalRestorationVerified, Complete, ResultUncertain, Failed }
public interface IPersistenceWriteAuthorization { bool Allowed { get; } }
public sealed class DenyAllPersistenceAuthorization : IPersistenceWriteAuthorization { public bool Allowed => false; }
public sealed record PersistenceJournal(string WorkflowId, string ClientCommit, string Stm32Commit, int OriginalBrightness, int TestBrightness, PersistencePhase Phase, int SaveBudget, int SaveAttemptCount, ushort? TestSaveToken, ushort? RestoreSaveToken, bool ResultUncertain, string? FailureReason, DateTimeOffset UpdatedAt);
public sealed class PersistenceWorkflow(IPersistenceWriteAuthorization authorization)
{
 public PersistencePhase Phase { get; private set; } = PersistencePhase.Initial; public int SaveBudget { get; private set; } = 2; public int SaveAttemptCount { get; private set; }
 public bool CanSave => authorization.Allowed && SaveBudget > 0 && Phase is PersistencePhase.TestValueSaveRequested or PersistencePhase.OriginalValueSaveRequested;
 public void MarkPreflight(){Require(PersistencePhase.Initial);Phase=PersistencePhase.PreflightComplete;}
 public void MarkTestApplied(){Require(PersistencePhase.PreflightComplete);Phase=PersistencePhase.TestValueAppliedRam;}
 public void RequestTestSave(ushort token){Require(PersistencePhase.TestValueAppliedRam);Consume(token);Phase=PersistencePhase.TestValueSaveRequested;}
 public void ConfirmTestSave(){Require(PersistencePhase.TestValueSaveRequested);Phase=PersistencePhase.TestValueSaveConfirmed;}
 public void MarkTestRebootVerified(){Require(PersistencePhase.TestValueSaveConfirmed);Phase=PersistencePhase.TestValuePersistenceVerified;}
 public void MarkOriginalApplied(){Require(PersistencePhase.TestValuePersistenceVerified);Phase=PersistencePhase.OriginalValueAppliedRam;}
 public void RequestOriginalSave(ushort token){Require(PersistencePhase.OriginalValueAppliedRam);Consume(token);Phase=PersistencePhase.OriginalValueSaveRequested;}
 public void ConfirmOriginalSave(){Require(PersistencePhase.OriginalValueSaveRequested);Phase=PersistencePhase.OriginalValueSaveConfirmed;}
 public void MarkFinalRebootVerified(){Require(PersistencePhase.OriginalValueSaveConfirmed);Phase=PersistencePhase.FinalRestorationVerified;}
 public void Complete(){Require(PersistencePhase.FinalRestorationVerified);Phase=PersistencePhase.Complete;}
 public void MarkUncertain(string reason){Phase=PersistencePhase.ResultUncertain;throw new InvalidOperationException(reason);}
 private void Consume(ushort token){if(!authorization.Allowed)throw new UnauthorizedAccessException("Persistence authorization denied.");if(token==0||SaveBudget<=0||Phase==PersistencePhase.Complete)throw new InvalidOperationException("SAVE budget or state exhausted.");SaveBudget--;SaveAttemptCount++;}
 private void Require(PersistencePhase expected){if(Phase!=expected)throw new InvalidOperationException($"Invalid persistence transition {Phase} -> {expected}.");}
}
public static class PersistenceJournalStore
{
 public static async Task WriteAtomicAsync(string path, PersistenceJournal journal, CancellationToken ct=default){var dir=Path.GetDirectoryName(Path.GetFullPath(path))!;Directory.CreateDirectory(dir);var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";await using(var fs=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){await JsonSerializer.SerializeAsync(fs,journal,new JsonSerializerOptions{WriteIndented=true},ct);await fs.FlushAsync(ct);}File.Move(temp,path,true);}
 public static async Task<PersistenceJournal> ReadAsync(string path,CancellationToken ct=default){await using var fs=File.OpenRead(path);return await JsonSerializer.DeserializeAsync<PersistenceJournal>(fs,cancellationToken:ct)??throw new InvalidDataException("Persistence journal is empty.");}
}
