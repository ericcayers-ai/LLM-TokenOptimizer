using System.Collections.ObjectModel;
using TokenOptimizer.Sandbox;

namespace TokenOptimizer.App.ViewModels;

public sealed class SetupWizardViewModel : ViewModelBase
{
    private readonly PreflightGate _gate;

    public SetupWizardViewModel(PreflightGate gate) => _gate = gate;

    public event EventHandler? Completed;

    public ObservableCollection<string> LogItems { get; } = new();

    public IReadOnlyList<string> Log => LogItems;

    public async Task<bool> RunAsync(CancellationToken ct = default)
    {
        AppendLog("Checking sandbox prerequisites...");
        var result = await _gate.CheckAsync(ct);

        if (result.Ok)
        {
            AppendLog("Sandbox is ready.");
            RaiseCompleted();
            return true;
        }

        AppendLog($"Missing: {string.Join(", ", result.Missing)}");

        foreach (var step in result.Steps)
        {
            if (!await RunStepWithRetryAsync(step, ct))
            {
                AppendLog($"[{step.Id}] failed - setup incomplete.");
                return false;
            }
        }

        result = await _gate.CheckAsync(ct);
        if (!result.Ok)
        {
            AppendLog("Final check failed - sandbox is still not ready.");
            return false;
        }

        AppendLog("Sandbox is ready.");
        RaiseCompleted();
        return true;
    }

    private async Task<bool> RunStepWithRetryAsync(SetupStep step, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            AppendLog($"[{step.Id}] {step.Description}{(attempt > 1 ? " (retry)" : "")}...");
            try
            {
                if (await step.Execute(ct)) return true;
                AppendLog($"[{step.Id}] did not succeed.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppendLog($"[{step.Id}] error: {ex.Message}");
            }
        }
        return false;
    }

    private void AppendLog(string message) => LogItems.Add(message);

    private void RaiseCompleted() => Completed?.Invoke(this, EventArgs.Empty);
}
