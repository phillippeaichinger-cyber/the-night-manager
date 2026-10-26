using System.Collections.Concurrent;
using NightManager.Core.Contracts;

namespace NightManager.Core.Sequencing;

/// <summary>
/// The phone side of the sequence's Night Manager instructions: questions waiting for an answer, alarms waiting
/// to be acknowledged, and the pause / finish requests that Pause points and "Loop until finished" read.
/// Thread-safe; answers arrive on the sync thread while instructions wait on the sequencer's.
/// </summary>
public sealed class PhoneControl {
    private readonly ConcurrentDictionary<string, (PhonePrompt Prompt, TaskCompletionSource<string> Answer)> prompts = new();
    private readonly ConcurrentDictionary<string, (PhoneAlarm Alarm, TaskCompletionSource Acknowledged)> alarms = new();
    private readonly TimeProvider time;
    private volatile bool paused;
    private volatile bool finishRequested;
    private int holding;
    private TaskCompletionSource resumed = NewSignal();

    public PhoneControl(TimeProvider? time = null) {
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Anything the app should see changed (push a snapshot).</summary>
    public event EventHandler? Changed;

    /// <summary>Raised for requests from the phone, for the timeline (kind, title).</summary>
    public event EventHandler<(string Kind, string Title, string? Message)>? Requested;

    public bool Paused => paused;
    public bool FinishRequested => finishRequested;

    public PhoneState State => new() {
        Paused = paused,
        HoldingAtPause = Volatile.Read(ref holding) > 0,
        FinishRequested = finishRequested,
        Prompts = prompts.Values.Select(p => p.Prompt).OrderBy(p => p.AskedAt).ToList(),
        Alarms = alarms.Values.Select(a => a.Alarm).OrderBy(a => a.RaisedAt).ToList(),
    };

    // MARK: Questions

    public PhonePrompt Ask(string kind, string question, string? detail, string yes, string? no, TimeSpan? timeout, string defaultAnswer) {
        var now = time.GetUtcNow();
        var prompt = new PhonePrompt {
            Id = Guid.NewGuid().ToString("N")[..12],
            Kind = kind,
            Question = question,
            Detail = detail,
            Yes = yes,
            No = no,
            AskedAt = now,
            TimeoutAt = timeout is { } t && t > TimeSpan.Zero ? now + t : null,
            DefaultAnswer = defaultAnswer,
        };
        prompts[prompt.Id] = (prompt, new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
        Changed?.Invoke(this, EventArgs.Empty);
        return prompt;
    }

    /// <summary>Waits for the answer; on timeout returns the prompt's default. Answers are "yes", "no" or "go".</summary>
    public async Task<(string Answer, bool TimedOut)> WaitForAnswerAsync(string promptId, CancellationToken token) {
        if (!prompts.TryGetValue(promptId, out var entry)) {
            throw new InvalidOperationException("Unknown question.");
        }
        try {
            var wait = entry.Answer.Task;
            if (entry.Prompt.TimeoutAt is { } deadline) {
                var remaining = deadline - time.GetUtcNow();
                var timeout = Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, time, token);
                var done = await Task.WhenAny(wait, timeout).ConfigureAwait(false);
                if (done != wait) {
                    token.ThrowIfCancellationRequested();
                    return (entry.Prompt.DefaultAnswer, true);
                }
            } else {
                await wait.WaitAsync(token).ConfigureAwait(false);
            }
            return (await wait.ConfigureAwait(false), false);
        } finally {
            prompts.TryRemove(promptId, out _);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool Answer(string promptId, string answer) {
        var normalized = answer.Trim().ToLowerInvariant();
        if (normalized is not ("yes" or "no" or "go")) {
            throw new ArgumentException($"Unknown answer '{answer}'.");
        }
        return prompts.TryGetValue(promptId, out var entry) && entry.Answer.TrySetResult(normalized);
    }

    // MARK: Alarms

    public PhoneAlarm RaiseAlarm(string title, string? message) {
        var alarm = new PhoneAlarm { Id = Guid.NewGuid().ToString("N")[..12], Title = title, Message = message, RaisedAt = time.GetUtcNow() };
        alarms[alarm.Id] = (alarm, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        Changed?.Invoke(this, EventArgs.Empty);
        return alarm;
    }

    public bool IsActive(string alarmId) => alarms.ContainsKey(alarmId);

    public Task Acknowledged(string alarmId) => alarms.TryGetValue(alarmId, out var a) ? a.Acknowledged.Task : Task.CompletedTask;

    /// <summary>Acknowledges one alarm, or all of them when <paramref name="alarmId"/> is empty.</summary>
    public int Acknowledge(string? alarmId) {
        var ids = string.IsNullOrEmpty(alarmId) ? alarms.Keys.ToList() : [alarmId];
        var count = 0;
        foreach (var id in ids) {
            if (alarms.TryRemove(id, out var a)) {
                a.Acknowledged.TrySetResult();
                count++;
            }
        }
        if (count > 0) {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return count;
    }

    // MARK: Pause / finish

    public void Pause() {
        if (paused) {
            return;
        }
        paused = true;
        Interlocked.Exchange(ref resumed, NewSignal());
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Resume() {
        if (!paused) {
            return;
        }
        paused = false;
        Volatile.Read(ref resumed).TrySetResult();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RequestFinish() {
        finishRequested = true;
        Resume();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A Pause point: returns at once unless a pause was requested, then waits for resume (or finish).</summary>
    public async Task<bool> HoldIfPausedAsync(TimeSpan? maxWait, CancellationToken token) {
        if (!paused) {
            return false;
        }
        Interlocked.Increment(ref holding);
        Changed?.Invoke(this, EventArgs.Empty);
        try {
            var signal = Volatile.Read(ref resumed).Task;
            if (maxWait is { } limit && limit > TimeSpan.Zero) {
                await Task.WhenAny(signal, Task.Delay(limit, time, token)).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            } else {
                await signal.WaitAsync(token).ConfigureAwait(false);
            }
            return true;
        } finally {
            Interlocked.Decrement(ref holding);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A new sequence starts with no pause or finish left over from last night.</summary>
    public void ResetForNewSequence() {
        finishRequested = false;
        Resume();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // MARK: Commands

    /// <summary>Applies an answer / acknowledge / pause / resume / finish command; returns the result message.</summary>
    public string Handle(RemoteCommand command) {
        string Arg(string name) => command.Args is { } a && a.TryGetValue(name, out var v) ? v : string.Empty;
        var by = command.IssuedBy is { Length: > 0 } who ? $" ({who})" : "";
        switch (command.Type) {
            case CommandTypes.Answer:
                if (!Answer(Arg("promptId"), Arg("answer"))) {
                    throw new InvalidOperationException("That question has already been answered or timed out.");
                }
                Requested?.Invoke(this, (EventKinds.UserAnswered, $"Answered \"{Arg("answer")}\" from the app{by}", null));
                return "Answer received.";
            case CommandTypes.Acknowledge:
                var count = Acknowledge(Arg("alarmId"));
                if (count > 0) {
                    Requested?.Invoke(this, (EventKinds.UserAnswered, $"Alarm acknowledged{by}", null));
                }
                return count > 0 ? "Alarm stopped." : "No alarm was ringing.";
            case CommandTypes.Pause:
                Pause();
                Requested?.Invoke(this, (EventKinds.SequencePaused, $"Pause requested{by}", "The sequence holds at its next Pause point."));
                return "Pause requested: the sequence holds at its next Pause point.";
            case CommandTypes.Resume:
                Resume();
                Requested?.Invoke(this, (EventKinds.SequenceResumed, $"Resumed{by}", null));
                return "Resumed.";
            case CommandTypes.FinishNight:
                RequestFinish();
                Requested?.Invoke(this, (EventKinds.FinishRequested, $"Finish night requested{by}", "Imaging loops end after the current exposure; the end-of-sequence steps still run."));
                return "Finishing: loops end after the current step and the end-of-sequence steps run.";
            default:
                throw new InvalidOperationException($"Unsupported command {command.Type}.");
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
